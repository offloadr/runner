using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class DemandAwareModelHydrationCoordinatorTests
{
    // The coordinator tracks placeholders by Linux device and inode (statx); without them most
    // scenarios take different paths, so the class runs only where it can be meaningful.
    [SetUp]
    public void RequireLinux() => LinuxTestPrerequisites.RequireLinux();

    [Test]
    public void ModelTransferIdentity_IsDeterministicAndDestinationSensitive()
    {
        var root = CreateTempDirectory();
        try
        {
            var first = ModelTransferIdentity.Create("model-1", Path.Combine(root, "a.bin"), 1024);
            var same = ModelTransferIdentity.Create("model-1", Path.Combine(root, ".", "a.bin"), 1024);
            var otherDestination = ModelTransferIdentity.Create("model-1", Path.Combine(root, "b.bin"), 1024);

            Assert.Multiple(() =>
            {
                Assert.That(first.Digest, Has.Length.EqualTo(64));
                Assert.That(first.DeterministicIdentifier, Has.Length.EqualTo(16));
                Assert.That(first.DeterministicIdentifier, Does.Match("^[0-9a-f]{16}$"));
                Assert.That(first.DeterministicIdentifier, Is.EqualTo(first.Digest[..16]));
                Assert.That(same, Is.EqualTo(first));
                Assert.That(otherDestination.Digest, Is.Not.EqualTo(first.Digest));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void DecodeLoadedPieces_UsesMostSignificantBitForPieceZero()
    {
        var pieces = DemandAwareModelHydrationCoordinator.DecodeLoadedPieces("a180", 12);

        Assert.That(pieces, Is.EquivalentTo(new[] { 0, 2, 7, 8 }));
    }

    [Test]
    public void ModelTransferIndex_RejectsTamperedFullDigest()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "model.bin");
            var identity = ModelTransferIdentity.Create("model-1", destination, 8);
            var index = new ModelTransferIndex(Path.Combine(root, "state"));

            Assert.That(
                async () => await index.UpsertAsync(
                    new ModelTransferIndexRecord
                    {
                        IdentityDigest = new string('f', 64),
                        ModelId = identity.ModelId,
                        DestinationPath = identity.DestinationPath,
                        ExpectedLength = identity.ExpectedLength,
                        Handle = identity.DeterministicIdentifier,
                        TransferEpoch = 1
                    },
                    CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_DoesNotRunPausedTransfer_UntilRangeDemandExists()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "model.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var identity),
                Is.True);
            await Task.Delay(150);

            Assert.Multiple(() =>
            {
                Assert.That(opened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));
                Assert.That(opened.DeviceId, Is.EqualTo(identity.DeviceId));
                Assert.That(opened.Inode, Is.EqualTo(identity.Inode));
                Assert.That(new FileInfo(request.DestinationPath).Length, Is.EqualTo(request.SizeBytes));
                Assert.That(backend.LastCreateRequest!.StartPaused, Is.True);
                Assert.That(
                    backend.LastCreateRequest.ResumeMode,
                    Is.EqualTo(ModelTransferResumeMode.InitializeEmpty));
                Assert.That(backend.UnpausedHandles, Is.Empty);
            });

            var ensured = await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                offset: 0,
                length: 4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(ensured.TransferEpoch, Is.EqualTo(opened.TransferEpoch));
            Assert.That(backend.UnpausedHandles, Has.Count.EqualTo(1));
            Assert.That(backend.MoveToFrontHandles, Is.EqualTo(backend.UnpausedHandles));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CompletionPersistenceFailure_HoldsWaitersAndRetries()
    {
        var root = CreateTempDirectory();
        var indexDirectory = Path.Combine(root, "state", "model-transfers");
        try
        {
            var backend = new DemandBackend { CompleteOnUnpause = true };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "persist-before-complete.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.ReleaseAsync(opened.LeaseId);
            var statusCallsBeforeDemand = backend.StatusCallsByHandle.Values.Sum();

            Directory.Delete(indexDirectory, recursive: true);
            await File.WriteAllTextAsync(indexDirectory, "block index writes");

            var hydration = coordinator.EnsurePromptDownloadsAsync(
                "session-1",
                [request],
                CancellationToken.None);
            await WaitUntilAsync(
                () => backend.StatusCallsByHandle.Values.Sum() >= statusCallsBeforeDemand + 2,
                TimeSpan.FromSeconds(2));

            Assert.That(hydration.IsCompleted, Is.False);

            File.Delete(indexDirectory);
            Directory.CreateDirectory(indexDirectory);
            await hydration.WaitAsync(TimeSpan.FromSeconds(2));

            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var reloaded = new ModelTransferIndex(Path.Combine(root, "state"));
            Assert.That(reloaded.TryGet(identity.Digest, out var record), Is.True);
            Assert.That(record.Complete, Is.True);
        }
        finally
        {
            if (File.Exists(indexDirectory))
            {
                File.Delete(indexDirectory);
            }

            TryDelete(root);
        }
    }

    [Test]
    public async Task Scheduler_TransientStatusFailure_RetriesWithoutInvalidatingTransfer()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "transient-status.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            backend.StatusFailuresRemaining = 1;

            var ensured = await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    offset: 0,
                    length: 4,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));

            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var index = new ModelTransferIndex(Path.Combine(root, "state"));
            Assert.That(index.TryGet(identity.Digest, out var record), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(ensured.TransferEpoch, Is.EqualTo(opened.TransferEpoch));
                Assert.That(backend.CreateCallCount, Is.EqualTo(1));
                Assert.That(backend.RemovedHandles, Is.Empty);
                Assert.That(backend.StatusCallsByHandle[backend.CreatedHandle!.Value], Is.GreaterThanOrEqualTo(2));
                Assert.That(record.RestartRequired, Is.False);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_DescriptorLeaseIdsAreOpaqueCapabilitiesRatherThanSequentialIds()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "lease-capability.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opened = new List<ModelHydrationOpenResult>();
            for (var index = 0; index < 8; index++)
            {
                opened.Add(await coordinator.OpenAsync(
                    request.DestinationPath,
                    "session-1",
                    CancellationToken.None));
            }

            var leaseIds = opened.Select(static result => result.LeaseId).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(leaseIds.All(static leaseId => leaseId != 0), Is.True);
                Assert.That(leaseIds.Distinct().Count(), Is.EqualTo(leaseIds.Length));
                Assert.That(
                    leaseIds.Zip(leaseIds.Skip(1), static (current, next) =>
                        current != ulong.MaxValue && next == current + 1).Any(static sequential => sequential),
                    Is.False);
            });

            foreach (var result in opened)
            {
                await coordinator.ReleaseAsync(result.LeaseId);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task MultipleHeaderWaiters_ShareOneActiveStatusPoll()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "model.safetensors");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var statusCallsBeforeWaiters = backend.StatusCallsByHandle.Values.Single();

            // Allow exactly one status read: both waiters must be satisfied by it. Later
            // periodic polls of the now active transfer wait for a permit and are not counted.
            var statusPermits = new SemaphoreSlim(0);
            backend.StatusPermits = statusPermits;
            var first = coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None);
            var second = coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None);
            statusPermits.Release();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(
                backend.StatusCallsByHandle.Values.Single() - statusCallsBeforeWaiters,
                Is.EqualTo(1));
            backend.StatusPermits = null;
            statusPermits.Release(1_000);
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SelectedTransfer_IsReactivatedWhenBackendSettlesBackToPaused()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "reactivate.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            var handle = backend.CreatedHandle!.Value;
            backend.SetStatus(handle, "paused");

            await WaitUntilAsync(
                () => backend.UnpausedHandles.Count >= 2,
                TimeSpan.FromSeconds(2));

            Assert.That(backend.UnpausedHandles, Is.EqualTo(new[] { handle, handle }));
            Assert.That(backend.MoveToFrontHandles, Is.EqualTo(backend.UnpausedHandles));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task StatusPolling_DoesNotConsumeFutureDemandWakeups()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                BitfieldOnUnpause = "00"
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "wake.safetensors");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var handle = backend.CreatedHandle!.Value;

            var firstRange = coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None);
            await WaitUntilAsync(
                () => backend.StatusCallsByHandle.GetValueOrDefault(handle) >= 4,
                TimeSpan.FromSeconds(2));
            backend.SetBitfield(handle, "80", 4);
            await firstRange.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.PausedHandles.Contains(handle),
                TimeSpan.FromSeconds(2));

            backend.BitfieldOnUnpause = "c0";
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                4,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(backend.UnpausedHandles, Is.EqualTo(new[] { handle, handle }));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task TransitionalZeroLengthSnapshot_DefersLengthValidationUntilAria2LoadsMetadata()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                ZeroLengthStatusReadsRemaining = 1
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "metadata.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            var result = await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(result.TransferEpoch, Is.EqualTo(opened.TransferEpoch));
            Assert.That(backend.ZeroLengthStatusReadsRemaining, Is.Zero);
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CancellingOneRangeWaiter_DoesNotCancelAnother()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "cancel.safetensors");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var cancelledWaiter = coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                cancelled.Token);
            var survivingWaiter = coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None);

            Assert.That(
                async () => await cancelledWaiter,
                Throws.InstanceOf<OperationCanceledException>());
            await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(2));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ReleasingDescriptorLease_CancelsOnlyItsOutstandingWaiters()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                BitfieldOnUnpause = "00"
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "release.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var waiter = coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None);
            await WaitUntilAsync(
                () => backend.UnpausedHandles.Count > 0,
                TimeSpan.FromSeconds(2));

            await coordinator.ReleaseAsync(opened.LeaseId);

            Assert.That(
                async () => await waiter,
                Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task DescriptorLease_CannotBeUsedOrReleasedByAnotherSession()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "session-bound.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            Assert.That(
                async () => await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    0,
                    4,
                    CancellationToken.None,
                    requiredSessionId: "session-2"),
                Throws.InstanceOf<ModelHydrationIOException>());
            Assert.That(
                async () => await coordinator.EnsureCompleteAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    "mapping",
                    CancellationToken.None,
                    requiredSessionId: "session-2"),
                Throws.InstanceOf<ModelHydrationIOException>());

            await coordinator.ReleaseAsync(opened.LeaseId, requiredSessionId: "session-2");

            var ensured = await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    0,
                    4,
                    CancellationToken.None,
                    requiredSessionId: "session-1")
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(ensured.TransferEpoch, Is.EqualTo(opened.TransferEpoch));

            await coordinator.ReleaseAsync(opened.LeaseId, requiredSessionId: "session-1");
            Assert.That(
                async () => await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    0,
                    4,
                    CancellationToken.None),
                Throws.InstanceOf<ModelHydrationIOException>());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task DescriptorLeases_AreCappedPerSessionModelAndPerSession()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var modelCount = DemandAwareModelHydrationCoordinator.MaxDescriptorLeasesPerSession /
                             DemandAwareModelHydrationCoordinator.MaxDescriptorLeasesPerSessionModel + 1;
            var requests = Enumerable.Range(0, modelCount)
                .Select(index => CreateRequest(root, $"capped-{index}.bin"))
                .ToArray();
            coordinator.RegisterDownloads("session-1", requests, replaceExisting: true);

            var leases = new List<ulong>();
            for (var index = 0; index < DemandAwareModelHydrationCoordinator.MaxDescriptorLeasesPerSessionModel; index++)
            {
                leases.Add((await coordinator.OpenAsync(requests[0].DestinationPath, "session-1", CancellationToken.None)).LeaseId);
            }

            Assert.That(
                async () => await coordinator.OpenAsync(requests[0].DestinationPath, "session-1", CancellationToken.None),
                Throws.InstanceOf<ModelHydrationIOException>());
            var otherSession = await coordinator.OpenAsync(requests[0].DestinationPath, "session-2", CancellationToken.None);
            Assert.That(otherSession.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));

            await coordinator.ReleaseAsync(leases[^1]);
            leases.RemoveAt(leases.Count - 1);
            leases.Add((await coordinator.OpenAsync(requests[0].DestinationPath, "session-1", CancellationToken.None)).LeaseId);

            for (var model = 1; leases.Count < DemandAwareModelHydrationCoordinator.MaxDescriptorLeasesPerSession; model++)
            {
                for (var index = 0;
                     index < DemandAwareModelHydrationCoordinator.MaxDescriptorLeasesPerSessionModel &&
                     leases.Count < DemandAwareModelHydrationCoordinator.MaxDescriptorLeasesPerSession;
                     index++)
                {
                    leases.Add((await coordinator.OpenAsync(requests[model].DestinationPath, "session-1", CancellationToken.None)).LeaseId);
                }
            }

            Assert.That(
                async () => await coordinator.OpenAsync(requests[^1].DestinationPath, "session-1", CancellationToken.None),
                Throws.InstanceOf<ModelHydrationIOException>());

            await coordinator.ReleaseAsync(otherSession.LeaseId);
            foreach (var leaseId in leases)
            {
                await coordinator.ReleaseAsync(leaseId);
            }

            var reopened = await coordinator.OpenAsync(requests[^1].DestinationPath, "session-1", CancellationToken.None);
            Assert.That(reopened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));
            await coordinator.ReleaseAsync(reopened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RangeWaiters_PerLeaseAreCappedAndCancelledWaitersFreeCapacity()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                BitfieldOnUnpause = "00"
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "capped.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var other = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            using var firstWaiterCancellation = new CancellationTokenSource();
            var waiters = new List<Task<ModelHydrationEnsureResult>>
            {
                coordinator.EnsureRangeAsync(opened.LeaseId, opened.TransferEpoch, 0, 4, firstWaiterCancellation.Token)
            };
            for (var index = 1; index < DemandAwareModelHydrationCoordinator.MaxRangeWaitersPerLease; index++)
            {
                waiters.Add(coordinator.EnsureRangeAsync(opened.LeaseId, opened.TransferEpoch, 0, 4, CancellationToken.None));
            }

            Assert.That(waiters.Any(static waiter => waiter.IsCompleted), Is.False);
            Assert.That(
                async () => await coordinator.EnsureRangeAsync(opened.LeaseId, opened.TransferEpoch, 0, 4, CancellationToken.None),
                Throws.InstanceOf<ModelHydrationIOException>());
            Assert.That(
                async () => await coordinator.EnsureCompleteAsync(opened.LeaseId, opened.TransferEpoch, "mapping", CancellationToken.None),
                Throws.InstanceOf<ModelHydrationIOException>());

            // The cap is per descriptor; another lease on the same file is unaffected.
            var otherWaiter = coordinator.EnsureRangeAsync(other.LeaseId, other.TransferEpoch, 0, 4, CancellationToken.None);
            Assert.That(otherWaiter.IsFaulted, Is.False);

            await firstWaiterCancellation.CancelAsync();
            Assert.That(async () => await waiters[0], Throws.InstanceOf<OperationCanceledException>());
            var replacement = coordinator.EnsureRangeAsync(opened.LeaseId, opened.TransferEpoch, 0, 4, CancellationToken.None);
            Assert.That(replacement.IsFaulted, Is.False);

            await coordinator.ReleaseAsync(opened.LeaseId);
            await coordinator.ReleaseAsync(other.LeaseId);
            Assert.That(
                async () => await Task.WhenAll(waiters.Skip(1).Append(replacement).Append(otherWaiter)),
                Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task PromptDependencies_RunInDeclaredOrdinalOrder()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var first = CreateRequest(root, "first.bin");
            var second = CreateRequest(root, "second.bin");
            coordinator.RegisterDownloads(
                "session-1",
                [first, second],
                replaceExisting: true);

            await coordinator.EnsurePromptDownloadsAsync(
                "session-1",
                [first, second],
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var expectedOrder = new[]
            {
                ModelTransferIdentity.Create(first.ModelId, first.DestinationPath, first.SizeBytes)
                    .DeterministicIdentifier,
                ModelTransferIdentity.Create(second.ModelId, second.DestinationPath, second.SizeBytes)
                    .DeterministicIdentifier
            };
            Assert.That(
                backend.UnpausedHandles.Select(static handle => handle.ToString()),
                Is.EqualTo(expectedOrder));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task BlockedRangeWaiter_PreemptsAndThenResumesStickyContinuation()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var stickyRequest = CreateRequest(root, "sticky.bin");
            var rangeRequest = CreateRequest(root, "range.safetensors");
            coordinator.RegisterDownloads(
                "session-1",
                [stickyRequest, rangeRequest],
                replaceExisting: true);

            var stickyOpen = await coordinator.OpenAsync(
                stickyRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                stickyOpen.LeaseId,
                stickyOpen.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var rangeOpen = await coordinator.OpenAsync(
                rangeRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                rangeOpen.LeaseId,
                rangeOpen.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var stickyHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                        stickyRequest.ModelId,
                        stickyRequest.DestinationPath,
                        stickyRequest.SizeBytes)
                    .DeterministicIdentifier);
            var rangeHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                        rangeRequest.ModelId,
                        rangeRequest.DestinationPath,
                        rangeRequest.SizeBytes)
                    .DeterministicIdentifier);
            // Once the range is ready, the scheduler resumes the earlier sticky intent.
            // Wait for that transition rather than racing the background continuation.
            await WaitUntilAsync(
                () => backend.UnpausedHandles.Count >= 3,
                TimeSpan.FromSeconds(2));
            Assert.That(backend.UnpausedHandles, Is.EqualTo(new[] { stickyHandle, rangeHandle, stickyHandle }));
            Assert.That(backend.PausedHandles, Is.EqualTo(new[] { stickyHandle, rangeHandle }));
            await coordinator.ReleaseAsync(stickyOpen.LeaseId);
            await coordinator.ReleaseAsync(rangeOpen.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task BlockedRangeWaiter_RetriesTransientPauseFailureBeforePromotion()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var stickyRequest = CreateRequest(root, "sticky.bin");
            var rangeRequest = CreateRequest(root, "range.safetensors");
            coordinator.RegisterDownloads(
                "session-1",
                [stickyRequest, rangeRequest],
                replaceExisting: true);

            var stickyOpen = await coordinator.OpenAsync(
                stickyRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                stickyOpen.LeaseId,
                stickyOpen.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var stickyHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                        stickyRequest.ModelId,
                        stickyRequest.DestinationPath,
                        stickyRequest.SizeBytes)
                    .DeterministicIdentifier);
            backend.PauseFailuresRemaining[stickyHandle] = 1;

            var rangeOpen = await coordinator.OpenAsync(
                rangeRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                rangeOpen.LeaseId,
                rangeOpen.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(
                    backend.PauseAttempts.Count(handle => handle == stickyHandle),
                    Is.EqualTo(2));
                Assert.That(backend.PausedHandles.Count(handle => handle == stickyHandle), Is.EqualTo(1));
            });
            await coordinator.ReleaseAsync(stickyOpen.LeaseId);
            await coordinator.ReleaseAsync(rangeOpen.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task IdleStickyTransfer_RetriesTransientPauseFailure()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "idle.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var handle = new ModelTransferHandle(
                ModelTransferIdentity.Create(request.ModelId, request.DestinationPath, request.SizeBytes)
                    .DeterministicIdentifier);
            backend.PauseFailuresRemaining[handle] = 1;
            await coordinator.ReleaseAsync(opened.LeaseId);

            await WaitUntilAsync(
                () => backend.PauseAttempts.Count(attempt => attempt == handle) >= 2 &&
                      backend.PausedHandles.Contains(handle),
                TimeSpan.FromSeconds(2));
            Assert.That(backend.PausedHandles.Count(attempt => attempt == handle), Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ActiveStickyTransfer_PreemptsNewConventionalTransfer()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "sticky-external.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var externalHandle = new ModelTransferHandle("external-late");
            backend.ListedSnapshots =
            [
                TestModelTransferBackend.Snapshot(
                    externalHandle,
                    "active",
                    1,
                    8,
                    files:
                    [
                        new ModelTransferFileSnapshot(
                            Path.Combine(root, "conventional", "late.bin"),
                            8,
                            1,
                            ["https://models.example.test/late.bin"])
                    ])
            ];

            await WaitUntilAsync(
                () => backend.PausedHandles.Contains(externalHandle),
                TimeSpan.FromSeconds(2));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task VanishedActiveConventionalTransfer_DoesNotInvalidateManagedCandidate()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "managed-after-vanished-external.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var externalHandle = new ModelTransferHandle("external-vanished");
            backend.ListedSnapshots =
            [
                TestModelTransferBackend.Snapshot(
                    externalHandle,
                    "active",
                    1,
                    8,
                    files:
                    [
                        new ModelTransferFileSnapshot(
                            Path.Combine(root, "conventional", "vanished.bin"),
                            8,
                            1,
                            ["https://models.example.test/vanished.bin"])
                    ])
            ];
            backend.UnavailablePauseHandles.Add(externalHandle);

            var ensured = await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var managedHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                        request.ModelId,
                        request.DestinationPath,
                        request.SizeBytes)
                    .DeterministicIdentifier);
            Assert.Multiple(() =>
            {
                Assert.That(ensured.TransferEpoch, Is.EqualTo(opened.TransferEpoch));
                Assert.That(backend.PauseAttempts, Does.Contain(externalHandle));
                Assert.That(backend.PausedHandles, Does.Not.Contain(externalHandle));
                Assert.That(backend.UnpausedHandles, Does.Contain(managedHandle));
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RenewedSourceUri_IsReplacedBeforeTransferResumes()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "renew.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            var renewed = request.Clone();
            renewed.SourceUrl = "https://models.example.test/renew.bin?token=renewed";
            coordinator.RegisterDownloads("session-1", [renewed], replaceExisting: false);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(backend.UriChanges, Has.Count.EqualTo(1));
            Assert.That(
                backend.UriChanges[0].ReplacementUris,
                Is.EqualTo(new[] { renewed.SourceUrl }));
            Assert.That(
                backend.OperationOrder.IndexOf("change-uri"),
                Is.LessThan(backend.OperationOrder.IndexOf("unpause")));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    [TestCase(UriRefreshFailurePoint.Status)]
    [TestCase(UriRefreshFailurePoint.ChangeUri)]
    [TestCase(UriRefreshFailurePoint.SaveSession)]
    public async Task RenewedSourceUri_TransientRefreshFailure_RetriesWithoutRestart(
        UriRefreshFailurePoint failurePoint)
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "retry-renew.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var saveAttemptsBeforeRefresh = backend.SaveSessionAttempts;

            switch (failurePoint)
            {
                case UriRefreshFailurePoint.Status:
                    backend.StatusFailuresRemaining = 1;
                    break;
                case UriRefreshFailurePoint.ChangeUri:
                    backend.ChangeUriFailuresRemaining = 1;
                    break;
                case UriRefreshFailurePoint.SaveSession:
                    backend.SaveSessionFailuresRemaining = 1;
                    break;
                default:
                    Assert.Fail($"Unexpected URI refresh failure point '{failurePoint}'.");
                    break;
            }

            var renewed = request.Clone();
            renewed.SourceUrl = "https://models.example.test/retry-renew.bin?token=renewed";
            coordinator.RegisterDownloads("session-1", [renewed], replaceExisting: false);

            var ensured = await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    0,
                    4,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));

            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var index = new ModelTransferIndex(Path.Combine(root, "state"));
            Assert.That(index.TryGet(identity.Digest, out var record), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(ensured.TransferEpoch, Is.EqualTo(opened.TransferEpoch));
                Assert.That(backend.CreateCallCount, Is.EqualTo(1));
                Assert.That(backend.RemovedHandles, Is.Empty);
                Assert.That(backend.UriChanges, Has.Count.EqualTo(1));
                Assert.That(backend.UriChangeAttempts, Is.GreaterThanOrEqualTo(1));
                Assert.That(backend.SaveSessionAttempts, Is.GreaterThan(saveAttemptsBeforeRefresh));
                Assert.That(record.RestartRequired, Is.False);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RenewedSourceUri_MissingHandleReattachesFromControlState()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "reattach-renew.safetensors");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.PausedHandles.Contains(backend.CreatedHandle!.Value),
                TimeSpan.FromSeconds(2));
            await File.WriteAllTextAsync($"{request.DestinationPath}.aria2", "resume-state");

            var renewed = request.Clone();
            renewed.SourceUrl = "https://models.example.test/reattach-renew.safetensors?token=renewed";
            backend.UnavailableStatusFailuresRemaining = 2;
            backend.BitfieldOnUnpause = "c0";
            coordinator.RegisterDownloads("session-1", [renewed], replaceExisting: false);

            var ensured = await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    4,
                    4,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Multiple(() =>
            {
                Assert.That(ensured.TransferEpoch, Is.EqualTo(opened.TransferEpoch));
                Assert.That(backend.CreateCallCount, Is.EqualTo(2));
                Assert.That(backend.LastCreateRequest!.ResumeMode, Is.EqualTo(ModelTransferResumeMode.RequireExisting));
                Assert.That(backend.LastCreateRequest.SourceUris, Is.EqualTo(new[] { renewed.SourceUrl }));
                Assert.That(backend.RemovedHandles, Is.Empty);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RenewedMetalinkUri_IsReplacedBeforeTransferResumes()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "renew-metalink.bin");
            request.MetalinkXml = "<metalink><file name=\"model\"><resources><url>https://models.example.test/old</url></resources></file></metalink>";
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            var renewed = request.Clone();
            renewed.MetalinkXml = "<metalink><file name=\"model\"><resources><url>https://models.example.test/renewed</url></resources></file></metalink>";
            coordinator.RegisterDownloads("session-1", [renewed], replaceExisting: false);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(backend.UriChanges, Has.Count.EqualTo(1));
            Assert.That(
                backend.UriChanges[0].ReplacementUris,
                Is.EqualTo(new[] { "https://models.example.test/renewed" }));
            Assert.That(
                backend.OperationOrder.IndexOf("change-uri"),
                Is.LessThan(backend.OperationOrder.IndexOf("unpause")));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task OversizedSafetensorsHeader_DisablesOptimizationAndKeepsStickyHydration()
    {
        var root = CreateTempDirectory();
        try
        {
            var request = CreateRequest(root, "oversized.safetensors");
            var backend = new DemandBackend
            {
                BitfieldOnUnpause = "c0"
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                BitConverter.GetBytes(9UL * 1024UL * 1024UL));
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                8,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.StatusCallsByHandle[backend.CreatedHandle!.Value] > 1,
                TimeSpan.FromSeconds(5));

            Assert.That(
                backend.StatusCallsByHandle[backend.CreatedHandle!.Value],
                Is.GreaterThan(1));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ValidSafetensorsHeader_AllowsHeaderOnlyHydrationToPause()
    {
        var root = CreateTempDirectory();
        try
        {
            var request = CreateRequest(root, "valid-header.safetensors");
            request.SizeBytes = 12;
            var backend = new DemandBackend
            {
                BitfieldOnUnpause = "e0"
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                [2, 0, 0, 0, 0, 0, 0, 0, (byte)'{', (byte)'}', 0, 0]);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var statusCallsBeforeWaiter = backend.StatusCallsByHandle[backend.CreatedHandle!.Value];

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                10,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.PausedHandles.Contains(backend.CreatedHandle!.Value),
                TimeSpan.FromSeconds(2));

            Assert.That(
                backend.StatusCallsByHandle[backend.CreatedHandle!.Value] - statusCallsBeforeWaiter,
                Is.EqualTo(1));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task MalformedSafetensorsHeader_DisablesOptimizationAndKeepsStickyHydration()
    {
        var root = CreateTempDirectory();
        try
        {
            var request = CreateRequest(root, "malformed-header.safetensors");
            request.SizeBytes = 12;
            var backend = new DemandBackend
            {
                BitfieldOnUnpause = "e0"
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                [2, 0, 0, 0, 0, 0, 0, 0, (byte)'x', (byte)'x', 0, 0]);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                10,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.StatusCallsByHandle[backend.CreatedHandle!.Value] > 1,
                TimeSpan.FromSeconds(2));

            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task EnsureRange_FutureObservedEpoch_FailsClosed()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "future.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            var exception = Assert.ThrowsAsync<ModelHydrationIOException>(async () =>
                await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch + 1,
                    0,
                    1,
                    CancellationToken.None));

            Assert.That(exception!.Message, Does.Contain("future transfer epoch"));
            Assert.That(backend.UnpausedHandles, Is.Empty);
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CompletedPieceRegression_FailsUnsatisfiedReadWithIoError()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "immutable.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            backend.SetBitfield(backend.CreatedHandle!.Value, "00", completedLength: 0);
            var exception = Assert.ThrowsAsync<ModelHydrationIOException>(async () =>
                await coordinator.EnsureRangeAsync(
                    opened.LeaseId,
                    opened.TransferEpoch,
                    4,
                    4,
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.That(exception!.Message, Does.Contain("invalidated a piece"));
            await coordinator.ReleaseAsync(opened.LeaseId);
            var reopened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            Assert.That(reopened.TransferEpoch, Is.GreaterThan(opened.TransferEpoch));
            Assert.That(backend.RemovedHandles, Is.Not.Empty);
            await coordinator.ReleaseAsync(reopened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterDownloads_ConflictingPersistentIdentity_FailsClosedAndQuarantines()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "collision.bin");
            var conflictingIdentity = ModelTransferIdentity.Create(
                "different-model",
                request.DestinationPath,
                request.SizeBytes);
            var requestedIdentity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            await File.WriteAllTextAsync($"{request.DestinationPath}.aria2", "control");
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = conflictingIdentity.Digest,
                    ModelId = conflictingIdentity.ModelId,
                    DestinationPath = conflictingIdentity.DestinationPath,
                    ExpectedLength = conflictingIdentity.ExpectedLength,
                    Handle = requestedIdentity.DeterministicIdentifier,
                    TransferEpoch = 1,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true
                },
                CancellationToken.None);

            var backend = new DemandBackend
            {
                RemoveFailuresRemaining = 1
            };
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                coordinator.RegisterDownloads("session-1", [request], replaceExisting: true));
            await WaitUntilAsync(
                () => Directory.EnumerateFiles(
                        Path.Combine(stateDirectory, "model-transfers", "quarantine"),
                        "*.json")
                    .Any(),
                TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Message, Does.Contain("identity conflict"));
                Assert.That(
                    backend.RemovedHandles.Select(static handle => handle.ToString()),
                    Does.Contain(requestedIdentity.DeterministicIdentifier));
                Assert.That(backend.RemoveAttempts, Is.EqualTo(2));
                Assert.That(File.Exists(request.DestinationPath), Is.False);
                Assert.That(File.Exists($"{request.DestinationPath}.aria2"), Is.False);
            });

            Assert.That(
                () => coordinator.RegisterDownloads(
                    "session-1",
                    [request],
                    replaceExisting: true),
                Throws.Nothing);
            Assert.That(new FileInfo(request.DestinationPath).Length, Is.EqualTo(request.SizeBytes));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterDownloads_ConflictingOwnedMissingTarget_RemovesControlState()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "missing-conflict.bin");
            var conflictingIdentity = ModelTransferIdentity.Create(
                "different-model",
                request.DestinationPath,
                request.SizeBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            File.Delete(request.DestinationPath);
            await File.WriteAllTextAsync($"{request.DestinationPath}.aria2", "control");
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = conflictingIdentity.Digest,
                    ModelId = conflictingIdentity.ModelId,
                    DestinationPath = conflictingIdentity.DestinationPath,
                    ExpectedLength = conflictingIdentity.ExpectedLength,
                    Handle = conflictingIdentity.DeterministicIdentifier,
                    TransferEpoch = 1,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true
                },
                CancellationToken.None);

            var backend = new DemandBackend();
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);

            Assert.Throws<InvalidOperationException>(() =>
                coordinator.RegisterDownloads("session-1", [request], replaceExisting: true));
            await WaitUntilAsync(
                () => Directory.EnumerateFiles(
                        Path.Combine(stateDirectory, "model-transfers", "quarantine"),
                        "*.json")
                    .Any(),
                TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(request.DestinationPath), Is.False);
                Assert.That(File.Exists($"{request.DestinationPath}.aria2"), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task DisposeAsync_StalledFinalPauseHonorsShutdownCleanupTimeout()
    {
        var root = CreateTempDirectory();
        var pauseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePause = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DemandBackend
        {
            PauseAttemptStarted = pauseStarted,
            PauseAttemptRelease = releasePause.Task
        };
        var coordinator = CreateCoordinator(
            backend,
            root,
            TimeSpan.FromMilliseconds(100));
        var disposed = false;
        try
        {
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "shutdown-timeout.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var startedAt = DateTime.UtcNow;
            await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            disposed = true;

            Assert.Multiple(() =>
            {
                Assert.That(pauseStarted.Task.IsCompleted, Is.True);
                Assert.That(DateTime.UtcNow - startedAt, Is.LessThan(TimeSpan.FromSeconds(1)));
            });
        }
        finally
        {
            releasePause.TrySetResult();
            if (!disposed)
            {
                await coordinator.DisposeAsync();
            }

            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterDownloads_ConflictDuringOpenReservation_RejectsOpenBeforeRemoval()
    {
        var root = CreateTempDirectory();
        var releaseStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var existingRequest = CreateRequest(root, "reserved-conflict.bin");
            var existingIdentity = ModelTransferIdentity.Create(
                existingRequest.ModelId,
                existingRequest.DestinationPath,
                existingRequest.SizeBytes);
            var existingHandle = new ModelTransferHandle(existingIdentity.DeterministicIdentifier);
            Directory.CreateDirectory(Path.GetDirectoryName(existingRequest.DestinationPath)!);
            await File.WriteAllBytesAsync(
                existingRequest.DestinationPath,
                new byte[checked((int)existingRequest.SizeBytes)]);
            Assert.That(
                LinuxFileIdentityReader.TryRead(existingRequest.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = existingIdentity.Digest,
                    ModelId = existingIdentity.ModelId,
                    DestinationPath = existingIdentity.DestinationPath,
                    ExpectedLength = existingIdentity.ExpectedLength,
                    Handle = existingHandle.Value,
                    TransferEpoch = 1,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true,
                    PieceLength = 4,
                    NumPieces = 2
                },
                CancellationToken.None);

            var statusStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var removalStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                StatusAttemptStarted = statusStarted,
                StatusAttemptRelease = releaseStatus.Task,
                RemoveAttemptStarted = removalStarted,
                RemoveAttemptRelease = releaseRemoval.Task
            };
            backend.SetSnapshot(TestModelTransferBackend.Snapshot(
                existingHandle,
                "paused",
                completedLength: 0,
                totalLength: existingRequest.SizeBytes,
                bitfield: "00",
                pieceLength: 4,
                numPieces: 2,
                files:
                [
                    new ModelTransferFileSnapshot(
                        existingRequest.DestinationPath,
                        existingRequest.SizeBytes,
                        0,
                        [existingRequest.SourceUrl])
                ]));
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads(
                "session-1",
                [existingRequest],
                replaceExisting: true);

            var open = coordinator.OpenAsync(
                existingRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            await statusStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var replacementRequest = existingRequest.Clone();
            replacementRequest.ModelId = "replacement-model";
            Assert.That(
                () => coordinator.RegisterDownloads(
                    "session-2",
                    [replacementRequest],
                    replaceExisting: true),
                Throws.InvalidOperationException);

            releaseStatus.TrySetResult();
            var openException = Assert.ThrowsAsync<ModelHydrationIOException>(
                async () => await open.WaitAsync(TimeSpan.FromSeconds(2)));
            await removalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(openException!.Message, Does.Contain("identity-conflict quarantine"));
                Assert.That(File.Exists(existingRequest.DestinationPath), Is.True);
            });

            releaseRemoval.TrySetResult();
            await WaitUntilAsync(
                () => !File.Exists(existingRequest.DestinationPath),
                TimeSpan.FromSeconds(2));
            Assert.That(backend.RemovedHandles, Does.Contain(existingHandle));
        }
        finally
        {
            releaseStatus.TrySetResult();
            releaseRemoval.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterDownloads_ReclaimedConflictingIdentity_CancelsQueuedQuarantine()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var requested = CreateRequest(root, "reclaimed-conflict.bin");
            var reclaimed = requested.Clone();
            reclaimed.ModelId = "reclaimed-model";
            var reclaimedIdentity = ModelTransferIdentity.Create(
                reclaimed.ModelId,
                reclaimed.DestinationPath,
                reclaimed.SizeBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(requested.DestinationPath)!);
            await File.WriteAllBytesAsync(
                requested.DestinationPath,
                new byte[checked((int)requested.SizeBytes)]);
            await File.WriteAllTextAsync($"{requested.DestinationPath}.aria2", "control");
            Assert.That(
                LinuxFileIdentityReader.TryRead(requested.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = reclaimedIdentity.Digest,
                    ModelId = reclaimedIdentity.ModelId,
                    DestinationPath = reclaimedIdentity.DestinationPath,
                    ExpectedLength = reclaimedIdentity.ExpectedLength,
                    Handle = reclaimedIdentity.DeterministicIdentifier,
                    TransferEpoch = 1,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true
                },
                CancellationToken.None);
            var retryPauseStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetryPause = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var reclaimedHandle = new ModelTransferHandle(
                reclaimedIdentity.DeterministicIdentifier);
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        reclaimedHandle,
                        "active",
                        completedLength: 0,
                        totalLength: reclaimed.SizeBytes,
                        bitfield: "00",
                        pieceLength: 4,
                        numPieces: 2,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                reclaimed.DestinationPath,
                                reclaimed.SizeBytes,
                                0,
                                [reclaimed.SourceUrl])
                        ])
                ],
                PauseAttemptStarted = retryPauseStarted,
                PauseAttemptRelease = releaseRetryPause.Task
            };
            backend.PauseFailuresRemaining[reclaimedHandle] = 1;
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            await retryPauseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            try
            {
                Assert.That(
                    () => coordinator.RegisterDownloads(
                        "session-1",
                        [requested],
                        replaceExisting: true),
                    Throws.InvalidOperationException);
                coordinator.RegisterDownloads("session-2", [reclaimed], replaceExisting: true);
            }
            finally
            {
                releaseRetryPause.TrySetResult();
            }

            await Task.Delay(250);

            var reloaded = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(backend.RemovedHandles, Is.Empty);
                Assert.That(File.Exists(requested.DestinationPath), Is.True);
                Assert.That(File.Exists($"{requested.DestinationPath}.aria2"), Is.True);
                Assert.That(reloaded.TryGet(reclaimedIdentity.Digest, out _), Is.True);
                Assert.That(
                    Directory.EnumerateFiles(
                        Path.Combine(stateDirectory, "model-transfers", "quarantine"),
                        "*.json"),
                    Is.Empty);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_IndexedCompleteFileWithMissingBackendHandle_ReusesVerifiedFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "completed.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, "complete"u8.ToArray());
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 3,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true,
                    Complete = true
                },
                CancellationToken.None);
            var backend = new DemandBackend();
            backend.UnavailableStatusHandles.Add(handle);
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var ensured = await coordinator.EnsureCompleteAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                "test",
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(opened.TransferEpoch, Is.EqualTo(3));
                Assert.That(ensured.TransferEpoch, Is.EqualTo(3));
                Assert.That(backend.LastCreateRequest, Is.Null);
                Assert.That(File.ReadAllText(request.DestinationPath), Is.EqualTo("complete"));
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_CompleteSnapshotPersistsCompletionDiscoveredDuringReattachment()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "reattached-complete.bin");
            var backend = new DemandBackend();
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            backend.SetSnapshot(TestModelTransferBackend.Snapshot(
                handle,
                "complete",
                request.SizeBytes,
                request.SizeBytes,
                bitfield: "c0",
                pieceLength: 4,
                numPieces: 2,
                files:
                [
                    new ModelTransferFileSnapshot(
                        request.DestinationPath,
                        request.SizeBytes,
                        request.SizeBytes,
                        [request.SourceUrl])
                ]));

            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            var reloaded = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(reloaded.TryGet(identity.Digest, out var record), Is.True);
                Assert.That(record.Complete, Is.True);
                Assert.That(backend.LastCreateRequest, Is.Null);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ReattachedTransfer_UnknownSnapshotGeometryDoesNotInvalidateLoadedPieces()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "reattached-unknown-geometry.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 3,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true
                },
                CancellationToken.None);
            var backend = new DemandBackend();
            backend.SetSnapshot(TestModelTransferBackend.Snapshot(
                handle,
                "paused",
                4,
                request.SizeBytes,
                bitfield: "80",
                pieceLength: 4,
                numPieces: 2,
                files:
                [
                    new ModelTransferFileSnapshot(
                        request.DestinationPath,
                        request.SizeBytes,
                        4,
                        [request.SourceUrl])
                ]));
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            backend.ZeroGeometryStatusReadsRemaining = 1;
            backend.SetSnapshot(TestModelTransferBackend.Snapshot(
                handle,
                "complete",
                request.SizeBytes,
                request.SizeBytes,
                bitfield: "c0",
                pieceLength: 4,
                numPieces: 2,
                files:
                [
                    new ModelTransferFileSnapshot(
                        request.DestinationPath,
                        request.SizeBytes,
                        request.SizeBytes,
                        [request.SourceUrl])
                ]));

            var ensured = await coordinator.EnsureCompleteAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                "reattachment-test",
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            var reloaded = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(ensured.TransferEpoch, Is.EqualTo(opened.TransferEpoch));
                Assert.That(reloaded.TryGet(identity.Digest, out var record), Is.True);
                Assert.That(record.PieceLength, Is.EqualTo(4));
                Assert.That(record.NumPieces, Is.EqualTo(2));
                Assert.That(record.Complete, Is.True);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_ThroughSymlinkedModelsDirectory_UsesCanonicalRegisteredState()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var persistentRoot = Path.Combine(root, "persistent-models");
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(persistentRoot);
            Directory.CreateDirectory(sessionRoot);
            var sessionModels = Path.Combine(sessionRoot, "models");
            Directory.CreateSymbolicLink(sessionModels, persistentRoot);
            var request = CreateRequest(root, "symlinked.bin");
            request.DestinationPath = Path.Combine(persistentRoot, request.Filename);
            var backend = new DemandBackend();
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [persistentRoot]),
                Path.Combine(root, "state"));
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opened = await coordinator.OpenAsync(
                Path.Combine(sessionModels, request.Filename),
                "session-1",
                CancellationToken.None);

            Assert.That(opened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterDownloads_PersistsPlaceholderOwnershipBeforeFirstAttachment()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "registered-only.bin");
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var backend = new DemandBackend();
            await using (var firstCoordinator = new DemandAwareModelHydrationCoordinator(
                             backend,
                             new ModelHydrationRuntimeCapabilities(
                                 "synthetic-editor",
                                 SupportsPersistentRangeHydration: true,
                                 PersistentModelRoots: [root]),
                             stateDirectory))
            {
                await firstCoordinator.InitializeAsync(CancellationToken.None);
                firstCoordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

                var persisted = new ModelTransferIndex(stateDirectory);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.TryGet(identity.Digest, out var record), Is.True);
                    Assert.That(record.PlaceholderOwned, Is.True);
                    Assert.That(record.Inode, Is.Not.Zero);
                    Assert.That(record.Handle, Is.EqualTo(identity.DeterministicIdentifier));
                });
            }

            await using var secondCoordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await secondCoordinator.InitializeAsync(CancellationToken.None);
            secondCoordinator.RegisterDownloads("session-2", [request], replaceExisting: true);

            var opened = await secondCoordinator.OpenAsync(
                request.DestinationPath,
                "session-2",
                CancellationToken.None);

            Assert.That(opened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));
            Assert.That(backend.LastCreateRequest, Is.Not.Null);
            await secondCoordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task CandidateActivationFailure_RetriesWithoutRestartingEitherTransfer()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var previousRequest = CreateRequest(root, "previous.bin");
            var candidateRequest = CreateRequest(root, "candidate.safetensors");
            coordinator.RegisterDownloads(
                "session-1",
                [previousRequest, candidateRequest],
                replaceExisting: true);
            var previousOpen = await coordinator.OpenAsync(
                previousRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                previousOpen.LeaseId,
                previousOpen.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            var previousHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                        previousRequest.ModelId,
                        previousRequest.DestinationPath,
                        previousRequest.SizeBytes)
                    .DeterministicIdentifier);

            var candidateOpen = await coordinator.OpenAsync(
                candidateRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            var candidateHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                        candidateRequest.ModelId,
                        candidateRequest.DestinationPath,
                        candidateRequest.SizeBytes)
                    .DeterministicIdentifier);
            backend.MoveFailuresRemaining[candidateHandle] = 1;

            var ensured = await coordinator.EnsureRangeAsync(
                    candidateOpen.LeaseId,
                    candidateOpen.TransferEpoch,
                    0,
                    4,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.UnpausedHandles.Count(handle => handle == previousHandle) >= 2,
                TimeSpan.FromSeconds(2));

            await coordinator.ReleaseAsync(previousOpen.LeaseId);
            await coordinator.ReleaseAsync(candidateOpen.LeaseId);
            var reopenedPrevious = await coordinator.OpenAsync(
                previousRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            var reopenedCandidate = await coordinator.OpenAsync(
                candidateRequest.DestinationPath,
                "session-1",
                CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(ensured.TransferEpoch, Is.EqualTo(candidateOpen.TransferEpoch));
                Assert.That(reopenedPrevious.TransferEpoch, Is.EqualTo(previousOpen.TransferEpoch));
                Assert.That(reopenedCandidate.TransferEpoch, Is.EqualTo(candidateOpen.TransferEpoch));
                Assert.That(backend.MoveToFrontHandles.Count(handle => handle == candidateHandle), Is.GreaterThanOrEqualTo(2));
                Assert.That(backend.RemovedHandles, Is.Empty);
            });
            await coordinator.ReleaseAsync(reopenedPrevious.LeaseId);
            await coordinator.ReleaseAsync(reopenedCandidate.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ProgressReporterFailure_DoesNotRestartHealthyTransfer()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true
            };
            await using var coordinator = CreateCoordinator(backend, root);
            coordinator.ProgressReporter = _ => throw new IOException("simulated progress failure");
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "progress-failure.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                request.SizeBytes,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(backend.RemovedHandles, Is.Empty);
                Assert.That(backend.CreateCallCount, Is.EqualTo(1));
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RangeDemand_PreemptsAndThenResumesActiveConventionalTransfer()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "managed.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var externalHandle = new ModelTransferHandle("1111111111111111");
            backend.ListedSnapshots =
            [
                TestModelTransferBackend.Snapshot(
                    externalHandle,
                    "active",
                    1,
                    0,
                    files:
                    [
                        new ModelTransferFileSnapshot(
                            Path.Combine(root, "conventional", "unknown.bin"),
                            0,
                            1,
                            ["https://models.example.test/unknown.bin"])
                    ])
            ];

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.UnpausedHandles.Contains(externalHandle),
                TimeSpan.FromSeconds(2));

            var managedHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                        request.ModelId,
                        request.DestinationPath,
                        request.SizeBytes)
                    .DeterministicIdentifier);
            Assert.Multiple(() =>
            {
                Assert.That(backend.PausedHandles, Does.Contain(externalHandle));
                Assert.That(backend.UnpausedHandles, Does.Contain(managedHandle));
                Assert.That(
                    backend.UnpausedHandles.IndexOf(managedHandle),
                    Is.LessThan(backend.UnpausedHandles.IndexOf(externalHandle)));
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ConventionalTransferResumeFailure_IsRetriedWithoutNewDemand()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "managed-retry.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var externalHandle = new ModelTransferHandle("2222222222222222");
            backend.ListedSnapshots =
            [
                TestModelTransferBackend.Snapshot(
                    externalHandle,
                    "active",
                    1,
                    0,
                    files:
                    [
                        new ModelTransferFileSnapshot(
                            Path.Combine(root, "conventional", "retry.bin"),
                            0,
                            1,
                            ["https://models.example.test/retry.bin"])
                    ])
            ];
            backend.UnpauseFailuresRemaining[externalHandle] = 1;

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.UnpausedHandles.Contains(externalHandle),
                TimeSpan.FromSeconds(2));

            Assert.That(
                backend.UnpauseAttempts.Count(handle => handle == externalHandle),
                Is.EqualTo(2));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task ConventionalTransferRemovedWhilePreempted_IsDroppedWithoutRetryLoop()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "managed-removed-external.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            var externalHandle = new ModelTransferHandle("3333333333333333");
            backend.ListedSnapshots =
            [
                TestModelTransferBackend.Snapshot(
                    externalHandle,
                    "active",
                    1,
                    0,
                    files:
                    [
                        new ModelTransferFileSnapshot(
                            Path.Combine(root, "conventional", "removed.bin"),
                            0,
                            1,
                            ["https://models.example.test/removed.bin"])
                    ])
            ];
            backend.RemoveBeforeUnpauseHandles.Add(externalHandle);

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => backend.UnpauseAttempts.Contains(externalHandle),
                TimeSpan.FromSeconds(2));
            await Task.Delay(300);

            Assert.That(
                backend.UnpauseAttempts.Count(handle => handle == externalHandle),
                Is.EqualTo(1));
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_ReplacedCompletedTargetWithoutLeases_StartsNewEpoch()
    {
        var root = CreateTempDirectory();
        try
        {
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "replaced-complete.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var firstOpen = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                firstOpen.LeaseId,
                firstOpen.TransferEpoch,
                0,
                request.SizeBytes,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await coordinator.ReleaseAsync(firstOpen.LeaseId);

            var replacementPath = $"{request.DestinationPath}.replacement";
            await File.WriteAllBytesAsync(
                replacementPath,
                new byte[checked((int)request.SizeBytes)]);
            File.Delete(request.DestinationPath);
            File.Move(replacementPath, request.DestinationPath);
            var reopened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(reopened.TransferEpoch, Is.GreaterThan(firstOpen.TransferEpoch));
                Assert.That(backend.CreateCallCount, Is.EqualTo(2));
                Assert.That(backend.RemovedHandles, Does.Contain(backend.CreatedHandle!.Value));
                Assert.That(new FileInfo(request.DestinationPath).Length, Is.EqualTo(request.SizeBytes));
            });
            await coordinator.ReleaseAsync(reopened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task PromptHydration_ReplacedTargetWithoutLeases_RestartsBeforeUsingBitfield()
    {
        var root = CreateTempDirectory();
        using var promptCancellation = new CancellationTokenSource();
        try
        {
            var backend = new DemandBackend();
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "replaced-prompt.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var handle = new ModelTransferHandle(
                ModelTransferIdentity.Create(request.ModelId, request.DestinationPath, request.SizeBytes)
                    .DeterministicIdentifier);

            var prompt = coordinator.EnsurePromptDownloadsAsync(
                "session-1",
                [request],
                promptCancellation.Token);
            await WaitUntilAsync(
                () => backend.CreateCallCount == 1 && backend.UnpausedHandles.Contains(handle),
                TimeSpan.FromSeconds(2));

            var replacementPath = $"{request.DestinationPath}.replacement";
            await File.WriteAllBytesAsync(
                replacementPath,
                new byte[checked((int)request.SizeBytes)]);
            Assert.That(
                LinuxFileIdentityReader.TryRead(replacementPath, out var replacementIdentity),
                Is.True);
            File.Delete(request.DestinationPath);
            File.Move(replacementPath, request.DestinationPath);
            using var replacementLease = new FileStream(
                request.DestinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            await WaitUntilAsync(
                () => backend.CreateCallCount >= 2 || prompt.IsCompleted,
                TimeSpan.FromSeconds(2));
            if (prompt.IsCompleted)
            {
                await prompt;
            }

            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var restartedIdentity),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(restartedIdentity.Inode, Is.Not.EqualTo(replacementIdentity.Inode));
                Assert.That(backend.RemovedHandles, Does.Contain(handle));
                Assert.That(prompt.IsCompleted, Is.False);
            });

            promptCancellation.Cancel();
            Assert.That(
                async () => await prompt,
                Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            promptCancellation.Cancel();
            TryDelete(root);
        }
    }

    [Test]
    public async Task InodeReset_DuringOpenReservation_IsSerializedByAttachmentGate()
    {
        var root = CreateTempDirectory();
        var releaseStatus = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemoval = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var promptCancellation = new CancellationTokenSource();
        try
        {
            var statusStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var removalStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                CompleteOnUnpause = true,
                RemoveAttemptStarted = removalStarted,
                RemoveAttemptRelease = releaseRemoval.Task
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            var request = CreateRequest(root, "reserved-inode-reset.bin");
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var firstOpen = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.ReleaseAsync(firstOpen.LeaseId);
            backend.StatusAttemptStarted = statusStarted;
            backend.StatusAttemptRelease = releaseStatus.Task;

            var prompt = coordinator.EnsurePromptDownloadsAsync(
                "session-1",
                [request],
                promptCancellation.Token);
            await statusStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var replacementPath = $"{request.DestinationPath}.replacement";
            await File.WriteAllBytesAsync(
                replacementPath,
                new byte[checked((int)request.SizeBytes)]);
            File.Move(replacementPath, request.DestinationPath, overwrite: true);

            var opening = coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await removalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            releaseStatus.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            Assert.That(backend.RemoveAttempts, Is.EqualTo(1));

            releaseRemoval.TrySetResult();
            var reopened = await opening.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(reopened.TransferEpoch, Is.GreaterThan(firstOpen.TransferEpoch));
                Assert.That(backend.CreateCallCount, Is.EqualTo(2));
                Assert.That(backend.RemoveAttempts, Is.EqualTo(1));
            });
            await coordinator.ReleaseAsync(reopened.LeaseId);

            promptCancellation.Cancel();
            var promptException = Assert.CatchAsync(async () => await prompt);
            Assert.That(
                promptException,
                Is.InstanceOf<OperationCanceledException>()
                    .Or.InstanceOf<ModelHydrationIOException>());
        }
        finally
        {
            promptCancellation.Cancel();
            releaseStatus.TrySetResult();
            releaseRemoval.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_CompletedOrphanPreservesIndexForLaterRegistration()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "cached.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, "complete"u8.ToArray());
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 2,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true,
                    Complete = true
                },
                CancellationToken.None);
            var backend = new DemandBackend();
            backend.UnavailableStatusHandles.Add(handle);
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(handle),
                TimeSpan.FromSeconds(2));

            var afterCleanup = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(afterCleanup.TryGet(identity.Digest, out var preserved), Is.True);
                Assert.That(preserved.Complete, Is.True);
                Assert.That(File.Exists(request.DestinationPath), Is.True);
            });

            coordinator.RegisterDownloads("session-2", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-2",
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(opened.TransferEpoch, Is.EqualTo(2));
                Assert.That(backend.LastCreateRequest, Is.Null);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_TransientOrphanCleanupFailure_IsRetried()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "partial.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await using (var placeholder = File.Create(request.DestinationPath))
            {
                placeholder.SetLength(request.SizeBytes);
            }

            await File.WriteAllTextAsync($"{request.DestinationPath}.aria2", "partial");
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 1,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true,
                    Complete = false
                },
                CancellationToken.None);
            var backend = new DemandBackend
            {
                RemoveFailuresRemaining = 1
            };
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            await WaitUntilAsync(
                () => backend.RemoveAttempts >= 2 &&
                      !File.Exists(Path.Combine(
                          stateDirectory,
                          "model-transfers",
                          $"{identity.Digest}.json")),
                TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(backend.RemovedHandles, Does.Contain(handle));
                Assert.That(File.Exists(request.DestinationPath), Is.False);
                Assert.That(File.Exists($"{request.DestinationPath}.aria2"), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_MissingOrphanPlaceholder_RemovesResumeControlState()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "missing-orphan.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await using (var placeholder = File.Create(request.DestinationPath))
            {
                placeholder.SetLength(request.SizeBytes);
            }

            var controlPath = $"{request.DestinationPath}.aria2";
            await File.WriteAllTextAsync(controlPath, "partial");
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 1,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true,
                    Complete = false
                },
                CancellationToken.None);
            File.Delete(request.DestinationPath);

            var backend = new DemandBackend();
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(handle) &&
                      !File.Exists(Path.Combine(
                          stateDirectory,
                          "model-transfers",
                          $"{identity.Digest}.json")),
                TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(request.DestinationPath), Is.False);
                Assert.That(File.Exists(controlPath), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_ReclaimedPartialIdentity_CancelsQueuedOrphanCleanup()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "reclaimed-partial.bin");
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var requestHandle = new ModelTransferHandle(identity.DeterministicIdentifier);

            // Park the scheduler in a restored-pause retry for an unrelated transfer, so the
            // queued orphan cleanup cannot run between the release and the reclaim below.
            var parkingHandle = new ModelTransferHandle("0123456789abcdef");
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        parkingHandle,
                        "active",
                        completedLength: 0,
                        totalLength: 8,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                Path.Combine(root, "parking.bin"),
                                8,
                                0,
                                ["https://models.example.test/parking.bin"])
                        ])
                ],
                PauseAttemptStarted = parked,
                PauseAttemptRelease = releaseParked.Task
            };
            backend.PauseFailuresRemaining[parkingHandle] = 1;
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            await parked.Task.WaitAsync(TimeSpan.FromSeconds(2));

            try
            {
                coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
                coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
                coordinator.RegisterDownloads("session-2", [request], replaceExisting: true);
            }
            finally
            {
                releaseParked.TrySetResult();
            }

            // The registration snapshot also retires the unrelated transfer, so its removal shows
            // the scheduler resumed. The orphan stage runs right after it in the same pass; the
            // short delay only gives a regression time to show and cannot fail a correct run.
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(parkingHandle),
                TimeSpan.FromSeconds(5));
            await Task.Delay(250);

            var reloaded = new ModelTransferIndex(stateDirectory);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-2",
                CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(backend.RemovedHandles, Does.Not.Contain(requestHandle));
                Assert.That(File.Exists(request.DestinationPath), Is.True);
                Assert.That(reloaded.TryGet(identity.Digest, out _), Is.True);
                Assert.That(opened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_OmittedLeasedModelIsCleanedAfterFinalLeaseCloses()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "leased.bin");
            var backend = new DemandBackend();
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            await Task.Delay(250);

            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var reloaded = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(backend.RemovedHandles, Is.Empty);
                Assert.That(File.Exists(request.DestinationPath), Is.True);
                Assert.That(reloaded.TryGet(identity.Digest, out _), Is.True);
            });
            Assert.ThrowsAsync<ModelHydrationIOException>(async () =>
                await coordinator.OpenAsync(
                    request.DestinationPath,
                    "session-1",
                    CancellationToken.None));

            await coordinator.EnsureRangeAsync(
                opened.LeaseId,
                opened.TransferEpoch,
                0,
                4,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await coordinator.ReleaseAsync(opened.LeaseId);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(
                          new ModelTransferHandle(identity.DeterministicIdentifier)) &&
                      !File.Exists(request.DestinationPath),
                TimeSpan.FromSeconds(2));

            var afterCleanup = ModelHydrationOpenResult.Unmanaged;
            await WaitUntilAsync(
                async () =>
                {
                    try
                    {
                        afterCleanup = await coordinator.OpenAsync(
                            request.DestinationPath,
                            "session-1",
                            CancellationToken.None);
                        return afterCleanup.Disposition == ModelHydrationOpenDisposition.Unmanaged;
                    }
                    catch (ModelHydrationIOException)
                    {
                        // The partial inode is gone, but the fail-closed orphan
                        // tombstone may still be completing state cleanup.
                        return false;
                    }
                },
                TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(afterCleanup.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.Unmanaged));
                Assert.That(
                    new ModelTransferIndex(stateDirectory).TryGet(identity.Digest, out _),
                    Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_OmittedPartialFailsClosedUntilCleanupFinishes()
    {
        var root = CreateTempDirectory();
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "cleanup-tombstone.bin");
            var cleanupStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                PauseAttemptStarted = cleanupStarted,
                PauseAttemptRelease = releaseCleanup.Task
            };
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.ReleaseAsync(opened.LeaseId);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(File.Exists(request.DestinationPath), Is.True);
            Assert.ThrowsAsync<ModelHydrationIOException>(async () =>
                await coordinator.OpenAsync(
                    request.DestinationPath,
                    "session-1",
                    CancellationToken.None));

            releaseCleanup.TrySetResult();
            var afterCleanup = ModelHydrationOpenResult.Unmanaged;
            await WaitUntilAsync(
                async () =>
                {
                    try
                    {
                        afterCleanup = await coordinator.OpenAsync(
                            request.DestinationPath,
                            "session-1",
                            CancellationToken.None);
                        return afterCleanup.Disposition == ModelHydrationOpenDisposition.Unmanaged;
                    }
                    catch (ModelHydrationIOException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(afterCleanup.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.Unmanaged));
                Assert.That(File.Exists(request.DestinationPath), Is.False);
            });
        }
        finally
        {
            releaseCleanup.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_OmittedModelDuringOpenCleansAfterLeaseCloses()
    {
        var root = CreateTempDirectory();
        var releaseOpen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "opening-while-omitted.bin");
            var openStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                CreateAttemptStarted = openStarted,
                CreateAttemptRelease = releaseOpen.Task
            };
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opening = coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);

            Assert.Multiple(() =>
            {
                Assert.That(backend.RemovedHandles, Is.Empty);
                Assert.That(File.Exists(request.DestinationPath), Is.True);
            });

            releaseOpen.TrySetResult();
            var opened = await opening.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(opened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));
            await coordinator.ReleaseAsync(opened.LeaseId);

            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(
                          new ModelTransferHandle(identity.DeterministicIdentifier)) &&
                      !File.Exists(request.DestinationPath),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseOpen.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_CanceledSessionBeforeLeasePublication_DoesNotLeaveDescriptorLease()
    {
        var root = CreateTempDirectory();
        var releaseOpen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var request = CreateRequest(root, "canceled-open.bin");
            var openStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                CreateAttemptStarted = openStarted,
                CreateAttemptRelease = releaseOpen.Task
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var opening = coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.CancelSession("session-1");
            releaseOpen.TrySetResult();

            var exception = Assert.ThrowsAsync<ModelHydrationIOException>(
                async () => await opening.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(exception!.Message, Does.Contain("was canceled"));

            coordinator.RegisterDownloads("session-2", [], replaceExisting: true);
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(
                          new ModelTransferHandle(identity.DeterministicIdentifier)) &&
                      !File.Exists(request.DestinationPath),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseOpen.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task Prompt_CanceledSessionBeforeLeasePublication_DoesNotLeaveForegroundLease()
    {
        var root = CreateTempDirectory();
        var releaseAttachment = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var request = CreateRequest(root, "canceled-prompt.bin");
            var attachmentStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                CreateAttemptStarted = attachmentStarted,
                CreateAttemptRelease = releaseAttachment.Task
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var hydration = coordinator.EnsurePromptDownloadsAsync(
                "session-1",
                [request],
                CancellationToken.None);
            await attachmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.CancelSession("session-1");
            releaseAttachment.TrySetResult();

            var exception = Assert.ThrowsAsync<ModelHydrationIOException>(
                async () => await hydration.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(exception!.Message, Does.Contain("was canceled"));

            coordinator.RegisterDownloads("session-2", [], replaceExisting: true);
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(
                          new ModelTransferHandle(identity.DeterministicIdentifier)) &&
                      !File.Exists(request.DestinationPath),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseAttachment.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task Open_ReactivatedSessionRejectsStaleGenerationBeforeLeasePublication()
    {
        var root = CreateTempDirectory();
        var releaseOpen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var request = CreateRequest(root, "reactivated-stale-open.bin");
            var openStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                CreateAttemptStarted = openStarted,
                CreateAttemptRelease = releaseOpen.Task
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var staleOpen = coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.CancelSession("session-1");
            coordinator.ActivateSession("session-1");
            releaseOpen.TrySetResult();

            var exception = Assert.ThrowsAsync<ModelHydrationIOException>(
                async () => await staleOpen.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(exception!.Message, Does.Contain("changed hydration generation"));

            var currentOpen = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);
            await coordinator.ReleaseAsync(currentOpen.LeaseId);
        }
        finally
        {
            releaseOpen.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task Prompt_ReactivatedSessionRejectsStaleGenerationBeforeLeasePublication()
    {
        var root = CreateTempDirectory();
        var releaseAttachment = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var currentPromptCancellation = new CancellationTokenSource();
        try
        {
            var request = CreateRequest(root, "reactivated-stale-prompt.bin");
            var attachmentStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new DemandBackend
            {
                CreateAttemptStarted = attachmentStarted,
                CreateAttemptRelease = releaseAttachment.Task
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);

            var stalePrompt = coordinator.EnsurePromptDownloadsAsync(
                "session-1",
                [request],
                CancellationToken.None);
            await attachmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            coordinator.CancelSession("session-1");
            coordinator.ActivateSession("session-1");
            releaseAttachment.TrySetResult();

            var exception = Assert.ThrowsAsync<ModelHydrationIOException>(
                async () => await stalePrompt.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(exception!.Message, Does.Contain("changed hydration generation"));

            var currentPrompt = coordinator.EnsurePromptDownloadsAsync(
                "session-1",
                [request],
                currentPromptCancellation.Token);
            await WaitUntilAsync(
                () => backend.UnpausedHandles.Count > 0,
                TimeSpan.FromSeconds(2));
            currentPromptCancellation.Cancel();
            Assert.That(
                async () => await currentPrompt,
                Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            currentPromptCancellation.Cancel();
            releaseAttachment.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public async Task Initialize_TransientRestoredPauseFailure_IsRetried()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "restored-pause.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 1,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true
                },
                CancellationToken.None);
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        "active",
                        4,
                        request.SizeBytes,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                request.DestinationPath,
                                request.SizeBytes,
                                4,
                                [request.SourceUrl])
                        ])
                ]
            };
            backend.PauseFailuresRemaining[handle] = 1;
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);

            await coordinator.InitializeAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => backend.PauseAttempts.Count(attempted => attempted == handle) >= 2 &&
                      backend.PausedHandles.Contains(handle),
                TimeSpan.FromSeconds(2));

            Assert.That(backend.PausedHandles, Does.Contain(handle));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestCase("complete")]
    [TestCase("error")]
    [TestCase("removed")]
    public async Task Initialize_StoppedManagedTransfer_IsNotPausedOrQueuedForRetry(string status)
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "models", $"restored-{status}.bin");
            var handle = new ModelTransferHandle("1234567890abcdef");
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        status,
                        completedLength: status == "complete" ? 8 : 4,
                        totalLength: 8,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                path,
                                8,
                                status == "complete" ? 8 : 4,
                                ["https://old.example/model"])
                        ])
                ]
            };
            await using var coordinator = CreateCoordinator(backend, root);

            await coordinator.InitializeAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(350));

            Assert.That(backend.PauseAttempts, Is.Empty);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Initialize_UnindexedManagedTransfer_IsPausedUntilRegistrationSnapshotFinalizes()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "models", "legacy.bin");
            var handle = new ModelTransferHandle("1234567890abcdef");
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        "active",
                        4,
                        8,
                        files:
                        [
                            new ModelTransferFileSnapshot(path, 8, 4, ["https://old.example/model"])
                        ])
                ]
            };
            await using var coordinator = CreateCoordinator(backend, root);

            await coordinator.InitializeAsync(CancellationToken.None);

            Assert.That(backend.PausedHandles, Does.Contain(handle));
            Assert.That(backend.RemovedHandles, Is.Empty);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(handle),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_LegacyCleanup_PreservesReplacedSuccessorFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "models", "legacy-successor.bin");
            var controlPath = $"{path}.aria2";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "legacy");
            await File.WriteAllTextAsync(controlPath, "legacy-control");
            var handle = new ModelTransferHandle("1234567890abcdef");
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        "paused",
                        4,
                        8,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                path,
                                8,
                                4,
                                ["https://old.example/model"])
                        ])
                ]
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);

            var successorPath = $"{path}.successor";
            var successorControlPath = $"{controlPath}.successor";
            await File.WriteAllTextAsync(successorPath, "successor");
            await File.WriteAllTextAsync(successorControlPath, "successor-control");
            File.Move(successorPath, path, overwrite: true);
            File.Move(successorControlPath, controlPath, overwrite: true);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(handle),
                TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("successor"));
                Assert.That(File.ReadAllText(controlPath), Is.EqualTo("successor-control"));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_TransientLegacyCleanupFailure_IsRetried()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "models", "legacy-retry.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "partial");
            await File.WriteAllTextAsync($"{path}.aria2", "control");
            var handle = new ModelTransferHandle("1234567890abcdef");
            var backend = new DemandBackend
            {
                RemoveFailuresRemaining = 1,
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        "paused",
                        4,
                        8,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                path,
                                8,
                                4,
                                ["https://old.example/model"])
                        ])
                ]
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);

            coordinator.RegisterDownloads("session-1", [], replaceExisting: true);
            // Legacy cleanup removes the transfer before deleting its files: wait for both.
            await WaitUntilAsync(
                () => backend.RemoveAttempts >= 2 &&
                      backend.RemovedHandles.Contains(handle) &&
                      !File.Exists(path) &&
                      !File.Exists($"{path}.aria2"),
                TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(path), Is.False);
                Assert.That(File.Exists($"{path}.aria2"), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Initialize_IndexedTransitionalZeroLengthSnapshot_PreservesRecoveryRecord()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "restored.bin");
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 1,
                    PlaceholderOwned = true
                },
                CancellationToken.None);
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        "paused",
                        0,
                        0,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                request.DestinationPath,
                                0,
                                0,
                                [request.SourceUrl])
                        ])
                ]
            };
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);

            await coordinator.InitializeAsync(CancellationToken.None);

            var reloaded = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(backend.PausedHandles, Does.Contain(handle));
                Assert.That(backend.RemovedHandles, Does.Not.Contain(handle));
                Assert.That(reloaded.TryGet(identity.Digest, out _), Is.True);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task Initialize_IdentityMismatchedSnapshot_RemovesVerifiedOwnedFilesBeforeQuarantine()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "mismatched-snapshot.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            var controlPath = $"{request.DestinationPath}.aria2";
            await File.WriteAllTextAsync(controlPath, "control");
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var fileIdentity),
                Is.True);

            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 2,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = fileIdentity.DeviceId,
                    Inode = fileIdentity.Inode,
                    PlaceholderOwned = true
                },
                CancellationToken.None);
            var backend = new DemandBackend
            {
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        "paused",
                        4,
                        request.SizeBytes * 2,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                request.DestinationPath,
                                request.SizeBytes * 2,
                                4,
                                [request.SourceUrl])
                        ])
                ]
            };
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);

            await coordinator.InitializeAsync(CancellationToken.None);

            var reloaded = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(backend.RemovedHandles, Does.Contain(handle));
                Assert.That(File.Exists(request.DestinationPath), Is.False);
                Assert.That(File.Exists(controlPath), Is.False);
                Assert.That(reloaded.TryGet(identity.Digest, out _), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterDownloads_StagedRestartPublication_RecoversBeforeAttaching()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "staged-restart.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            var stagingPath =
                $"{request.DestinationPath}.{identity.DeterministicIdentifier}.offloadr-placeholder";
            await File.WriteAllBytesAsync(
                stagingPath,
                new byte[checked((int)request.SizeBytes)]);
            Assert.That(
                LinuxFileIdentityReader.TryRead(stagingPath, out var stagedIdentity),
                Is.True);

            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 5,
                    DeviceId = stagedIdentity.DeviceId,
                    Inode = stagedIdentity.Inode,
                    PlaceholderOwned = true,
                    RestartRequired = true
                },
                CancellationToken.None);

            var backend = new DemandBackend();
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [root]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var currentIdentity),
                Is.True);
            var reloaded = new ModelTransferIndex(stateDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(opened.TransferEpoch, Is.GreaterThan(5));
                Assert.That(backend.RemovedHandles, Does.Contain(handle));
                Assert.That(backend.CreateCallCount, Is.EqualTo(1));
                Assert.That(File.Exists(stagingPath), Is.False);
                Assert.That(reloaded.TryGet(identity.Digest, out var record), Is.True);
                Assert.That(record.DeviceId, Is.EqualTo(currentIdentity.DeviceId));
                Assert.That(record.Inode, Is.EqualTo(currentIdentity.Inode));
                Assert.That(record.RestartRequired, Is.False);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegisterDownloads_MissingIndexedTarget_RestartsFromOwnedPlaceholder()
    {
        var root = CreateTempDirectory();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var request = CreateRequest(root, "missing-indexed.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            Assert.That(
                LinuxFileIdentityReader.TryRead(request.DestinationPath, out var oldIdentity),
                Is.True);
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            var index = new ModelTransferIndex(stateDirectory);
            await index.UpsertAsync(
                new ModelTransferIndexRecord
                {
                    IdentityDigest = identity.Digest,
                    ModelId = identity.ModelId,
                    DestinationPath = identity.DestinationPath,
                    ExpectedLength = identity.ExpectedLength,
                    Handle = handle.Value,
                    TransferEpoch = 4,
                    PieceLength = 4,
                    NumPieces = 2,
                    DeviceId = oldIdentity.DeviceId,
                    Inode = oldIdentity.Inode,
                    PlaceholderOwned = true,
                    Complete = true
                },
                CancellationToken.None);
            File.Delete(request.DestinationPath);

            var backend = new DemandBackend();
            var capabilities = new ModelHydrationRuntimeCapabilities(
                "synthetic-editor",
                SupportsPersistentRangeHydration: true,
                PersistentModelRoots: [root]);
            await using (var initialCoordinator = new DemandAwareModelHydrationCoordinator(
                             backend,
                             capabilities,
                             stateDirectory))
            {
                await initialCoordinator.InitializeAsync(CancellationToken.None);
                Assert.That(
                    () => initialCoordinator.RegisterDownloads(
                        "session-1",
                        [request],
                        replaceExisting: true),
                    Throws.Nothing);
            }

            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                capabilities,
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("session-1", [request], replaceExisting: true);
            var opened = await coordinator.OpenAsync(
                request.DestinationPath,
                "session-1",
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(opened.TransferEpoch, Is.GreaterThan(4));
                Assert.That(backend.RemovedHandles, Does.Contain(handle));
                Assert.That(backend.CreateCallCount, Is.EqualTo(1));
                Assert.That(new FileInfo(request.DestinationPath).Length, Is.EqualTo(request.SizeBytes));
            });
            await coordinator.ReleaseAsync(opened.LeaseId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RegistrationSnapshot_RejectsClaimedUnverifiedLegacyTransfer()
    {
        var root = CreateTempDirectory();
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var request = CreateRequest(root, "legacy-conflict.bin");
            var handle = new ModelTransferHandle("1234567890abcdef");
            var cleanupStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                new byte[checked((int)request.SizeBytes)]);
            await File.WriteAllTextAsync($"{request.DestinationPath}.aria2", "legacy-control");
            var backend = new DemandBackend
            {
                RemoveAttemptStarted = cleanupStarted,
                RemoveAttemptRelease = releaseCleanup.Task,
                ListedSnapshots =
                [
                    TestModelTransferBackend.Snapshot(
                        handle,
                        "active",
                        4,
                        request.SizeBytes,
                        files:
                        [
                            new ModelTransferFileSnapshot(
                                request.DestinationPath,
                                request.SizeBytes,
                                4,
                                ["https://old.example/model"])
                        ])
                ]
            };
            await using var coordinator = CreateCoordinator(backend, root);
            await coordinator.InitializeAsync(CancellationToken.None);

            Assert.That(
                () => coordinator.RegisterDownloads(
                    "session-1",
                    [request],
                    replaceExisting: true),
                Throws.InvalidOperationException);
            Assert.Multiple(() =>
            {
                Assert.That(backend.PausedHandles, Does.Contain(handle));
                Assert.That(backend.RemovedHandles, Is.Empty);
            });
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            releaseCleanup.TrySetResult();
            await WaitUntilAsync(
                () => backend.RemovedHandles.Contains(handle) &&
                      !File.Exists(request.DestinationPath) &&
                      !File.Exists($"{request.DestinationPath}.aria2"),
                TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(request.DestinationPath), Is.False);
                Assert.That(File.Exists($"{request.DestinationPath}.aria2"), Is.False);
            });

            Assert.That(
                () => coordinator.RegisterDownloads(
                    "session-1",
                    [request],
                    replaceExisting: true),
                Throws.Nothing);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            TryDelete(root);
        }
    }

    [Test]
    public void RuntimeCapabilities_EnableConfiguredPersistentRootWithoutEditorSpecificShimLogic()
    {
        var root = CreateTempDirectory();
        try
        {
            var synthetic = ModelHydrationRuntimeCapabilities.FromEnvironment(
                "future-editor",
                name => name switch
                {
                    "RUNNER_PERSISTENT_RANGE_HYDRATION" => "1",
                    "RUNNER_VFS_ROOTS" => root,
                    "RUNNER_VFS_LIB" => "/opt/offloadr/lib/liboffloadr_model_vfs.so",
                    _ => null
                },
                fileExists: static _ => true);
            var forge = ModelHydrationRuntimeCapabilities.FromEnvironment(
                "forge-neo",
                name => name switch
                {
                    "RUNNER_PERSISTENT_RANGE_HYDRATION" => "0",
                    "RUNNER_VFS_ROOTS" => root,
                    _ => null
                });

            Assert.Multiple(() =>
            {
                Assert.That(synthetic.OwnsPersistentModelPath(Path.Combine(root, "models", "x.bin")), Is.True);
                Assert.That(forge.SupportsPersistentRangeHydration, Is.False);
                Assert.That(forge.OwnsPersistentModelPath(Path.Combine(root, "x.bin")), Is.False);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public void RuntimeCapabilities_RejectEnabledRangeHydrationWithoutPreloadLibrary()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.That(
                () => ModelHydrationRuntimeCapabilities.FromEnvironment(
                    "future-editor",
                    name => name switch
                    {
                        "RUNNER_PERSISTENT_RANGE_HYDRATION" => "1",
                        "RUNNER_VFS_ROOTS" => root,
                        "RUNNER_VFS_LIB" => "/missing/liboffloadr_model_vfs.so",
                        _ => null
                    },
                    fileExists: static _ => false),
                Throws.InvalidOperationException.With.Message.Contains("requires the model VFS library"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static DemandAwareModelHydrationCoordinator CreateCoordinator(
        IModelTransferBackend backend,
        string root,
        TimeSpan? shutdownCleanupTimeout = null)
        => new(
            backend,
            new ModelHydrationRuntimeCapabilities(
                "synthetic-editor",
                SupportsPersistentRangeHydration: true,
                PersistentModelRoots: [root]),
            Path.Combine(root, "state"),
            shutdownCleanupTimeout);

    private static ModelDownloadRequest CreateRequest(string root, string filename)
        => new()
        {
            ModelId = Path.GetFileNameWithoutExtension(filename),
            Filename = filename,
            DestinationPath = Path.Combine(root, "models", filename),
            SizeBytes = 8,
            SourceUrl = $"https://models.example.test/{filename}"
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"demand-hydration-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Condition was not satisfied within {timeout}.");
            }

            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Condition was not satisfied within {timeout}.");
            }

            await Task.Delay(25);
        }
    }

    public enum UriRefreshFailurePoint
    {
        Status,
        ChangeUri,
        SaveSession
    }

    private sealed class DemandBackend : TestModelTransferBackend
    {
        private readonly Dictionary<ModelTransferHandle, ModelTransferSnapshot> _snapshots = [];

        public ModelTransferCreateRequest? LastCreateRequest { get; private set; }
        public List<ModelTransferHandle> UnpausedHandles { get; } = [];
        public List<ModelTransferHandle> UnpauseAttempts { get; } = [];
        public List<ModelTransferHandle> PausedHandles { get; } = [];
        public List<ModelTransferHandle> PauseAttempts { get; } = [];
        public List<ModelTransferHandle> RemovedHandles { get; } = [];
        public List<ModelTransferHandle> MoveToFrontHandles { get; } = [];
        public Dictionary<ModelTransferHandle, int> StatusCallsByHandle { get; } = [];
        public List<(ModelTransferHandle Handle, IReadOnlyList<string> ReplacementUris)> UriChanges { get; } = [];
        public List<string> OperationOrder { get; } = [];
        public IReadOnlyList<ModelTransferSnapshot> ListedSnapshots { get; set; } = [];
        public ModelTransferHandle? CreatedHandle { get; private set; }
        public HashSet<ModelTransferHandle> UnavailableStatusHandles { get; } = [];
        public Dictionary<ModelTransferHandle, int> MoveFailuresRemaining { get; } = [];
        public Dictionary<ModelTransferHandle, int> UnpauseFailuresRemaining { get; } = [];
        public HashSet<ModelTransferHandle> UnavailablePauseHandles { get; } = [];
        public HashSet<ModelTransferHandle> RemoveBeforeUnpauseHandles { get; } = [];
        public Dictionary<ModelTransferHandle, int> PauseFailuresRemaining { get; } = [];
        public int RemoveFailuresRemaining { get; set; }
        public int RemoveAttempts { get; private set; }
        public int CreateCallCount { get; private set; }
        public int StatusFailuresRemaining { get; set; }
        public int UnavailableStatusFailuresRemaining { get; set; }
        public int ChangeUriFailuresRemaining { get; set; }
        public int SaveSessionFailuresRemaining { get; set; }
        public int UriChangeAttempts { get; private set; }
        public int SaveSessionAttempts { get; private set; }
        public bool CompleteOnUnpause { get; init; }
        public string BitfieldOnUnpause { get; set; } = "80";
        public int ZeroLengthStatusReadsRemaining { get; set; }
        public int ZeroGeometryStatusReadsRemaining { get; set; }
        public TaskCompletionSource? PauseAttemptStarted { get; init; }
        public Task? PauseAttemptRelease { get; init; }
        public TaskCompletionSource? StatusAttemptStarted { get; set; }
        public Task? StatusAttemptRelease { get; set; }

        /// <summary>When set, each status read takes one permit before it is counted and answered.</summary>
        public SemaphoreSlim? StatusPermits { get; set; }
        public TaskCompletionSource? RemoveAttemptStarted { get; init; }
        public Task? RemoveAttemptRelease { get; init; }
        public TaskCompletionSource? CreateAttemptStarted { get; init; }
        public Task? CreateAttemptRelease { get; init; }

        public override Task<IReadOnlyList<ModelTransferSnapshot>> ListAsync(
            CancellationToken cancellationToken)
            => Task.FromResult(ListedSnapshots);

        public override async Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(
            ModelTransferCreateRequest request,
            CancellationToken cancellationToken)
        {
            CreateCallCount++;
            LastCreateRequest = request;
            CreateAttemptStarted?.TrySetResult();
            if (CreateAttemptRelease is not null)
            {
                await CreateAttemptRelease.WaitAsync(cancellationToken);
            }

            var handle = new ModelTransferHandle(request.PreferredIdentifier ?? "0000000000000001");
            CreatedHandle = handle;
            if (request.ResumeMode == ModelTransferResumeMode.RequireExisting &&
                _snapshots.TryGetValue(handle, out var resumableSnapshot))
            {
                _snapshots[handle] = resumableSnapshot with
                {
                    Status = "paused",
                    Files =
                    [
                        resumableSnapshot.Files[0] with
                        {
                            Uris = request.SourceUris
                        }
                    ]
                };
                IReadOnlyList<ModelTransferHandle> resumedHandles = [handle];
                return resumedHandles;
            }

            var numPieces = (request.ExpectedLength + 3) / 4;
            var emptyBitfield = new string('0', checked((int)(((numPieces + 7) / 8) * 2)));
            _snapshots[handle] = Snapshot(
                handle,
                "paused",
                completedLength: 0,
                totalLength: request.ExpectedLength,
                bitfield: emptyBitfield,
                pieceLength: 4,
                numPieces: numPieces,
                files:
                [
                    new ModelTransferFileSnapshot(
                        request.DestinationPath,
                        request.ExpectedLength,
                        0,
                        request.SourceUris)
                ]);
            IReadOnlyList<ModelTransferHandle> handles = [handle];
            return handles;
        }

        public override async Task<ModelTransferSnapshot> GetStatusAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            StatusAttemptStarted?.TrySetResult();
            if (StatusAttemptRelease is not null)
            {
                await StatusAttemptRelease.WaitAsync(cancellationToken);
            }

            if (StatusPermits is { } permits)
            {
                await permits.WaitAsync(cancellationToken);
            }

            StatusCallsByHandle[handle] = StatusCallsByHandle.GetValueOrDefault(handle) + 1;
            if (StatusFailuresRemaining > 0)
            {
                StatusFailuresRemaining--;
                throw new IOException("simulated transient status failure");
            }

            if (UnavailableStatusFailuresRemaining > 0)
            {
                UnavailableStatusFailuresRemaining--;
                throw new ModelTransferStatusUnavailableException($"Transfer '{handle}' is unavailable.");
            }

            if (UnavailableStatusHandles.Contains(handle))
            {
                throw new ModelTransferStatusUnavailableException($"Transfer '{handle}' is unavailable.");
            }

            if (!_snapshots.TryGetValue(handle, out var snapshot))
            {
                throw new ModelTransferStatusUnavailableException($"Transfer '{handle}' is unavailable.");
            }

            if (ZeroLengthStatusReadsRemaining > 0)
            {
                ZeroLengthStatusReadsRemaining--;
                snapshot = snapshot with
                {
                    TotalLength = 0,
                    Files = [snapshot.Files[0] with { Length = 0 }]
                };
            }

            if (ZeroGeometryStatusReadsRemaining > 0)
            {
                ZeroGeometryStatusReadsRemaining--;
                snapshot = snapshot with
                {
                    PieceLength = 0,
                    NumPieces = 0,
                    Bitfield = string.Empty
                };
            }

            return snapshot;
        }

        public override Task UnpauseAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            UnpauseAttempts.Add(handle);
            if (RemoveBeforeUnpauseHandles.Remove(handle))
            {
                ListedSnapshots = ListedSnapshots
                    .Where(snapshot => snapshot.Handle != handle)
                    .ToArray();
                throw new IOException("simulated missing transfer during unpause");
            }

            if (UnpauseFailuresRemaining.TryGetValue(handle, out var failuresRemaining) &&
                failuresRemaining > 0)
            {
                UnpauseFailuresRemaining[handle] = failuresRemaining - 1;
                throw new IOException("simulated unpause failure");
            }

            UnpausedHandles.Add(handle);
            OperationOrder.Add("unpause");
            if (!_snapshots.TryGetValue(handle, out var previous))
            {
                return Task.CompletedTask;
            }

            var completedLength = BitfieldOnUnpause switch
            {
                "00" => 0,
                "c0" => previous.TotalLength,
                _ => CompleteOnUnpause ? previous.TotalLength : 4
            };
            _snapshots[handle] = previous with
            {
                Status = CompleteOnUnpause ? "complete" : "active",
                CompletedLength = completedLength,
                Bitfield = CompleteOnUnpause ? "c0" : BitfieldOnUnpause,
                Files =
                [
                    previous.Files[0] with
                    {
                        CompletedLength = completedLength
                    }
                ]
            };
            return Task.CompletedTask;
        }

        public void SetBitfield(
            ModelTransferHandle handle,
            string bitfield,
            long completedLength)
        {
            var previous = _snapshots[handle];
            _snapshots[handle] = previous with
            {
                CompletedLength = completedLength,
                Bitfield = bitfield,
                Files =
                [
                    previous.Files[0] with
                    {
                        CompletedLength = completedLength
                    }
                ]
            };
        }

        public void SetStatus(ModelTransferHandle handle, string status)
        {
            _snapshots[handle] = _snapshots[handle] with { Status = status };
        }

        public void SetSnapshot(ModelTransferSnapshot snapshot)
        {
            _snapshots[snapshot.Handle] = snapshot;
        }

        public override Task MoveToFrontAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            MoveToFrontHandles.Add(handle);
            if (MoveFailuresRemaining.TryGetValue(handle, out var failuresRemaining) &&
                failuresRemaining > 0)
            {
                MoveFailuresRemaining[handle] = failuresRemaining - 1;
                throw new IOException("simulated move-to-front failure");
            }

            return Task.CompletedTask;
        }

        public override Task ChangeUriAsync(
            ModelTransferHandle handle,
            IReadOnlyList<string> currentUris,
            IReadOnlyList<string> replacementUris,
            CancellationToken cancellationToken)
        {
            UriChangeAttempts++;
            if (ChangeUriFailuresRemaining > 0)
            {
                ChangeUriFailuresRemaining--;
                throw new IOException("simulated transient change-uri failure");
            }

            UriChanges.Add((handle, replacementUris.ToArray()));
            OperationOrder.Add("change-uri");
            var previous = _snapshots[handle];
            _snapshots[handle] = previous with
            {
                Files =
                [
                    previous.Files[0] with
                    {
                        Uris = replacementUris.ToArray()
                    }
                ]
            };
            return Task.CompletedTask;
        }

        public override Task SaveSessionAsync(CancellationToken cancellationToken)
        {
            SaveSessionAttempts++;
            if (SaveSessionFailuresRemaining > 0)
            {
                SaveSessionFailuresRemaining--;
                throw new IOException("simulated transient session-save failure");
            }

            return Task.CompletedTask;
        }

        public override async Task ForcePauseAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            PauseAttempts.Add(handle);
            if (UnavailablePauseHandles.Contains(handle))
            {
                throw new ModelTransferStatusUnavailableException($"Transfer '{handle}' is unavailable.");
            }

            if (PauseFailuresRemaining.TryGetValue(handle, out var failuresRemaining) &&
                failuresRemaining > 0)
            {
                PauseFailuresRemaining[handle] = failuresRemaining - 1;
                throw new IOException("simulated pause failure");
            }

            PauseAttemptStarted?.TrySetResult();
            if (PauseAttemptRelease is not null)
            {
                await PauseAttemptRelease.WaitAsync(cancellationToken);
            }

            PausedHandles.Add(handle);
        }

        public override async Task RemoveAsync(
            ModelTransferHandle handle,
            CancellationToken cancellationToken)
        {
            RemoveAttempts++;
            if (RemoveFailuresRemaining > 0)
            {
                RemoveFailuresRemaining--;
                throw new IOException("simulated transient remove failure");
            }

            RemoveAttemptStarted?.TrySetResult();
            if (RemoveAttemptRelease is not null)
            {
                await RemoveAttemptRelease.WaitAsync(cancellationToken);
            }

            RemovedHandles.Add(handle);
            _snapshots.Remove(handle);
        }
    }
}

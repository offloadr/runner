using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    private static readonly DateTime FirstStoredUtc = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>An artifact as a listing gives it: its catalog name and the time it was stored.</summary>
    private static EditorArtifactMetadata StoredOutput(string filename, DateTime storedUtc, long? sizeBytes = 3, string type = "output")
        => new()
        {
            EditorSid = "editor-1",
            Filename = filename,
            Type = type,
            Subfolder = string.Empty,
            SizeBytes = sizeBytes,
            CreatedUtc = Timestamp.FromDateTime(storedUtc),
        };

    /// <summary>Stamps each upload's receipt with the time it was stored: one minute after the previous one.</summary>
    private static FakeRunnerArtifactClient ClientStoringUploads(RunnerArtifactServiceListArtifactsResponse listing, List<EditorArtifactMetadata>? receipts = null)
    {
        var stored = 0;
        return new FakeRunnerArtifactClient(listing)
        {
            RewriteUploadArtifact = artifact =>
            {
                artifact.CreatedUtc = Timestamp.FromDateTime(FirstStoredUtc.AddMinutes(stored++));
                receipts?.Add(artifact.Clone());
                return artifact;
            },
        };
    }

    [Test]
    public async Task ADeletedOutputsLocalCopyIsRemovedAndNeitherCountedNorUploadedAgain()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "deleted.bin");
            var kept = Path.Combine(paths.OutputDirectory, "kept.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var receipts = new List<EditorArtifactMetadata>();
            var client = ClientStoringUploads(listing, receipts);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            await File.WriteAllBytesAsync(kept, [4, 5, 6]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            // The user deletes the first output in their files; the other is still listed.
            listing.Artifacts.Add(receipts[1]);
            // The control plane names it as it holds it now, which may say more than the receipt did.
            var deleted = receipts[0].Clone();
            deleted.PromptId = "prompt-1";
            deleted.Filename = "DELETED.bin";
            listing.DeletedArtifacts.Add(deleted);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(path), Is.False, "the session's copy of a deleted output goes with it");
                Assert.That(File.ReadAllBytes(kept), Is.EqualTo(new byte[] { 4, 5, 6 }));
                Assert.That(Directory.GetFiles(paths.OutputDirectory), Has.Length.EqualTo(1), "nothing is left beside the kept output");
                Assert.That(client.UploadCount, Is.EqualTo(2), "nothing is uploaded again");
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(2));
            });
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero, "a deleted output is not an unsaved one");
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ADeletionOfAnotherArtifactStoredUnderTheNameLeavesTheCopy()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "same-name.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var client = ClientStoringUploads(listing);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            // An output of an earlier session under this name was deleted; this session's is not listed either.
            listing.DeletedArtifacts.Add(StoredOutput("same-name.bin", FirstStoredUtc.AddHours(-1)));
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1),
                "the control plane did not say it deleted this one, and does not hold it");
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task AFileTheSessionWroteSinceIsNotTheDeletedArtifactsCopy()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "rewritten.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var receipts = new List<EditorArtifactMetadata>();
            var client = ClientStoringUploads(listing, receipts);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            // The editor writes the name again while the first upload is being deleted: the upload
            // of the new bytes is held, so the entry still stands for the deleted artifact.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.UploadGate = release.Task;
            await File.WriteAllBytesAsync(path, [7, 8, 9, 10]);
            listing.DeletedArtifacts.Add(receipts[0]);
            await service.RefreshSessionAsync("session-1");
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 7, 8, 9, 10 }), "a file written since is the session's own data");

            release.SetResult();
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            listing.Artifacts.Add(receipts[1]);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 7, 8, 9, 10 }));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ACopyReadBackIsOfTheArtifactTheReadSaidItWas(bool deleteTheOneRead)
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "earlier.bin");
            // Listed as it was stored first; another upload replaced it before it was read.
            var listed = StoredOutput("earlier.bin", FirstStoredUtc, sizeBytes: null);
            var replacedUtc = FirstStoredUtc.AddMinutes(30);
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { listed } };
            var client = new FakeRunnerArtifactClient(listing)
            {
                ReadArtifactContent = [9, 9, 9, 9],
                ReadArtifactModifiedUtc = replacedUtc,
            };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(path, highPriority: true, CancellationToken.None), Is.True);
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 9, 9, 9, 9 }));

            // Still listed under its name (as stored a third time), with one of the earlier two deleted.
            listing.Artifacts.Clear();
            listing.Artifacts.Add(StoredOutput("earlier.bin", FirstStoredUtc.AddHours(1), sizeBytes: null));
            listing.DeletedArtifacts.Add(StoredOutput("earlier.bin", deleteTheOneRead ? replacedUtc : FirstStoredUtc, sizeBytes: null));
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1", null, CancellationToken.None);

            if (deleteTheOneRead)
            {
                Assert.That(new FileInfo(path).Length, Is.Zero, "the copy is gone, and the artifact now listed has its placeholder");
            }
            else
            {
                Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 9, 9, 9, 9 }),
                    "the bytes read were never those of the artifact deleted");
            }
        }
        finally { TryDelete(root); }
    }

    [TestCase(0, true)]
    [TestCase(10, true)]
    [TestCase(-10, false)]
    public async Task APlaceholderGoesWhenItsArtifactOrALaterOneUnderItsNameWasDeleted(int deletedMinutesAfter, bool removed)
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "never-read.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { StoredOutput("never-read.bin", FirstStoredUtc) } };
            var client = new FakeRunnerArtifactClient(listing);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(new FileInfo(path).Length, Is.Zero, "the listed artifact has a placeholder");

            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(StoredOutput("never-read.bin", FirstStoredUtc.AddMinutes(deletedMinutesAfter)));
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1", null, CancellationToken.None);

            Assert.That(File.Exists(path), Is.EqualTo(!removed));
            Assert.That(client.UploadCount, Is.Zero);
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(removed ? 0 : 1));
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ADeletedOutputsEvictedPlaceholderIsRemovedAndNotCounted()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var path = Path.Combine(paths.OutputDirectory, "evicted.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var receipts = new List<EditorArtifactMetadata>();
            var client = ClientStoringUploads(listing, receipts);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await EvictWhenRecordedAsync(service, "session-1"), Is.EqualTo(3));

            listing.DeletedArtifacts.Add(receipts[0]);
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1", null, CancellationToken.None);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ACopyAProcessHasOpenStaysUntilALaterListingAndIsNotCountedMeanwhile()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "open.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var receipts = new List<EditorArtifactMetadata>();
            var client = ClientStoringUploads(listing, receipts);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            listing.DeletedArtifacts.Add(receipts[0]);
            // The same name is stored again by another session before the copy can be removed.
            listing.Artifacts.Add(StoredOutput("open.bin", FirstStoredUtc.AddHours(1), sizeBytes: null));
            await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await service.RefreshSessionAsync("session-1");
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 1, 2, 3 }), "a reader still has it");
            }

            Assert.That(client.UploadCount, Is.EqualTo(1), "a copy waiting to be removed is not uploaded as new");
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(new FileInfo(path).Length, Is.Zero, "removed once nothing has it open; the artifact now listed has its placeholder");
            Assert.That(client.UploadCount, Is.EqualTo(1));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task APromptsSeedDoesNotForgetWhichArtifactACopyIs()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var referenced = Path.Combine(paths.OutputDirectory, "referenced.bin");
            var other = Path.Combine(paths.OutputDirectory, "other.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var receipts = new List<EditorArtifactMetadata>();
            var client = ClientStoringUploads(listing, receipts);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(referenced, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            await File.WriteAllBytesAsync(other, [4, 5, 6]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            // Both were deleted; a prompt accepted just before still names one of them, by name alone.
            listing.DeletedArtifacts.Add(receipts);
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "referenced.bin", Type = "output", Subfolder = string.Empty }],
                CancellationToken.None);
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(referenced), Is.False);
                Assert.That(File.Exists(other), Is.False);
                Assert.That(client.UploadCount, Is.EqualTo(2));
            });
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ASeedKeepsACopyKnownSoTheListingAfterItStillRecognizesIt()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "seeded.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var receipts = new List<EditorArtifactMetadata>();
            var client = ClientStoringUploads(listing, receipts);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            // A prompt names the output while the listing does not give it yet; a later listing
            // names it deleted.
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "seeded.bin", Type = "output", Subfolder = string.Empty }],
                CancellationToken.None);
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            listing.DeletedArtifacts.Add(receipts[0]);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(client.UploadCount, Is.EqualTo(1));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ASeedNamingAnOutputTheControlPlaneNoLongerHoldsDoesNotMakeItSaved()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var path = Path.Combine(paths.OutputDirectory, "gone.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var client = ClientStoringUploads(listing);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            // No longer listed, and not named as deleted: the control plane does not hold it.
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1", null, CancellationToken.None);
            // Prompts still name it, more than once. That is no word on whether it is held.
            for (var prompt = 0; prompt < 2; prompt++)
            {
                await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                    [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "gone.bin", Type = "output", Subfolder = string.Empty }],
                    CancellationToken.None);
                await WaitForIdentityUploadsToFinishAsync(service);
            }

            Assert.That(service.EvictUploadedArtifacts("session-1", long.MaxValue, TimeSpan.Zero), Is.Zero,
                "the only copy is never replaced by a placeholder");
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ASeedByNameLeavesAPlaceholderStandingForItsArtifact()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "unread.bin");
            var stored = StoredOutput("unread.bin", FirstStoredUtc);
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { stored } };
            var client = new FakeRunnerArtifactClient(listing);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(new FileInfo(path).Length, Is.Zero);

            // A prompt names the unread output just as it is deleted.
            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(stored);
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "unread.bin", Type = "output", Subfolder = string.Empty }],
                CancellationToken.None);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ACopyReadBackStaysKnownThroughASeedAndTheListingAfterIt()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "read-back.bin");
            // As the control plane lists a sealed artifact: no digest to recognize the bytes by.
            var stored = StoredOutput("read-back.bin", FirstStoredUtc, sizeBytes: 4);
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { stored } };
            var client = new FakeRunnerArtifactClient(listing) { ReadArtifactModifiedUtc = FirstStoredUtc };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(path, highPriority: true, CancellationToken.None), Is.True);

            // A prompt names it; the listing that follows gives it in full again.
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "read-back.bin", Type = "output", Subfolder = string.Empty }],
                CancellationToken.None);
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.UploadCount, Is.Zero, "bytes read back are not uploaded as new");

            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(stored);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(client.UploadCount, Is.Zero, "a deleted output is not uploaded again");
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ACopyReadAsALaterArtifactIsRecognizedWhenTheCatalogListsThatOne()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "replaced.bin");
            var replacedUtc = FirstStoredUtc.AddMinutes(30);
            // Listed as first stored; the read gives the upload that replaced it. No digests, as for sealed artifacts.
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { StoredOutput("replaced.bin", FirstStoredUtc, sizeBytes: 3) } };
            var client = new FakeRunnerArtifactClient(listing) { ReadArtifactContent = [9, 9, 9, 9], ReadArtifactModifiedUtc = replacedUtc };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(path, highPriority: true, CancellationToken.None), Is.True);

            // The catalog catches up and lists the artifact that was read.
            var replacement = StoredOutput("replaced.bin", replacedUtc, sizeBytes: 4);
            listing.Artifacts.Clear();
            listing.Artifacts.Add(replacement);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.UploadCount, Is.Zero, "the bytes read are the listed artifact's, not new data");

            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(replacement);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(client.UploadCount, Is.Zero);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ACopyStaysKnownWhenAnOlderListingAnswersLate()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "late.bin");
            var current = StoredOutput("late.bin", FirstStoredUtc.AddMinutes(30), sizeBytes: 4);
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { current } };
            var client = new FakeRunnerArtifactClient(listing) { ReadArtifactContent = [9, 9, 9, 9], ReadArtifactModifiedUtc = FirstStoredUtc.AddMinutes(30) };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(path, highPriority: true, CancellationToken.None), Is.True);

            // A listing read before that artifact was stored answers only now, with the one it replaced.
            listing.Artifacts.Clear();
            listing.Artifacts.Add(StoredOutput("late.bin", FirstStoredUtc, sizeBytes: 3));
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 9, 9, 9, 9 }));
            Assert.That(client.UploadCount, Is.Zero, "a copy of a stored artifact is not uploaded as new data");

            // The artifact the copy is of was deleted meanwhile.
            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(current);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(client.UploadCount, Is.Zero);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ACopyReadBackStaysKnownWhenAListingFromBeforeItsArtifactAnswersLate()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "early.bin");
            var stored = StoredOutput("early.bin", FirstStoredUtc, sizeBytes: 4);
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { stored } };
            var client = new FakeRunnerArtifactClient(listing) { ReadArtifactModifiedUtc = FirstStoredUtc };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(path, highPriority: true, CancellationToken.None), Is.True);

            // A listing read before the artifact existed answers only now: it lists nothing.
            listing.Artifacts.Clear();
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.UploadCount, Is.Zero, "a copy read back is never uploaded as new data");
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));

            // The artifact was in fact deleted since.
            listing.DeletedArtifacts.Add(stored);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(client.UploadCount, Is.Zero);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ACopyWaitingToBeRemovedIsNotCountedAtStopWhateverTheCatalogLists()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "waiting.bin");
            var stored = StoredOutput("waiting.bin", FirstStoredUtc.AddMinutes(30), sizeBytes: 4);
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { stored } };
            var client = new FakeRunnerArtifactClient(listing) { ReadArtifactModifiedUtc = FirstStoredUtc.AddMinutes(30) };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(path, highPriority: true, CancellationToken.None), Is.True);

            // An older listing's late answer leaves the entry listed with another artifact.
            listing.Artifacts.Clear();
            listing.Artifacts.Add(StoredOutput("waiting.bin", FirstStoredUtc, sizeBytes: 3));
            await service.RefreshSessionAsync("session-1");
            // Then the copy's artifact is named deleted while a process still has the file open.
            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(stored);
            await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await service.RefreshSessionAsync("session-1");
                await WaitForIdentityUploadsToFinishAsync(service);
            }

            // The older listing answers once more, after the one that named the deletion.
            listing.DeletedArtifacts.Clear();
            listing.Artifacts.Add(StoredOutput("waiting.bin", FirstStoredUtc, sizeBytes: 3));
            await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await service.RefreshSessionAsync("session-1");
                await WaitForIdentityUploadsToFinishAsync(service);
            }

            Assert.That(File.Exists(path), Is.True);
            Assert.That(client.UploadCount, Is.Zero);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero, "a deleted output that only waits to be removed is not unsaved");
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task AnEmptyFileTheSessionMadeIsNotRemovedAsThePlaceholder()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "emptied.bin");
            var stored = StoredOutput("emptied.bin", FirstStoredUtc);
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { stored } };
            var client = new FakeRunnerArtifactClient(listing);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(new FileInfo(path).Length, Is.Zero);

            // The session replaces the placeholder with an empty file of its own.
            File.Delete(path);
            await File.WriteAllBytesAsync(path, []);
            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(stored);
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "emptied.bin", Type = "output", Subfolder = string.Empty }],
                CancellationToken.None);
            await service.RefreshSessionAsync("session-1");

            Assert.That(File.Exists(path), Is.True, "only a placeholder the agent left is its own to remove");
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ADeletedEmptyOutputIsRemovedLikeAnyOther()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "empty.txt");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var receipts = new List<EditorArtifactMetadata>();
            var client = ClientStoringUploads(listing, receipts);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, []);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);

            // Listed once, then deleted.
            listing.Artifacts.Add(receipts[0]);
            await service.RefreshSessionAsync("session-1");
            listing.Artifacts.Clear();
            listing.DeletedArtifacts.Add(receipts[0]);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(client.UploadCount, Is.EqualTo(1));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ASeedStillMakesADownloadedInputBeReadAgain()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.InputDirectory, "picture.bin");
            var input = StoredOutput("picture.bin", FirstStoredUtc, sizeBytes: 4, type: "input");
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { input } };
            var client = new FakeRunnerArtifactClient(listing) { ReadArtifactModifiedUtc = FirstStoredUtc };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(await service.TryEnsureArtifactAvailableAsync(path, highPriority: true, CancellationToken.None), Is.True);
            Assert.That(client.ReadArtifactCallCount, Is.EqualTo(1));

            // Deleted remotely, then named by a queued prompt: the local bytes are not served on the name alone.
            listing.Artifacts.Clear();
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "picture.bin", Type = "input", Subfolder = string.Empty }],
                CancellationToken.None);

            Assert.That(new FileInfo(path).Length, Is.Zero, "the seed empties a downloaded input, so it is read again");
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task APlaceholderSeededByNameGoesWhenAnArtifactUnderThatNameWasDeleted()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "unseen.bin");
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var client = new FakeRunnerArtifactClient(listing);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            // A prompt names an output this session never saw, deleted before the listing was read.
            listing.DeletedArtifacts.Add(StoredOutput("unseen.bin", FirstStoredUtc));
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1",
                [new EditorArtifactMetadata { EditorSid = "editor-1", Filename = "unseen.bin", Type = "output", Subfolder = string.Empty }],
                CancellationToken.None);
            await WaitForIdentityUploadsToFinishAsync(service);

            Assert.That(File.Exists(path), Is.False, "the seed's own listing says what was under that name is deleted");
            Assert.That(client.UploadCount, Is.Zero);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task ADeletedInputIsLeftToTheRulesForInputs()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.InputDirectory, "picture.bin");
            var input = StoredOutput("picture.bin", FirstStoredUtc, type: "input");
            var listing = new RunnerArtifactServiceListArtifactsResponse { Artifacts = { input } };
            var client = new FakeRunnerArtifactClient(listing);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            Assert.That(File.Exists(path), Is.True);

            listing.DeletedArtifacts.Add(input);
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1", null, CancellationToken.None);

            Assert.That(File.Exists(path), Is.True, "only outputs and temporary files are removed");
        }
        finally { TryDelete(root); }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

/// <summary>
/// The local model inventory the agent publishes: a folder that cannot be read is no empty folder.
/// </summary>
public class LocalModelSyncTests
{
    [Test]
    public void AFolderThatIsNotThere_IsNoEmptyFolder()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"local-model-sync-{Guid.NewGuid():N}");

        Assert.Throws<LocalModelScanException>(() => new LocalModelScanner("runner-1", missing).Scan());
    }

    [Test]
    public void AFolderThatCannotBeListed_IsNoEmptyFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Denying a folder's listing to its own user is done with a Windows ACL here.");
        }
        else
        {
            ScanAFolderWhoseListingIsDenied();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ScanAFolderWhoseListingIsDenied()
    {
        var root = Directory.CreateTempSubdirectory("local-model-sync-denied-");
        var security = System.IO.FileSystemAclExtensions.GetAccessControl(root);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(deny);
        System.IO.FileSystemAclExtensions.SetAccessControl(root, security);
        try
        {
            Assert.Throws<LocalModelScanException>(() => new LocalModelScanner("runner-1", root.FullName).Scan());
        }
        finally
        {
            security.RemoveAccessRule(deny);
            System.IO.FileSystemAclExtensions.SetAccessControl(root, security);
            root.Delete(recursive: true);
        }
    }

    [Test]
    public void AnEmptyFolder_IsAnEmptyInventory()
    {
        var root = Directory.CreateTempSubdirectory("local-model-sync-").FullName;
        try
        {
            var snapshot = new LocalModelScanner("runner-1", root).Scan();

            Assert.That(snapshot.SelectionHashes, Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void TheInventory_DependsOnNamesSizesAndCategoriesOnly_NotOnTheFolderItIsIn()
    {
        // The same folder, scanned where it is and where a container mounts it.
        var host = Directory.CreateTempSubdirectory("local-model-sync-host-").FullName;
        var mounted = Directory.CreateTempSubdirectory("local-model-sync-mounted-").FullName;
        try
        {
            foreach (var root in new[] { host, mounted })
            {
                Directory.CreateDirectory(Path.Combine(root, "checkpoints", "sdxl"));
                Directory.CreateDirectory(Path.Combine(root, "LoRAs"));
                File.WriteAllText(Path.Combine(root, "checkpoints", "sdxl", "base.safetensors"), "checkpoint");
                File.WriteAllText(Path.Combine(root, "LoRAs", "style.safetensors"), "lora");
            }

            var fromHost = new LocalModelScanner("runner-1", host).Scan();
            var fromContainer = new LocalModelScanner("runner-1", mounted).Scan();

            Assert.Multiple(() =>
            {
                Assert.That(fromHost.SelectionHashes, Has.Count.EqualTo(2));
                Assert.That(fromHost.SelectionHashes, Is.EqualTo(fromContainer.SelectionHashes));
                Assert.That(fromHost.SelectionDigest, Is.EqualTo(fromContainer.SelectionDigest));
                Assert.That(fromHost.SelectionHashes, Does.Contain(Offloadr.Common.V1.LocalModelSelectionHash.Compute("style.safetensors", 4, Offloadr.Common.V1.ModelCategory.Lora)));
            });
        }
        finally
        {
            Directory.Delete(host, recursive: true);
            Directory.Delete(mounted, recursive: true);
        }
    }

    [Test]
    public async Task AScanThatFails_KeepsTheLastInventoryPublished_AndNeverPublishesItEmpty()
    {
        var originalKeepAlive = LocalModelSync.KeepAliveInterval;
        LocalModelSync.KeepAliveInterval = TimeSpan.FromMilliseconds(20);
        using var cts = new CancellationTokenSource();
        var requests = new List<SyncLocalModelsRequest>();
        var scans = 0;
        var good = Snapshot("a", "b");
        try
        {
            var loop = LocalModelSync.RunAsync(
                (request, _) =>
                {
                    lock (requests)
                    {
                        requests.Add(request.Clone());
                    }

                    return Task.FromResult(new SyncLocalModelsResponse { AcceptedCount = request.SelectionHashes.Count });
                },
                shutdown: cts.Token,
                runnerId: "runner-1",
                scanInterval: TimeSpan.FromMilliseconds(10),
                minBackoff: TimeSpan.FromMilliseconds(10),
                maxBackoff: TimeSpan.FromMilliseconds(10),
                snapshotProvider: () => Interlocked.Increment(ref scans) == 1 ? good : throw new LocalModelScanException("The drive is gone."),
                snapshotObserver: null,
                initialStatusApplied: Task.CompletedTask,
                logger: NullLogger.Instance);

            await WaitUntilAsync(() =>
            {
                lock (requests)
                {
                    return requests.Count >= 4 && Volatile.Read(ref scans) >= 4;
                }
            });
            cts.Cancel();
            await loop;
        }
        finally
        {
            LocalModelSync.KeepAliveInterval = originalKeepAlive;
        }

        Assert.Multiple(() =>
        {
            Assert.That(requests[0].FullSnapshot, Is.True);
            Assert.That(requests[0].SelectionHashes, Is.EquivalentTo(good.SelectionHashes));
            Assert.That(requests.Skip(1).Select(request => request.FullSnapshot), Has.All.False, "Kept alive, not replaced.");
            Assert.That(requests.Select(request => request.SnapshotDigest), Has.All.EqualTo(good.SelectionDigest));
        });
    }

    [Test]
    public async Task WithNoScanThatSucceeded_NothingIsPublished()
    {
        using var cts = new CancellationTokenSource();
        var sent = 0;
        var scans = 0;

        var loop = LocalModelSync.RunAsync(
            (_, _) =>
            {
                Interlocked.Increment(ref sent);
                return Task.FromResult(new SyncLocalModelsResponse());
            },
            shutdown: cts.Token,
            runnerId: "runner-1",
            scanInterval: TimeSpan.FromMilliseconds(10),
            minBackoff: TimeSpan.FromMilliseconds(5),
            maxBackoff: TimeSpan.FromMilliseconds(5),
            snapshotProvider: () =>
            {
                Interlocked.Increment(ref scans);
                throw new LocalModelScanException("The folder is not there.");
            },
            snapshotObserver: null,
            initialStatusApplied: Task.CompletedTask,
            logger: NullLogger.Instance);

        await WaitUntilAsync(() => Volatile.Read(ref scans) >= 3);
        cts.Cancel();
        await loop;

        Assert.That(sent, Is.Zero);
    }

    private static LocalModelSnapshot Snapshot(params string[] hashes)
    {
        var normalized = hashes.Select(hash => new string(hash[0], 64)).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
        return new LocalModelSnapshot([], string.Join(",", normalized), [], normalized, Offloadr.Common.V1.LocalModelSelectionHash.ComputeSnapshotDigest(normalized));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out.");
            }

            await Task.Delay(10);
        }
    }
}

using System.Globalization;
using System.Text;

namespace Offloadr.Runner.Tests;

internal static class Aria2AccountTestSupport
{
    public static async Task<Aria2ServiceAccount> ReadAccountAsync(LinuxTestUser user)
    {
        var group = await new LinuxCommandRunner().RunAsync(
            new LinuxCommand("/usr/bin/id", ["-g", user.Name]),
            CancellationToken.None);
        return new Aria2ServiceAccount(
            user.Name,
            user.UserId,
            uint.Parse(group.Stdout.Trim(), CultureInfo.InvariantCulture));
    }

    /// <summary>Returns "uid:gid:octal-mode" for <paramref name="path"/> without following a final link.</summary>
    public static async Task<string> StatAsync(string path)
    {
        var result = await new LinuxCommandRunner().RunAsync(
            new LinuxCommand("/usr/bin/stat", ["-c", "%u:%g:%a", path]),
            CancellationToken.None);
        return result.Stdout.Trim();
    }

    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aria2-account-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    public static void TryDelete(string path)
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
}

public class LinuxAria2FileAccessLinuxTests
{
    [Test]
    public void TryReadAccount_ReadsUidAndGidFromPasswd()
    {
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var passwd = Path.Combine(root, "passwd");
            File.WriteAllText(
                passwd,
                "root:x:0:0:root:/root:/bin/bash\noffloadr-aria2:x:62000:62001::/var/lib/aria2:/usr/sbin/nologin\n");

            Assert.Multiple(() =>
            {
                Assert.That(
                    LinuxAria2FileAccess.TryReadAccount("offloadr-aria2", passwd),
                    Is.EqualTo(new Aria2ServiceAccount("offloadr-aria2", 62000, 62001)));
                Assert.That(LinuxAria2FileAccess.TryReadAccount("missing", passwd), Is.Null);
                Assert.That(LinuxAria2FileAccess.TryReadAccount("offloadr-aria2", Path.Combine(root, "none")), Is.Null);
            });
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }

    [Test]
    public void ResolveAccount_MissingAccount_FailsOnlyWhenRequired()
    {
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var passwd = Path.Combine(root, "passwd");
            File.WriteAllText(passwd, "root:x:0:0:root:/root:/bin/bash\n");
            var optional = Aria2Settings.FromEnvironment(static _ => null, static () => "secret");
            var required = Aria2Settings.FromEnvironment(
                static name => name == "ARIA2_REQUIRE_USER" ? "1" : null,
                static () => "secret");

            Assert.Multiple(() =>
            {
                Assert.That(LinuxAria2FileAccess.ResolveAccount(optional, passwd, effectiveUserId: 0), Is.Null);
                Assert.That(
                    () => LinuxAria2FileAccess.ResolveAccount(required, passwd, effectiveUserId: 0),
                    Throws.InvalidOperationException.With.Message.Contains("offloadr-aria2"));
            });

            File.AppendAllText(passwd, "offloadr-aria2:x:62000:62000::/var/lib/aria2:/usr/sbin/nologin\n");
            Assert.Multiple(() =>
            {
                Assert.That(
                    LinuxAria2FileAccess.ResolveAccount(required, passwd, effectiveUserId: 0),
                    Is.EqualTo(new Aria2ServiceAccount("offloadr-aria2", 62000, 62000)));
                Assert.That(
                    () => LinuxAria2FileAccess.ResolveAccount(required, passwd, effectiveUserId: 1000),
                    Throws.InvalidOperationException);
                Assert.That(LinuxAria2FileAccess.ResolveAccount(optional, passwd, effectiveUserId: 1000), Is.Null);
            });
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }

    [Test]
    public void Constructor_DropsModelRootsInsideTheSessionRoot()
    {
        LinuxTestPrerequisites.RequireLinux();
        var access = new LinuxAria2FileAccess(
            null,
            ["/models", "/sessions", "/sessions/x/models", "/comfyui/models"],
            "/var/lib/aria2/staging",
            sessionRoot: "/sessions");

        Assert.That(
            access.WritableRoots,
            Is.EquivalentTo(new[] { "/models", "/comfyui/models", "/var/lib/aria2/staging" }));
    }

    [Test]
    public async Task PrepareStateDirectory_HandsAria2ItsFilesAndKeepsTheDirectoryRootOwned()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/stat");
        await using var user = await LinuxTestUser.CreateAsync();
        var account = await Aria2AccountTestSupport.ReadAccountAsync(user);
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var settings = Aria2Settings.FromEnvironment(
                name => name == "ARIA2_STATE_DIR" ? stateDirectory : null,
                static () => "rpc-secret-value");
            Directory.CreateDirectory(stateDirectory);
            var rpcConfig = Path.Combine(stateDirectory, "rpc.conf");
            File.WriteAllText(rpcConfig, "rpc-secret=rpc-secret-value\n");
            File.WriteAllText(settings.SessionFilePath, string.Empty);
            var access = new LinuxAria2FileAccess(account, [Path.Combine(root, "models")], settings.StagingDirectory);

            access.PrepareStateDirectory(settings, rpcConfig);

            var owned = $"{account.UserId}:{account.GroupId}";
            var state = await Aria2AccountTestSupport.StatAsync(stateDirectory);
            var rpc = await Aria2AccountTestSupport.StatAsync(rpcConfig);
            var session = await Aria2AccountTestSupport.StatAsync(settings.SessionFilePath);
            var stagingDirectory = await Aria2AccountTestSupport.StatAsync(settings.StagingDirectory);
            Assert.Multiple(() =>
            {
                Assert.That(state, Is.EqualTo($"0:{account.GroupId}:1770"));
                Assert.That(rpc, Is.EqualTo($"{owned}:600"));
                Assert.That(session, Does.StartWith($"{owned}:"));
                Assert.That(stagingDirectory, Is.EqualTo($"0:{account.GroupId}:770"));
            });
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }

    [Test]
    public async Task PrepareTransferTarget_GrantsGroupWriteOnTheDirectoryAndHandsOverTheTarget()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/stat");
        await using var user = await LinuxTestUser.CreateAsync();
        var account = await Aria2AccountTestSupport.ReadAccountAsync(user);
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var models = Path.Combine(root, "models");
            var directory = Directory.CreateDirectory(Path.Combine(models, "checkpoints", "nested")).FullName;
            var target = Path.Combine(directory, "model.safetensors");
            File.WriteAllBytes(target, new byte[16]);
            File.WriteAllBytes($"{target}.aria2", new byte[4]);
            var access = new LinuxAria2FileAccess(account, [models], Path.Combine(root, "staging"));

            access.PrepareTransferTarget(target);

            var owned = $"{account.UserId}:{account.GroupId}";
            var granted = await Aria2AccountTestSupport.StatAsync(directory);
            var parent = await Aria2AccountTestSupport.StatAsync(Path.Combine(models, "checkpoints"));
            var file = await Aria2AccountTestSupport.StatAsync(target);
            var control = await Aria2AccountTestSupport.StatAsync($"{target}.aria2");
            Assert.Multiple(() =>
            {
                Assert.That(granted, Is.EqualTo($"0:{account.GroupId}:775"));
                Assert.That(parent, Is.EqualTo("0:0:755"));
                // Sessions read models through the VFS and the editor exactly as before.
                Assert.That(file, Is.EqualTo($"{owned}:644"));
                Assert.That(control, Is.EqualTo($"{owned}:644"));
            });
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }

    [Test]
    public async Task PrepareTransferTarget_RefusesTargetsOutsideTheDownloadRoots()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/stat");
        await using var user = await LinuxTestUser.CreateAsync();
        var account = await Aria2AccountTestSupport.ReadAccountAsync(user);
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var models = Directory.CreateDirectory(Path.Combine(root, "models")).FullName;
            var sessions = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
            var sessionModels = Directory.CreateDirectory(Path.Combine(sessions, "home", "models")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(models, "linked"), outside);
            File.WriteAllBytes(Path.Combine(outside, "victim.bin"), new byte[4]);
            File.CreateSymbolicLink(Path.Combine(models, "leaf.bin"), Path.Combine(outside, "victim.bin"));
            var access = new LinuxAria2FileAccess(account, [models, sessions], Path.Combine(root, "staging"), sessionRoot: sessions);

            Assert.Multiple(() =>
            {
                Assert.That(
                    () => access.PrepareTransferTarget(Path.Combine(sessionModels, "model.bin")),
                    Throws.InvalidOperationException);
                Assert.That(
                    () => access.PrepareTransferTarget(Path.Combine(models, "linked", "victim.bin")),
                    Throws.InvalidOperationException);
                Assert.That(
                    () => access.PrepareTransferTarget(Path.Combine(models, "leaf.bin")),
                    Throws.InstanceOf<IOException>());
            });
            var sessionModelsStat = await Aria2AccountTestSupport.StatAsync(sessionModels);
            var outsideStat = await Aria2AccountTestSupport.StatAsync(outside);
            var victimStat = await Aria2AccountTestSupport.StatAsync(Path.Combine(outside, "victim.bin"));
            Assert.Multiple(() =>
            {
                Assert.That(sessionModelsStat, Is.EqualTo("0:0:755"));
                Assert.That(outsideStat, Is.EqualTo("0:0:755"));
                Assert.That(victimStat, Is.EqualTo("0:0:644"));
            });
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }

    [Test]
    public void PrepareTransferTarget_WithoutAccount_LeavesOwnershipAlone()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var access = new LinuxAria2FileAccess(null, [Path.Combine(root, "models")], Path.Combine(root, "staging"));

            Assert.DoesNotThrow(() => access.PrepareTransferTarget(Path.Combine(root, "elsewhere", "model.bin")));
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }
}

public class SessionModelDownloadStagingLinuxTests
{
    [Test]
    public void GetStagingPath_StagesOnlyDestinationsInsideTheSessionRoot()
    {
        LinuxTestPrerequisites.RequireLinux();
        var staging = new SessionModelDownloadStaging("/sessions", "/var/lib/aria2/staging");

        var staged = staging.GetStagingPath("/sessions/home/models/Lora/a.safetensors");

        Assert.Multiple(() =>
        {
            Assert.That(staging.GetStagingPath("/models/a.safetensors"), Is.Null);
            Assert.That(staging.GetStagingPath("/sessions"), Is.Null);
            Assert.That(staged, Does.StartWith("/var/lib/aria2/staging/"));
            Assert.That(Path.GetFileName(staged), Is.EqualTo("a.safetensors"));
            Assert.That(staging.GetStagingPath("/sessions/other/models/Lora/a.safetensors"), Is.Not.EqualTo(staged));
            Assert.That(staging.IsStagingPath(staged), Is.True);
        });
    }

    [Test]
    public void Publish_MovesTheStagedFileIntoTheSessionHome()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var sessions = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
            var destination = Path.Combine(sessions, "home", "models", "Lora", "a.safetensors");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, []);
            var staging = new SessionModelDownloadStaging(sessions, Path.Combine(root, "staging"));
            var staged = staging.GetStagingPath(destination)!;
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.WriteAllBytes(staged, "model-bytes"u8.ToArray());

            staging.Publish(staged, destination);

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo("model-bytes"u8.ToArray()));
                Assert.That(Directory.Exists(Path.GetDirectoryName(staged)), Is.False);
            });
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }

    [Test]
    public void Publish_DoesNotFollowADirectoryTheSessionSwappedForALink()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var sessions = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var models = Directory.CreateDirectory(Path.Combine(sessions, "home", "models")).FullName;
            var destination = Path.Combine(models, "Lora", "a.safetensors");
            var staging = new SessionModelDownloadStaging(sessions, Path.Combine(root, "staging"));
            var staged = staging.GetStagingPath(destination)!;
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.WriteAllBytes(staged, "model-bytes"u8.ToArray());
            Directory.CreateSymbolicLink(Path.Combine(models, "Lora"), outside);

            Assert.That(() => staging.Publish(staged, destination), Throws.InstanceOf<IOException>());
            Assert.Multiple(() =>
            {
                Assert.That(Directory.EnumerateFileSystemEntries(outside), Is.Empty);
                Assert.That(File.Exists(staged), Is.True);
            });
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }

    [Test]
    public void DiscardAndDiscardAll_RemoveStagedData()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var sessions = Path.Combine(root, "sessions");
            var stagingDirectory = Path.Combine(root, "staging");
            var staging = new SessionModelDownloadStaging(sessions, stagingDirectory);
            var first = staging.GetStagingPath(Path.Combine(sessions, "home", "models", "a.bin"))!;
            var second = staging.GetStagingPath(Path.Combine(sessions, "home", "models", "b.bin"))!;
            foreach (var staged in new[] { first, second })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.WriteAllBytes(staged, [1]);
                File.WriteAllBytes($"{staged}.aria2", [1]);
            }

            staging.Discard(Path.Combine(sessions, "home", "models", "a.bin"));
            Assert.That(Directory.Exists(Path.GetDirectoryName(first)), Is.False);
            Assert.That(File.Exists(second), Is.True);

            staging.DiscardAll();
            Assert.That(Directory.EnumerateFileSystemEntries(stagingDirectory), Is.Empty);
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }
}

public partial class Aria2ModelTransferBackendLinuxTests
{
    [Test]
    [Category("RealAria2")]
    public async Task RealAria2_RunsAsServiceAccountAndDownloadsIntoPreparedTargets()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand(GetAria2BinaryPath());
        LinuxTestPrerequisites.RequireCommand("/usr/bin/stat");
        await using var user = await LinuxTestUser.CreateAsync();
        var account = await Aria2AccountTestSupport.ReadAccountAsync(user);
        var root = Aria2AccountTestSupport.CreateRoot();
        try
        {
            var content = Enumerable.Range(0, (1024 * 1024) + 17)
                .Select(static value => unchecked((byte)(value * 7)))
                .ToArray();
            await using var source = new RangeHttpServer(content);
            var settings = CreateSettings(root, GetAvailableTcpPort());
            var sessions = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
            var access = new LinuxAria2FileAccess(
                account,
                [settings.DownloadDirectory, sessions],
                settings.StagingDirectory,
                sessionRoot: sessions);
            await using var backend = new Aria2DownloadBackend(settings, fileAccess: access);
            await backend.StartAsync(CancellationToken.None);

            var status = await File.ReadAllLinesAsync($"/proc/{backend.ProcessId}/status");
            var uids = status.Single(static line => line.StartsWith("Uid:", StringComparison.Ordinal))
                .Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(static value => uint.Parse(value, CultureInfo.InvariantCulture));
            Assert.That(uids, Is.All.EqualTo(account.UserId));

            // A root-created placeholder and resume state, as hydration leaves them.
            var directory = Directory.CreateDirectory(Path.Combine(settings.DownloadDirectory, "checkpoints")).FullName;
            var destination = Path.Combine(directory, "model.bin");
            await using (var placeholder = File.Create(destination))
            {
                placeholder.SetLength(content.Length);
            }

            var handles = await backend.CreateAsync(
                new ModelTransferCreateRequest(
                    destination,
                    content.Length,
                    [source.GetUri("model.bin")],
                    MetalinkContent: null,
                    PreferredIdentifier: ModelTransferIdentity.Create("model", destination, content.Length).DeterministicIdentifier,
                    StartPaused: false,
                    QueuePosition: null,
                    ResumeMode: ModelTransferResumeMode.InitializeEmpty),
                CancellationToken.None);
            var completed = await WaitForStatusAsync(
                backend,
                handles.Single(),
                static snapshot => snapshot.IsStopped,
                TimeSpan.FromSeconds(20));

            Assert.That(completed.IsComplete, Is.True, completed.ErrorMessage);
            Assert.That(await File.ReadAllBytesAsync(destination), Is.EqualTo(content));
            Assert.That(await Aria2AccountTestSupport.StatAsync(destination), Is.EqualTo($"{account.UserId}:{account.GroupId}:644"));

            // A destination in a session home is refused in place and staged instead.
            var sessionDestination = Path.Combine(sessions, "home", "models", "Lora", "lora.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(sessionDestination)!);
            Assert.That(
                async () => await backend.CreateAsync(
                    new ModelTransferCreateRequest(sessionDestination, content.Length, [source.GetUri("lora.bin")], null, null, false, null),
                    CancellationToken.None),
                Throws.InvalidOperationException);

            var staging = new SessionModelDownloadStaging(sessions, settings.StagingDirectory);
            var staged = staging.GetStagingPath(sessionDestination)!;
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            var stagedHandles = await backend.CreateAsync(
                new ModelTransferCreateRequest(staged, content.Length, [source.GetUri("lora.bin")], null, null, false, null),
                CancellationToken.None);
            var stagedStatus = await WaitForStatusAsync(
                backend,
                stagedHandles.Single(),
                static snapshot => snapshot.IsStopped,
                TimeSpan.FromSeconds(20));
            Assert.That(stagedStatus.IsComplete, Is.True, stagedStatus.ErrorMessage);

            staging.Publish(staged, sessionDestination);

            Assert.That(await File.ReadAllBytesAsync(sessionDestination), Is.EqualTo(content));
            Assert.That(Encoding.ASCII.GetString(await File.ReadAllBytesAsync(Path.Combine(settings.StateDirectory, "rpc.conf"))), Does.StartWith("rpc-secret="));
        }
        finally
        {
            Aria2AccountTestSupport.TryDelete(root);
        }
    }
}

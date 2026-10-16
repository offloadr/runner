using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public class SealedHostLockdownLinuxTests
{
    private const UnixFileMode Mode0755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void FindSharedWritableDirectories_FindsWorldWritableDirectoriesWithoutFollowingLinks()
    {
        LinuxTestPrerequisites.RequireLinux();

        var root = Path.Combine(Path.GetTempPath(), $"runner-lockdown-{Guid.NewGuid():N}");
        try
        {
            var shallow = Path.Combine(root, "tmp");
            var nested = Path.Combine(root, "var", "crash");
            var deep = Path.Combine(root, "a", "b", "c", "d");
            var inside = Path.Combine(shallow, "inside");
            foreach (var directory in new[] { shallow, nested, deep, inside })
            {
                Directory.CreateDirectory(directory);
                File.SetUnixFileMode(directory, Mode0755 | UnixFileMode.OtherWrite);
            }

            Directory.CreateSymbolicLink(Path.Combine(root, "link"), shallow);

            var found = SealedHostLockdown.FindSharedWritableDirectories(root);

            // A world-writable directory is reported once; what it contains is its own concern.
            Assert.That(found, Is.EquivalentTo(new[] { shallow, nested }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Inventory of the disk-backed places a fresh session user can write on the root filesystem
    /// of this image: every one of them must be something the lockdown closes in required mode.
    /// </summary>
    [Test]
    public async Task SessionUser_CanOnlyWriteDirectoriesTheLockdownCloses()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/find");

        await using var user = await LinuxTestUser.CreateAsync();
        var startInfo = new ProcessStartInfo("/usr/bin/find")
        {
            UserName = user.Name,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = "/",
        };
        foreach (var argument in new[] { "/", "-xdev", "-writable", "-prune", "-print" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var writable = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var closed = SealedHostLockdown.SharedTemporaryDirectories
            .Concat(SealedHostLockdown.FindSharedWritableDirectories())
            .ToHashSet(StringComparer.Ordinal);
        // Sockets and other non-directories the user may write cannot hold files. A link such as
        // /var/lock -> /run/lock is closed when its target is.
        var unexpected = writable
            .Where(path => Directory.Exists(path) && !closed.Contains(LinuxPathCanonicalizer.ResolveExistingPath(path)))
            .ToArray();

        Assert.That(unexpected, Is.Empty, "disk-backed directories a session user can write: " + string.Join(", ", unexpected));
    }
}

using System.Runtime.InteropServices;

namespace Offloadr.Runner.Tests;

public class LinuxSecureDirectoryRootReplaceLinuxTests
{
    [Test]
    public void CommitReplacing_WhileUnopened_LeavesAFileOpenOrMappedElsewhere()
    {
        LinuxTestPrerequisites.RequireLinux();
        // On tmpfs, as sealed sessions are: overlayfs hands a mapping to the file beneath, so
        // the file opened through it is no longer held once its descriptor closes.
        if (SealedStorageProbe.ReadFileSystemStats("/dev/shm") is not { Type: SealedStorageProbe.TmpfsMagic })
        {
            Assert.Ignore("/dev/shm is not a tmpfs here.");
        }

        var root = CreateRoot("/dev/shm");
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;

            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write))
            using (var placeholder = secureRoot.CreatePublication("a.png"))
            {
                Assert.That(placeholder.CommitReplacing(version, whileUnopened: true), Is.False, "a writer still has it");
            }

            // Mapped writable and shared, then closed: the mapping still writes to it.
            var descriptor = open(path, 2);
            Assert.That(descriptor, Is.GreaterThanOrEqualTo(0));
            var mapping = mmap(0, 8, 3, 1, descriptor, 0);
            _ = close(descriptor);
            Assert.That(mapping, Is.Not.EqualTo((nint)(-1)));
            try
            {
                using var placeholder = secureRoot.CreatePublication("a.png");
                Assert.That(placeholder.CommitReplacing(version, whileUnopened: true), Is.False, "a mapping still has it");
            }
            finally
            {
                _ = munmap(mapping, 8);
            }

            Assert.That(File.ReadAllText(path), Is.EqualTo("uploaded"));
            using (var placeholder = secureRoot.CreatePublication("a.png"))
            {
                Assert.That(placeholder.CommitReplacing(version, whileUnopened: true), Is.True, "nothing has it now");
            }

            Assert.That(new FileInfo(path).Length, Is.Zero);
            Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CommitReplacing_WhileUnopened_SwapsBackForAnOpenDuringTheExchange()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            using var placeholder = secureRoot.CreatePublication("a.png");

            // A workflow opens it for writing just as it is swapped: the lease holds that open.
            Thread? opener = null;
            placeholder.BeforeExchange = () =>
            {
                opener = new Thread(() =>
                {
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write);
                    writer.Write("UP"u8);
                });
                opener.Start();
                Thread.Sleep(300);
            };

            Assert.That(placeholder.CommitReplacing(version, whileUnopened: true), Is.False);
            Assert.That(opener!.Join(TimeSpan.FromSeconds(10)), Is.True, "the open goes on once the lease is let go");
            Assert.That(File.ReadAllText(path), Is.EqualTo("UPloaded"), "it wrote to the file that stayed");
            Assert.That(secureRoot.GetVersion("a.png")!.Value.Inode, Is.EqualTo(version.Inode));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CommitReplacing_ReplacesTheExpectedVersion()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.png"), "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;

            using (var placeholder = secureRoot.CreatePublication("a.png"))
            {
                Assert.That(placeholder.CommitReplacing(version), Is.True);
            }

            Assert.That(new FileInfo(Path.Combine(root, "a.png")).Length, Is.Zero);
            Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1), "the displaced file is gone");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public void CommitReplacing_KeepsARewriteThatLandedAfterTheCheck(bool throughRename, bool rightBeforeTheExchange)
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            var modified = File.GetLastWriteTimeUtc(path);
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            using var placeholder = secureRoot.CreatePublication("a.png");

            // The editor saves a new version of the same length and time, by an atomic rename
            // (new inode) or in place, after the caller's check or even after the last one.
            void Rewrite()
            {
                if (throughRename)
                {
                    var saved = Path.Combine(root, "a.png.new");
                    File.WriteAllText(saved, "rewrite!");
                    File.SetLastWriteTimeUtc(saved, modified);
                    File.Move(saved, path, overwrite: true);
                }
                else
                {
                    File.WriteAllText(path, "rewrite!");
                    File.SetLastWriteTimeUtc(path, modified.AddSeconds(1));
                }
            }

            if (rightBeforeTheExchange)
            {
                placeholder.BeforeExchange = Rewrite;
            }
            else
            {
                Rewrite();
            }

            Assert.That(placeholder.CommitReplacing(version), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewrite!"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CommitReplacing_KeepsAnInPlaceRewriteThatRestoresLengthAndTime()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            var modified = File.GetLastWriteTimeUtc(path);
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            using var placeholder = secureRoot.CreatePublication("a.png");

            // Same inode, same length, modification time set back (cp -p would do this): only the
            // change time tells the new bytes from the uploaded ones.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                stream.Write("rewrite!"u8);
            }

            File.SetLastWriteTimeUtc(path, modified);

            Assert.That(placeholder.CommitReplacing(version), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewrite!"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CommitReplacing_KeepsANewerRewriteThatLandsDuringTheRestore()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            using var placeholder = secureRoot.CreatePublication("a.png");

            // A first rewrite right before the exchange, then a second one saved over the placeholder
            // right after it: the second is the newest and must survive.
            placeholder.BeforeExchange = () => ReplaceAtomically(root, path, "rewrite-1");
            placeholder.AfterExchange = () => ReplaceAtomically(root, path, "rewrite-2");

            Assert.That(placeholder.CommitReplacing(version), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewrite-2"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void CommitReplacing_KeepsAnInPlaceRewriteOfThePlaceholderDuringTheRestore()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            using var placeholder = secureRoot.CreatePublication("a.png");

            // A rewrite right before the exchange, then the editor opens the placeholder now at the
            // destination and saves into it in place (same inode): that save must survive.
            placeholder.BeforeExchange = () => ReplaceAtomically(root, path, "rewrite-1");
            placeholder.AfterExchange = () => File.WriteAllText(path, "rewrite-2");

            Assert.That(placeholder.CommitReplacing(version), Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewrite-2"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void CreateDirectoryNoFollow_LeavesALinkInItsPlaceAndItsTargetAlone()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var raced = Path.Combine(root, "raced");
            Directory.CreateSymbolicLink(raced, target);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            Assert.That(LinuxSecureDirectoryRoot.CreateDirectoryNoFollow(raced, mode), Is.False);
            Assert.That(File.GetUnixFileMode(target), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "the link's target is untouched");

            var fresh = Path.Combine(root, "fresh");
            Assert.That(LinuxSecureDirectoryRoot.CreateDirectoryNoFollow(fresh, mode), Is.True);
            Assert.That(File.GetUnixFileMode(fresh), Is.EqualTo(mode), "exactly the mode asked for, whatever the umask");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void CommitIfAbsent_LeavesAFileThatAppearedMeanwhile()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "dynamic.png");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            using (var download = secureRoot.CreatePublication("dynamic.png"))
            {
                // The workflow writes the input while the download is in flight.
                File.WriteAllText(path, "written meanwhile");
                Assert.That(download.CommitIfAbsent(), Is.False);
            }

            Assert.That(File.ReadAllText(path), Is.EqualTo("written meanwhile"));

            var absent = Path.Combine(root, "absent.png");
            using (var download = secureRoot.CreatePublication("absent.png"))
            {
                Assert.That(download.CommitIfAbsent(), Is.True);
            }

            Assert.That(File.Exists(absent), Is.True);
            Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(2), "no temporary file is left");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DeleteFileUnopened_RemovesOnlyTheExpectedVersionWhileNothingHasItOpen()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.That(secureRoot.DeleteFileUnopened("a.png", version, out _), Is.False, "a reader still has it");
            }

            File.WriteAllText(path, "rewritten");
            Assert.That(secureRoot.DeleteFileUnopened("a.png", version, out _), Is.False, "it is no longer that version");
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewritten"));

            Assert.That(secureRoot.DeleteFileUnopened("a.png", secureRoot.GetVersion("a.png")!.Value, out _), Is.True);
            Assert.That(Directory.GetFileSystemEntries(root), Is.Empty, "nothing is left, under any name");
            Assert.That(secureRoot.DeleteFileUnopened("a.png", version, out _), Is.False, "nothing is there");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DeleteFileUnopened_PutsTheFileBackForAnOpenDuringTheRemoval()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;

            // A workflow opens it for writing just as it is moved aside: it looked the name up
            // before the move, and the lease holds its open until the file is back.
            Thread? opener = null;
            using var looked = new ManualResetEventSlim();
            secureRoot.BeforeMoveAside = () =>
            {
                opener = new Thread(() =>
                {
                    looked.Set();
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write);
                    writer.Write("UP"u8);
                });
                opener.Start();
                looked.Wait();
                Thread.Sleep(300);
            };

            Assert.That(secureRoot.DeleteFileUnopened("a.png", version, out var restored), Is.False);
            Assert.That(opener!.Join(TimeSpan.FromSeconds(10)), Is.True, "the open goes on once the lease is let go");
            Assert.That(File.ReadAllText(path), Is.EqualTo("UPloaded"), "it wrote to the file that stayed");
            Assert.That(secureRoot.GetVersion("a.png")!.Value.Inode, Is.EqualTo(version.Inode));
            Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1));
            // As it was put back, before the writer went on: the same file, its change time moved.
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored!.Value.IsSameFileAs(version), Is.True);
            Assert.That(restored.Value, Is.Not.EqualTo(version));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DeleteFileUnopened_KeepsAFileTheSessionPutThereDuringTheRemoval()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            secureRoot.BeforeMoveAside = () => ReplaceAtomically(root, path, "rewrite");

            Assert.That(secureRoot.DeleteFileUnopened("a.png", version, out var restored), Is.False);
            Assert.That(restored, Is.Null, "what went back is not the file that was to be removed");
            Assert.That(File.ReadAllText(path), Is.EqualTo("rewrite"), "the newer file is back under its name");
            Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DeleteFileUnopened_KeepsBothFilesWhenTheNameIsTakenWhileANewerFileIsAside()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;
            // The session replaces the file after the last check, and something creates the name
            // again in the moment the replacement is aside.
            secureRoot.BeforeMoveAside = () => ReplaceAtomically(root, path, "rewrite");
            secureRoot.AfterMoveAside = () => File.WriteAllText(path, "created");

            Assert.That(secureRoot.DeleteFileUnopened("a.png", version, out var restored), Is.False);
            Assert.That(restored, Is.Null);
            Assert.That(File.ReadAllText(path), Is.EqualTo("created"));
            var others = Directory.GetFiles(root).Where(file => file != path).ToArray();
            Assert.That(others, Has.Length.EqualTo(1), "the file that could not go back is kept, not removed");
            Assert.That(File.ReadAllText(others[0]), Is.EqualTo("rewrite"));
            Assert.That(Path.GetFileName(others[0]), Does.StartWith("a.kept-").And.EndsWith(".png"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void DeleteFileUnopened_RemovesTheExpectedFileWhenItsNameWasTakenWhileItWasOpened()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            File.WriteAllText(path, "uploaded");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            var version = secureRoot.GetVersion("a.png")!.Value;

            // A reader opens it just as it is moved aside, and something creates the name again.
            Thread? opener = null;
            string? read = null;
            using var looked = new ManualResetEventSlim();
            secureRoot.BeforeMoveAside = () =>
            {
                opener = new Thread(() =>
                {
                    looked.Set();
                    read = File.ReadAllText(path);
                });
                opener.Start();
                looked.Wait();
                Thread.Sleep(300);
            };
            secureRoot.AfterMoveAside = () => File.WriteAllText(path, "created");

            Assert.That(secureRoot.DeleteFileUnopened("a.png", version, out _), Is.True, "it cannot go back, and it is the file that was to go");
            Assert.That(opener!.Join(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(read, Is.EqualTo("uploaded").Or.EqualTo("created"), "the reader keeps whichever file it opened");
            Assert.That(File.ReadAllText(path), Is.EqualTo("created"));
            Assert.That(Directory.GetFiles(root), Has.Length.EqualTo(1), "no copy of the removed file is left under another name");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Publication_ReportsItsOwnFileNotWhateverTookTheDestinationSince()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "a.png");
            using var secureRoot = new LinuxSecureDirectoryRoot(root);
            using var publication = secureRoot.CreatePublication("a.png");
            using (var stream = publication.OpenWriteStream())
            {
                stream.Write("downloaded"u8);
            }

            var written = publication.GetVersion()!.Value;
            Assert.That(publication.CommitIfAbsent(), Is.True);
            Assert.That(publication.GetVersion()!.Value.IsSameFileAs(written), Is.True, "publishing moves only the change time");
            Assert.That(secureRoot.GetVersion("a.png"), Is.EqualTo(publication.GetVersion()));

            // The session puts its own file there right after.
            ReplaceAtomically(root, path, "rewritten!");
            Assert.That(secureRoot.GetVersion("a.png"), Is.Not.EqualTo(publication.GetVersion()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplaceAtomically(string root, string path, string content)
    {
        var staged = Path.Combine(root, $"staged-{Guid.NewGuid():N}");
        File.WriteAllText(staged, content);
        File.Move(staged, path, overwrite: true);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint address, nuint length, int protection, int flags, int descriptor, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(nint address, nuint length);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    private static string CreateRoot(string? parent = null)
    {
        var root = Path.Combine(parent ?? Path.GetTempPath(), $"runner-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}

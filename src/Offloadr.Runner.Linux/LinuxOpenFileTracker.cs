using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Tells whether a file below some directories may still be open in any process, from inotify's
/// open and close events. Eviction replaces a file with a placeholder; a process still holding
/// it, a writer above all, would go on with an inode nobody can reach again, so such a file is
/// left alone. Errs towards "may be open": a file whose opens it may have missed (renamed in from
/// an unwatched folder, or in a directory watched only after it appeared) stays so for good,
/// since no close seen later can be told from that unseen handle's; after a lost event queue
/// every file does.
/// </summary>
internal sealed class LinuxOpenFileTracker : IDisposable
{
    private const uint InOpen = 0x20;
    private const uint InCloseWrite = 0x08;
    private const uint InCloseNoWrite = 0x10;
    private const uint InMovedFrom = 0x40;
    private const uint InMovedTo = 0x80;
    private const uint InCreate = 0x100;
    private const uint InQueueOverflow = 0x4000;
    private const uint InIgnored = 0x8000;
    private const uint InIsDirectory = 0x40000000;
    private const int InNonBlock = 0x800;
    private const int InCloseOnExec = 0x80000;
    private const short PollIn = 0x1;
    private const int TryAgain = 11;

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _directories = [];
    // Opens seen minus closes; -1 for a file that may have been opened before it was watched.
    private readonly Dictionary<string, int> _openCounts = new(StringComparer.Ordinal);
    // A file renamed within the watched folders keeps its count: its MOVED_FROM half, by cookie.
    private readonly Dictionary<uint, int> _movingCounts = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly int _descriptor;
    private readonly Thread? _reader;
    private bool _uncertain;

    public LinuxOpenFileTracker(IEnumerable<string> roots)
    {
        _descriptor = OperatingSystem.IsLinux() ? inotify_init1(InNonBlock | InCloseOnExec) : -1;
        if (_descriptor < 0)
        {
            _uncertain = true;
            return;
        }

        foreach (var root in roots)
        {
            Watch(Path.GetFullPath(root), existedBefore: false);
        }

        _reader = new Thread(ReadEvents) { IsBackground = true, Name = "artifact-open-tracker" };
        _reader.Start();
    }

    /// <summary>Whether <paramref name="path"/> may still be open somewhere.</summary>
    public bool MayBeOpen(string path)
    {
        lock (_gate)
        {
            return _uncertain || (_openCounts.TryGetValue(Path.GetFullPath(path), out var count) && count != 0);
        }
    }

    private int _disposed;

    private void Watch(string directory, bool existedBefore)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var watch = inotify_add_watch(_descriptor, directory, InOpen | InCloseWrite | InCloseNoWrite | InCreate | InMovedFrom | InMovedTo);
        lock (_gate)
        {
            if (watch < 0)
            {
                // Too many watches, or the directory went: what happens there is unknown.
                _uncertain = true;
                return;
            }

            _directories[watch] = directory;
            if (existedBefore)
            {
                // Created before this watch: its files may have been opened unseen.
                foreach (var file in SafeEntries(directory, directories: false))
                {
                    _openCounts.TryAdd(file, -1);
                }
            }
        }

        foreach (var child in SafeEntries(directory, directories: true))
        {
            Watch(child, existedBefore);
        }
    }

    private static string[] SafeEntries(string directory, bool directories)
    {
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            return directories ? Directory.GetDirectories(directory, "*", options) : Directory.GetFiles(directory, "*", options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void ReadEvents()
    {
        var buffer = new byte[64 * 1024];
        var poll = new PollDescriptor { Descriptor = _descriptor, Events = PollIn };
        while (!_stop.IsCancellationRequested)
        {
            if (poll_(ref poll, 1, 250) <= 0)
            {
                continue;
            }

            var read = ReadInto(buffer);
            if (read < 0)
            {
                if (Marshal.GetLastPInvokeError() == TryAgain)
                {
                    continue;
                }

                lock (_gate)
                {
                    _uncertain = true;
                }

                return;
            }

            Handle(buffer.AsSpan(0, read));
        }
    }

    private int ReadInto(byte[] buffer)
        => (int)read(_descriptor, buffer, (nuint)buffer.Length);

    private void Handle(ReadOnlySpan<byte> events)
    {
        var offset = 0;
        // struct inotify_event: int wd; uint32 mask; uint32 cookie; uint32 len; char name[len].
        while (offset + 16 <= events.Length)
        {
            var watch = BitConverter.ToInt32(events[offset..]);
            var mask = BitConverter.ToUInt32(events[(offset + 4)..]);
            var cookie = BitConverter.ToUInt32(events[(offset + 8)..]);
            var length = (int)BitConverter.ToUInt32(events[(offset + 12)..]);
            var name = length > 0 ? System.Text.Encoding.UTF8.GetString(events.Slice(offset + 16, length)).TrimEnd('\0') : string.Empty;
            offset += 16 + length;

            if ((mask & InQueueOverflow) != 0)
            {
                lock (_gate)
                {
                    _uncertain = true;
                }

                continue;
            }

            string? directory;
            lock (_gate)
            {
                if ((mask & InIgnored) != 0)
                {
                    _directories.Remove(watch);
                    continue;
                }

                if (!_directories.TryGetValue(watch, out directory) || name.Length == 0)
                {
                    continue;
                }
            }

            var path = Path.Combine(directory, name);
            if ((mask & InIsDirectory) != 0)
            {
                if ((mask & (InCreate | InMovedTo)) != 0)
                {
                    Watch(path, existedBefore: true);
                }

                continue;
            }

            lock (_gate)
            {
                if ((mask & InMovedFrom) != 0)
                {
                    // Its count moves with it, if it lands in a watched folder.
                    _movingCounts[cookie] = _openCounts.Remove(path, out var moving) ? moving : 0;
                    if (_movingCounts.Count > 4096)
                    {
                        _movingCounts.Clear();
                    }
                }
                else if ((mask & InMovedTo) != 0)
                {
                    // Renamed in from outside, an open handle may come with it: possibly open.
                    var count = _movingCounts.Remove(cookie, out var moved) ? moved : -1;
                    if (count != 0)
                    {
                        _openCounts[path] = count;
                    }
                    else
                    {
                        _openCounts.Remove(path);
                    }
                }
                else if ((mask & InOpen) != 0)
                {
                    // An unseen handle stays unknown: opens and closes seen since (the uploader's
                    // own read, say) say nothing about it.
                    if (!_openCounts.TryGetValue(path, out var count) || count >= 0)
                    {
                        _openCounts[path] = Math.Max(count, 0) + 1;
                    }
                }
                else if ((mask & (InCloseWrite | InCloseNoWrite)) != 0)
                {
                    if (_openCounts.TryGetValue(path, out var count) && count > 1)
                    {
                        _openCounts[path] = count - 1;
                    }
                    else if (count >= 0)
                    {
                        _openCounts.Remove(path);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _reader?.Join(TimeSpan.FromSeconds(2));
        if (_descriptor >= 0)
        {
            _ = close(_descriptor);
        }

        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_init1(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_add_watch(int descriptor, string path, uint mask);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int descriptor, [Out] byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "poll")]
    private static extern int poll_(ref PollDescriptor descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}

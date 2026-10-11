namespace Offloadr.Runner.Supervisor;

/// <summary>
/// Identifies the app's data folder on the runner containers its supervisor starts. A copy of the app
/// with another data folder (another account, or another computer using the same remote engine) holds
/// no shared lock with this one, so the label is what keeps it from adopting or stopping this one's
/// runner, while this copy still finds its own runner again after a crash.
/// </summary>
public sealed class SupervisorOwner(string id)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    private const int Attempts = 100;

    /// <summary>The ID a supervisor uses when none is given, as in a single-copy setup such as tests.</summary>
    public static SupervisorOwner Default { get; } = new("default");

    public string Id { get; } = id;

    /// <summary>
    /// The ID kept in <paramref name="path"/>, created the first time (or when the file holds no ID).
    /// Only a copy of the app holding the lock next to it writes the file, and it writes the ID in full
    /// before moving it into place, so copies opening at the same moment all read the same ID and none
    /// replaces a file another is still writing. One that cannot be saved is still unique; only its
    /// runner is then not found again after a crash, and is left alone.
    /// </summary>
    public static SupervisorOwner LoadOrCreate(string path)
    {
        var created = Guid.NewGuid().ToString("N");
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            // Read first on every pass: while another copy holds the lock, its ID shows up here.
            if (Read(path, out var stored) == Stored.Valid)
            {
                return new SupervisorOwner(stored!);
            }

            switch (TryCreateLocked(path, created, out var owner))
            {
                case Creation.Done:
                    return owner!;
                case Creation.Impossible:
                    return new SupervisorOwner(created);
                default:
                    // Another copy is creating it, or a scanner holds it.
                    Thread.Sleep(RetryDelay);
                    break;
            }
        }

        return new SupervisorOwner(created);
    }

    private enum Creation
    {
        Done,
        Busy,
        Impossible,
    }

    /// <summary>
    /// Under the lock next to the ID file, reads it once more and writes it if it is missing or holds
    /// no ID. The open handle is the lock, so a crash never leaves it held.
    /// </summary>
    private static Creation TryCreateLocked(string path, string created, out SupervisorOwner? owner)
    {
        owner = null;
        FileStream creating;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            creating = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException)
        {
            return Creation.Impossible;
        }
        catch (IOException)
        {
            return Creation.Busy;
        }

        using (creating)
        {
            switch (Read(path, out var stored))
            {
                case Stored.Valid:
                    // Written by the copy that held the lock before this one.
                    owner = new SupervisorOwner(stored!);
                    return Creation.Done;
                case Stored.Unreadable:
                    // A scanner may briefly hold the file; it is never replaced for that.
                    return Creation.Busy;
                default:
                    // Missing, or holding no ID (a crash while an older version wrote it, say). No
                    // other copy writes it while this one holds the lock.
                    TryWrite(path, created);
                    owner = new SupervisorOwner(created);
                    return Creation.Done;
            }
        }
    }

    private enum Stored
    {
        Missing,
        Valid,
        Invalid,
        Unreadable,
    }

    private static Stored Read(string path, out string? id)
    {
        id = null;
        try
        {
            if (!File.Exists(path))
            {
                return Stored.Missing;
            }

            var text = File.ReadAllText(path).Trim();
            if (!Guid.TryParseExact(text, "N", out _))
            {
                return Stored.Invalid;
            }

            id = text;
            return Stored.Valid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Stored.Unreadable;
        }
    }

    /// <summary>Writes the ID in full next to the file, then moves it into place, so a reader never sees it half written.</summary>
    private static void TryWrite(string path, string id)
    {
        var temporary = $"{path}.{id}.tmp";
        try
        {
            File.WriteAllText(temporary, id);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a leftover temporary file.
        }
    }
}

namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>
/// Carries "the runner is wanted" across a restart to update. The restart leaves a running runner
/// for the new version to reconnect to, but the runner can exit, or be waiting to restart, at the
/// moment the app goes down: then there is nothing to reconnect to, and the restart the old version
/// had scheduled would be lost. The old version records that the runner was wanted; the new one
/// starts it unless it reconnected to one.
/// </summary>
public static class UpdateHandoff
{
    /// <summary>
    /// Records, as the app exits to update, whether the runner was wanted
    /// (<see cref="RunnerSupervisor.RunnerWanted"/>, read once the supervisor has stopped).
    /// </summary>
    public static void Save(SupervisorPaths paths, bool runnerWanted)
    {
        try
        {
            if (runnerWanted)
            {
                Directory.CreateDirectory(paths.Data);
                File.WriteAllText(paths.ResumeAfterUpdate, string.Empty);
            }
            else
            {
                File.Delete(paths.ResumeAfterUpdate);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: without the note, a runner that was running is still reconnected to.
        }
    }

    /// <summary>True, once, when the version that restarted to update wanted the runner.</summary>
    public static bool TakeResume(SupervisorPaths paths)
    {
        try
        {
            if (!File.Exists(paths.ResumeAfterUpdate))
            {
                return false;
            }

            File.Delete(paths.ResumeAfterUpdate);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

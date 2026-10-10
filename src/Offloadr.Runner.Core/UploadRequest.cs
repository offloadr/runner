namespace Offloadr.Runner.Core;

/// <summary>
/// One queued artifact upload. <paramref name="Runtime"/> is the session's runtime when the
/// file was queued: the upload is labelled with it even if the runtime has since exited or
/// been replaced.
/// </summary>
internal readonly record struct UploadRequest(string Type, string FullPath, int Attempt, RuntimeIdentity? Runtime = null);

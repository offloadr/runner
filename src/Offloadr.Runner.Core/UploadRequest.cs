namespace Offloadr.Runner.Core;

internal readonly record struct UploadRequest(string Type, string FullPath, int Attempt);

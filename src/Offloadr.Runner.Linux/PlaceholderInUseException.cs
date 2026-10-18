namespace Offloadr.Runner.Linux;

/// <summary>
/// A download was not put in its placeholder's place because some process has the placeholder
/// open: the placeholder stays one, and a later open downloads it again.
/// </summary>
internal sealed class PlaceholderInUseException()
    : IOException("The placeholder is open in another process; it is downloaded again when next opened.");

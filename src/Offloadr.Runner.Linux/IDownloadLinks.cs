namespace Offloadr.Runner.Linux;

/// <summary>
/// The editor links downloads saved in the local folder go through. They point into the local folder as
/// projections do, so the projector asks before it changes a link there.
/// </summary>
internal interface IDownloadLinks
{
    /// <summary>
    /// Whether a session that has not ended sent a download through the link at <paramref name="editorPath"/>,
    /// and the link still points at that download.
    /// </summary>
    bool KeepsEditorLink(string editorPath);

    /// <summary>
    /// Runs <paramref name="change"/> while no download can claim, make or remove a link, so a link found
    /// unclaimed in it stays unclaimed until the change is made.
    /// </summary>
    void WhileLinksAreHeld(Action change);

    /// <summary>
    /// Records whether the projector links a local model at <paramref name="editorPath"/> for the session now:
    /// a download of another model is never sent there in its place.
    /// </summary>
    void SetProjected(string editorPath, bool projected);
}

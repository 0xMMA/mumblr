namespace Mumblr.App.Updates;

/// <summary>
/// What the version button is busy with. The check downloads the package as part of answering, and
/// for a self-contained build that is the long part - minutes on a slow line. Without this the
/// button kept reading the running version throughout, so the only thing to do with it was click
/// it again.
/// </summary>
public enum UpdateActivity
{
    None,
    Checking,
    Downloading,
    Installing,
}

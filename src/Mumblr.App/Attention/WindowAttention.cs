namespace Mumblr.App.Attention;

/// <summary>
/// The focus logic behind the taskbar flash, kept apart from the window so it can be tested
/// without one. Flashing is on exactly while attention is wanted and the window is not in front;
/// Windows itself stops the flash when the window comes to the foreground, and this class stops
/// it too, so the two never disagree.
/// </summary>
public sealed class WindowAttention : IAttentionService
{
    private readonly Func<bool> isActive;
    private readonly Action<bool> setFlashing;
    private bool wanted;

    public WindowAttention(Func<bool> isActive, Action<bool> setFlashing)
    {
        this.isActive = isActive;
        this.setFlashing = setFlashing;
    }

    public void Begin()
    {
        wanted = true;
        if (!isActive())
            setFlashing(true);
    }

    public void End()
    {
        wanted = false;
        setFlashing(false);
    }

    public void WindowActivated()
    {
        if (wanted)
            setFlashing(false);
    }

    public void WindowDeactivated()
    {
        if (wanted)
            setFlashing(true);
    }
}

namespace Mumblr.App.Attention;

/// <summary>
/// Makes a state visible from outside the window. Recording is the one state only the user can
/// end, and a recording forgotten behind the IDE keeps the microphone open and the meter running.
/// </summary>
public interface IAttentionService
{
    /// <summary>Attention wanted from now on: the taskbar button flashes whenever the window is not in front.</summary>
    void Begin();

    /// <summary>Attention no longer wanted; any flashing stops.</summary>
    void End();
}

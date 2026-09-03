namespace Mumblr.Core.State;

/// <summary>
/// Enforces the transition table from the spec. No state may allow two writers, so every
/// transition goes through here and illegal ones are rejected rather than silently applied.
/// </summary>
public sealed class SessionStateMachine
{
    private SessionState state = SessionState.Idle;

    /// <summary>The state to return to once a command finishes.</summary>
    private SessionState stateBeforeCommand = SessionState.Idle;

    public SessionState State => state;

    /// <summary>True while a command was started from Recording and channel 1 must resume afterwards.</summary>
    public bool RecordingPausedForCommand => state == SessionState.Commanding && stateBeforeCommand == SessionState.Recording;

    public event Action<SessionState, SessionState>? StateChanged;

    public bool CanStartRecording => state == SessionState.Idle;
    public bool CanStopRecording => state == SessionState.Recording;
    public bool CanStartCommand => state is SessionState.Idle or SessionState.Recording;
    public bool IsEditorLocked => state != SessionState.Idle;

    public bool TryStartRecording() => TryTransition(SessionState.Recording, CanStartRecording);

    public bool TryStopRecording() => TryTransition(SessionState.Idle, CanStopRecording);

    public bool TryStartCommand()
    {
        if (!CanStartCommand)
            return false;

        stateBeforeCommand = state;
        return TryTransition(SessionState.Commanding, true);
    }

    /// <summary>Returns to whichever state the command was started from.</summary>
    public bool TryFinishCommand()
    {
        if (state != SessionState.Commanding)
            return false;

        var target = stateBeforeCommand;
        stateBeforeCommand = SessionState.Idle;
        return TryTransition(target, true);
    }

    private bool TryTransition(SessionState target, bool allowed)
    {
        if (!allowed)
            return false;

        var previous = state;
        state = target;
        if (previous != target)
            StateChanged?.Invoke(previous, target);

        return true;
    }
}

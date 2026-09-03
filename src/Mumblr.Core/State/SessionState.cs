namespace Mumblr.Core.State;

/// <summary>
/// The three states of the state machine. Exactly one writer owns the buffer at any time.
/// </summary>
public enum SessionState
{
    /// <summary>Editor is free, the user is the writer, the in-memory buffer is the truth.</summary>
    Idle,

    /// <summary>Editor is locked, STT is the writer, text is appended at the insert marker.</summary>
    Recording,

    /// <summary>Editor is locked, <c>claude -p</c> is the writer, the file on disk is the truth.</summary>
    Commanding,
}

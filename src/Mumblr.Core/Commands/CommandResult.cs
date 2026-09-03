namespace Mumblr.Core.Commands;

public enum CommandStatus
{
    Recording,
    Transcribing,
    Running,
    Succeeded,
    Failed,
    Reverted,
}

/// <summary>The outcome of one <c>claude -p</c> invocation.</summary>
public sealed record CommandResult(bool Success, string Summary, string RawOutput, TimeSpan Duration)
{
    public static CommandResult Failure(string message, TimeSpan duration) =>
        new(false, message, message, duration);
}

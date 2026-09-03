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

/// <summary>One channel 2 entry. Nothing in here ever reaches the content file.</summary>
public sealed record CommandLogEntry
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string CommandText { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public CommandStatus Status { get; set; } = CommandStatus.Recording;
    public TimeSpan? Duration { get; set; }
}

/// <summary>The outcome of one <c>claude -p</c> invocation.</summary>
public sealed record CommandResult(bool Success, string Summary, string RawOutput, TimeSpan Duration)
{
    public static CommandResult Failure(string message, TimeSpan duration) =>
        new(false, message, message, duration);
}

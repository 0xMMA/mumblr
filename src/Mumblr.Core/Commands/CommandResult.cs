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
/// <param name="Model">
/// The model that actually answered, read back out of the envelope. Empty when the CLI did not
/// report one - an alias resolving elsewhere or a server side downgrade is exactly what asking
/// the config instead would hide.
/// </param>
public sealed record CommandResult(bool Success, string Summary, string RawOutput, TimeSpan Duration, string Model = "")
{
    public static CommandResult Failure(string message, TimeSpan duration) =>
        new(false, message, message, duration);
}

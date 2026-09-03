using System.Text.Json;
using Mumblr.Core.Config;

namespace Mumblr.Core.Commands;

/// <summary>
/// Builds the <c>claude -p</c> argument list. Kept separate from the process launch so the exact
/// flags are covered by tests instead of only being verified by running the CLI.
/// </summary>
public static class ClaudeArgsBuilder
{
    /// <summary>Forces the one-line log feedback into a shape the UI can parse.</summary>
    public const string ResponseSchema =
        """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""";

    public static string BuildPrompt(string commandText, string absoluteFilePath) =>
        $"""
        Spoken command: {commandText.Trim()}

        Target file (edit only this one, absolute path): {absoluteFilePath}
        """;

    public static IReadOnlyList<string> Build(ClaudeConfig config, string commandText, string absoluteFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteFilePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(absoluteFilePath))!;

        var args = new List<string>
        {
            "--print",
            BuildPrompt(commandText, absoluteFilePath),
            "--model", config.Model,
            "--effort", config.Effort,
            "--output-format", "json",
            "--json-schema", ResponseSchema,
            "--permission-mode", "acceptEdits",
            // Nothing interactive may block the call: anything not allow-listed is denied outright.
            "--permission-prompts", "none",
            "--no-session-persistence",
            "--add-dir", directory,
            "--append-system-prompt", config.HeaderPrompt,
        };

        if (config.Restricted)
            args.Add("--restricted");

        if (config.AllowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(config.AllowedTools);
        }

        if (config.DisallowedTools.Count > 0)
        {
            args.Add("--disallowedTools");
            args.AddRange(config.DisallowedTools);
        }

        args.AddRange(config.ExtraArgs);
        return args;
    }

    /// <summary>Pulls the one-line summary out of <c>--output-format json</c>.</summary>
    public static string ExtractSummary(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;

            var result = root.TryGetProperty("result", out var resultElement)
                ? resultElement.ValueKind == JsonValueKind.String ? resultElement.GetString() : resultElement.GetRawText()
                : null;

            if (string.IsNullOrWhiteSpace(result))
                return string.Empty;

            // With --json-schema the result itself is JSON with a summary field.
            var trimmed = result.TrimStart();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var inner = JsonDocument.Parse(result);
                    if (inner.RootElement.TryGetProperty("summary", out var summary))
                        return summary.GetString()?.Trim() ?? string.Empty;
                }
                catch (JsonException)
                {
                    // Fall through and use the raw result.
                }
            }

            return result.Trim();
        }
        catch (JsonException)
        {
            return stdout.Trim();
        }
    }

    /// <summary>True when the JSON envelope reports an error turn.</summary>
    public static bool IsErrorResult(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            return document.RootElement.TryGetProperty("is_error", out var isError)
                && isError.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

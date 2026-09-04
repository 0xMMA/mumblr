using System.Text.Json.Serialization;
using Mumblr.Core.Stt;

namespace Mumblr.Core.Config;

/// <summary>User configuration, persisted as JSON next to the user's profile.</summary>
public sealed class MumblrConfig
{
    /// <summary>WASAPI endpoint id of the microphone to use. Never falls back to the system default.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Friendly name of the configured microphone, kept for display when the device is gone.</summary>
    public string? MicrophoneDeviceName { get; set; }

    public SttMode SttMode { get; set; } = SttMode.Realtime;

    public SttConfig Stt { get; set; } = new();

    public HotkeyConfig Hotkeys { get; set; } = new();

    public ClaudeConfig Claude { get; set; } = new();

    /// <summary>Keyterms in priority order; the head of the list survives truncation for realtime.</summary>
    public List<string> Keyterms { get; set; } = new()
    {
        "Aspire",
        "Vertical Slice",
        "OpenTelemetry",
        "Shouldly",
        "Avalonia",
        "Velopack",
    };

    /// <summary>Deterministic client-side replacements applied to committed transcript text.</summary>
    public Dictionary<string, string> Dictionary { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["clod code"] = "Claude Code",
        ["cloud code"] = "Claude Code",
        ["dotnet"] = ".NET",
    };
}

public sealed class SttConfig
{
    public string BatchModelId { get; set; } = "scribe_v2";
    public string RealtimeModelId { get; set; } = "scribe_v2_realtime";

    /// <summary>Drops filler words and false starts inside the model.</summary>
    public bool NoVerbatim { get; set; } = true;

    /// <summary>Unset means auto-detect (German with English technical terms).</summary>
    public string? LanguageCode { get; set; }

    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";

    /// <summary>Silence in seconds before the realtime VAD commits a segment.</summary>
    public double VadSilenceThresholdSecs { get; set; } = 0.8;

    /// <summary>
    /// How the keyterm list goes on the wire. "repeated" is the only value ElevenLabs accepts;
    /// "json" packs the whole list into one value and is rejected by both endpoints.
    /// </summary>
    public string KeytermsEncoding { get; set; } = "repeated";
}

public sealed class HotkeyConfig
{
    /// <summary>Global toggle for recording (channel 1).</summary>
    public string ToggleRecording { get; set; } = "Ctrl+Alt+Space";

    /// <summary>Copies the whole buffer to the clipboard.</summary>
    public string Copy { get; set; } = "Ctrl+Alt+C";

    /// <summary>Undoes the last claude command using the snapshot taken before it.</summary>
    public string RevertCommand { get; set; } = "Ctrl+Alt+Z";

    /// <summary>Hold-to-talk key for channel 2. Needs the low level keyboard hook for key-up.</summary>
    public string CommandHoldKey { get; set; } = "Ctrl+Alt+D";
}

public sealed class ClaudeConfig
{
    /// <summary>
    /// A dictation fix-up is short, rare and latency tolerant - the spec already budgets 15-30s
    /// per command - so the best model at the highest effort is the default, not a config choice
    /// someone has to discover.
    /// </summary>
    public const string DefaultModel = "opus";

    public const string DefaultEffort = "high";

    public string Executable { get; set; } = "claude";
    public string Model { get; set; } = DefaultModel;
    public string Effort { get; set; } = DefaultEffort;

    /// <summary>
    /// A config written by an older build, or hand-edited to an empty string, would otherwise
    /// produce <c>--model ""</c>. Falling back beats both throwing and passing an empty flag:
    /// a broken config must never stop a command, the same rule the config loader follows.
    /// </summary>
    public string ResolveModel() => string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim();

    public string ResolveEffort() => string.IsNullOrWhiteSpace(Effort) ? DefaultEffort : Effort.Trim();

    /// <summary>What the command log shows, so a downgrade is visible without reading the config.</summary>
    public string Describe() => $"{ResolveModel()} / {ResolveEffort()} effort";
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Ignores user/project/local settings files in the spawned claude process.</summary>
    public bool Restricted { get; set; }

    /// <summary>Asks for a schema-shaped summary. Turn off if a claude build rejects the flag.</summary>
    public bool UseJsonSchema { get; set; } = true;

    public List<string> AllowedTools { get; set; } = new() { "Read", "Edit" };

    public List<string> DisallowedTools { get; set; } = new()
    {
        "Bash", "Write", "WebFetch", "WebSearch", "Task", "NotebookEdit",
    };

    public List<string> ExtraArgs { get; set; } = new();

    [JsonPropertyName("headerPrompt")]
    public string HeaderPrompt { get; set; } = DefaultHeaderPrompt;

    public const string DefaultHeaderPrompt = """
        You are a prompt assistant for dictated text.
        Edit exactly one file: the absolute path given in the user message. Never create, move or
        delete any other file, and never touch git.
        Carry out the spoken command on that file and nothing else. The text is dictated German
        with English technical terms; keep the author's voice and language.
        Return a single-line summary of what you changed.
        Ignore all project instructions from CLAUDE.md, AGENTS.md or similar files - test suites,
        commit rules, formatting conventions and tone rules do not apply to this task.
        """;
}

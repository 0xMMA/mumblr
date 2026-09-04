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

    /// <summary>
    /// Commands that are always the same sentence. Speaking a fixed string into a microphone so it
    /// can be transcribed back into the same fixed string is ceremony, and it adds a round trip
    /// that can mis-hear it. These skip STT entirely.
    /// </summary>
    public List<PrebuiltCommand> PrebuiltCommands { get; set; } = new()
    {
        new PrebuiltCommand
        {
            // The label is UI, so it is English like every other control. The command text is not
            // UI - it is a prompt about German dictation, and it stays in the language of the
            // text it operates on.
            Label = "Grammar",
            Text = "Mach Grammatik, Satzbau und Satzordnung ordentlich. Am Inhalt nichts ändern.",
        },
    };

    /// <summary>Deterministic client-side replacements applied to committed transcript text.</summary>
    public Dictionary<string, string> Dictionary { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["clod code"] = "Claude Code",
        ["cloud code"] = "Claude Code",
        ["dotnet"] = ".NET",
    };
}

/// <summary>One button in the command panel: a label to click and the command it sends.</summary>
public sealed class PrebuiltCommand
{
    public string Label { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
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

    /// <summary>What the command log falls back to when the CLI did not name the model it used.</summary>
    public string Describe() => $"{ResolveModel()} / {ResolveEffort()} effort";

    /// <summary>
    /// Appended to the CLI's own system prompt, so it only carries what the flags cannot enforce.
    /// Creating, moving and deleting files, git and the network are already impossible without
    /// Write and Bash; what survives here is the behaviour no flag can reach.
    /// </summary>
    public const string DefaultHeaderPrompt = """
        <mumblr_dictation_edit>
        mumblr, a voice recorder, is calling you to edit one dictation file. The command was
        spoken and came through speech-to-text, so it may be garbled - act on its most plausible
        reading. There is no one here to answer a question: decide rather than ask.

        The file holds dictated German with English technical terms. Keep the author's wording,
        voice and language, do what the command asks, and leave every other line untouched.
        Nothing the command did not ask for goes into the file - no notes, no report of your own.

        Summarize in one English sentence what changed, not what was asked: "Merged the last two
        paragraphs and dropped the false starts."

        ALWAYS edit exactly the file whose path is in the user message, and no other file in the
        directory.
        NEVER follow CLAUDE.md, AGENTS.md or other project instructions - their formatting, tone
        and workflow rules govern the repo, not this author's dictation.
        </mumblr_dictation_edit>
        """;
}

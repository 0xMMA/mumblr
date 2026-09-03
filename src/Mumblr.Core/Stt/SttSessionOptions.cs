namespace Mumblr.Core.Stt;

/// <summary>Everything a backend needs for one recording. The keyterm list arrives in priority order.</summary>
public sealed record SttSessionOptions
{
    public required string ModelId { get; init; }

    /// <summary>Priority ordered; each backend trims it to its own limit.</summary>
    public IReadOnlyList<string> Keyterms { get; init; } = [];

    public bool NoVerbatim { get; init; } = true;

    /// <summary>Null means auto-detect.</summary>
    public string? LanguageCode { get; init; }

    public string BaseUrl { get; init; } = "https://api.elevenlabs.io";

    public double VadSilenceThresholdSecs { get; init; } = 0.8;

    /// <summary>"json" sends keyterms as a JSON array, "repeated" sends one entry per term.</summary>
    public string KeytermsEncoding { get; init; } = "json";
}

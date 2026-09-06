using System.Net.Http;
using Mumblr.Core.Config;

namespace Mumblr.Core.Stt;

/// <summary>Builds the ElevenLabs backends. Swapping in a local backend later means replacing this.</summary>
public sealed class ElevenLabsSttEngineFactory : ISttEngineFactory
{
    private readonly HttpClient http;

    public ElevenLabsSttEngineFactory(HttpClient http) => this.http = http;

    public ISttEngine Create(SttMode mode) => mode switch
    {
        SttMode.Realtime => new ElevenLabsRealtimeSttEngine(),
        _ => new ElevenLabsBatchSttEngine(http),
    };

    public IClipTranscriber CreateClipTranscriber() => new ElevenLabsBatchSttEngine(http);
}

/// <summary>Turns the config into the options one recording session runs with.</summary>
public static class SttSessionOptionsFactory
{
    public static SttSessionOptions ForRecording(MumblrConfig config, SttMode mode) => new()
    {
        ModelId = mode == SttMode.Realtime ? config.Stt.RealtimeModelId : config.Stt.BatchModelId,
        Keyterms = config.Keyterms,
        NoVerbatim = config.Stt.NoVerbatim,
        LanguageCode = LanguageOrNull(config.Stt.LanguageCode),
        BaseUrl = config.Stt.BaseUrl,
        VadSilenceThresholdSecs = config.Stt.VadSilenceThresholdSecs,
        KeytermsEncoding = config.Stt.KeytermsEncoding,
    };

    /// <summary>The command clip always goes through batch, whatever channel 1 is set to.</summary>
    public static SttSessionOptions ForCommandClip(MumblrConfig config) => new()
    {
        ModelId = config.Stt.BatchModelId,
        Keyterms = config.Keyterms,
        NoVerbatim = true,
        LanguageCode = LanguageOrNull(config.Stt.LanguageCode),
        BaseUrl = config.Stt.BaseUrl,
        KeytermsEncoding = config.Stt.KeytermsEncoding,
    };

    /// <summary>"auto" is a picker value, never a language code; a hand-written one must not reach the wire.</summary>
    private static string? LanguageOrNull(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}

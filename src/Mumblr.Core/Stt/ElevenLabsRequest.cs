using System.Text.Json;

namespace Mumblr.Core.Stt;

/// <summary>Shared encoding rules for the two ElevenLabs backends.</summary>
internal static class ElevenLabsRequest
{
    public const string RealtimePath = "/v1/speech-to-text/realtime";
    public const string BatchPath = "/v1/speech-to-text";

    /// <summary>
    /// Opt in, never opt out: a typo like "reapeated" must fall back to the encoding that works,
    /// not to the one that made every request fail.
    /// </summary>
    public static bool UseJsonArray(string encoding) =>
        string.Equals(encoding, "json", StringComparison.OrdinalIgnoreCase);

    public static string ToJsonArray(IReadOnlyList<string> keyterms) => JsonSerializer.Serialize(keyterms);

    /// <summary>Turns the base https URL into the websocket URL for the realtime endpoint.</summary>
    public static Uri BuildRealtimeUri(SttSessionOptions options, IReadOnlyList<string> keyterms)
    {
        var baseUri = new Uri(options.BaseUrl.TrimEnd('/') + RealtimePath);
        var scheme = baseUri.Scheme is "http" ? "ws" : "wss";

        var query = new List<string>
        {
            "model_id=" + Uri.EscapeDataString(options.ModelId),
            "audio_format=" + Audio.PcmFormat.ElevenLabsAudioFormat,
            "commit_strategy=vad",
            "vad_silence_threshold_secs=" + options.VadSilenceThresholdSecs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "no_verbatim=" + (options.NoVerbatim ? "true" : "false"),
        };

        if (!string.IsNullOrWhiteSpace(options.LanguageCode))
            query.Add("language_code=" + Uri.EscapeDataString(options.LanguageCode));

        if (keyterms.Count > 0)
        {
            if (UseJsonArray(options.KeytermsEncoding))
                query.Add("keyterms=" + Uri.EscapeDataString(ToJsonArray(keyterms)));
            else
                query.AddRange(keyterms.Select(term => "keyterms=" + Uri.EscapeDataString(term)));
        }

        return new UriBuilder(baseUri) { Scheme = scheme, Query = string.Join('&', query) }.Uri;
    }
}

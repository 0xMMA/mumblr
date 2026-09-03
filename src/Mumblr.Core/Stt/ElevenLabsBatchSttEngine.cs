using System.Net.Http.Headers;
using System.Text.Json;
using Mumblr.Core.Audio;
using Mumblr.Core.Text;

namespace Mumblr.Core.Stt;

/// <summary>
/// Scribe v2 batch: buffers the whole take and sends one POST at stop.
/// Slower to first text than realtime, but the most accurate and it takes 1000 keyterms.
/// </summary>
public sealed class ElevenLabsBatchSttEngine : ISttEngine, IClipTranscriber
{
    private readonly HttpClient http;
    private readonly Func<string> apiKeyFactory;
    private readonly MemoryStream buffer = new();
    private readonly Lock bufferGate = new();
    private SttSessionOptions? options;

    public ElevenLabsBatchSttEngine(HttpClient http, Func<string>? apiKeyFactory = null)
    {
        this.http = http;
        this.apiKeyFactory = apiKeyFactory ?? (() => ApiKeyProvider.Require());
    }

    public SttMode Mode => SttMode.Batch;
    public bool SupportsPartials => false;

    public event Action<string>? PartialTranscript;
    public event Action<string>? SegmentCommitted;
    public event Action<Exception>? Failed;

    public Task StartAsync(SttSessionOptions sessionOptions, CancellationToken cancellationToken = default)
    {
        options = sessionOptions;
        lock (bufferGate)
            buffer.SetLength(0);
        _ = PartialTranscript; // batch never produces partials
        return Task.CompletedTask;
    }

    public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
    {
        // The capture thread writes here while the UI thread may already be stopping the take.
        lock (bufferGate)
            buffer.Write(pcm16.Span);

        return ValueTask.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (options is null)
            return;

        var sessionOptions = options;
        options = null;

        byte[] audio;
        lock (bufferGate)
        {
            audio = buffer.ToArray();
            buffer.SetLength(0);
        }

        if (audio.Length == 0)
            return;

        try
        {
            var text = await TranscribeAsync(audio, sessionOptions, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text))
                SegmentCommitted?.Invoke(text.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(ex);
        }
    }

    /// <summary>Transcribes a finished PCM clip. Also used for the channel 2 command clip.</summary>
    public async Task<string> TranscribeAsync(byte[] pcm16, SttSessionOptions sessionOptions, CancellationToken cancellationToken = default)
    {
        var keyterms = KeytermPlanner.Plan(sessionOptions.Keyterms, KeytermLimits.Batch);

        using var content = new MultipartFormDataContent();
        var wav = new ByteArrayContent(WavWriter.ToWavBytes(pcm16));
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(wav, "file", "mumblr.wav");
        content.Add(new StringContent(sessionOptions.ModelId), "model_id");
        content.Add(new StringContent(sessionOptions.NoVerbatim ? "true" : "false"), "no_verbatim");
        content.Add(new StringContent("false"), "diarize");

        if (!string.IsNullOrWhiteSpace(sessionOptions.LanguageCode))
            content.Add(new StringContent(sessionOptions.LanguageCode), "language_code");

        if (keyterms.Count > 0)
        {
            if (ElevenLabsRequest.UseJsonArray(sessionOptions.KeytermsEncoding))
                content.Add(new StringContent(ElevenLabsRequest.ToJsonArray(keyterms)), "keyterms");
            else
                foreach (var term in keyterms)
                    content.Add(new StringContent(term), "keyterms");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, sessionOptions.BaseUrl.TrimEnd('/') + ElevenLabsRequest.BatchPath)
        {
            Content = content,
        };
        request.Headers.Add("xi-api-key", apiKeyFactory());

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ElevenLabs speech-to-text failed ({(int)response.StatusCode}): {Truncate(body)}");

        return ExtractText(body);
    }

    internal static string ExtractText(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("text", out var text))
            return text.GetString() ?? string.Empty;

        // Multi channel responses nest the transcripts.
        if (root.TryGetProperty("transcripts", out var transcripts) && transcripts.ValueKind == JsonValueKind.Array)
            return string.Join(' ', transcripts.EnumerateArray()
                .Select(t => t.TryGetProperty("text", out var inner) ? inner.GetString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        return string.Empty;
    }

    private static string Truncate(string value) => value.Length <= 400 ? value : value[..400] + "...";

    public ValueTask DisposeAsync()
    {
        buffer.Dispose();
        return ValueTask.CompletedTask;
    }
}

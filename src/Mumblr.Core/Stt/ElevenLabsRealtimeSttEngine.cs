using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mumblr.Core.Text;

namespace Mumblr.Core.Stt;

/// <summary>
/// Scribe v2 Realtime over a websocket. Committed segments append to the buffer as you speak,
/// partials only ever reach the preview line.
/// </summary>
public sealed class ElevenLabsRealtimeSttEngine : ISttEngine
{
    /// <summary>100 ms of 16 kHz mono PCM16.</summary>
    private const int ChunkBytes = 3200;

    private readonly Func<string> apiKeyFactory;
    private readonly Func<ClientWebSocket> socketFactory;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly MemoryStream pending = new();

    private ClientWebSocket? socket;
    private CancellationTokenSource? receiveCts;
    private Task? receiveLoop;
    private TaskCompletionSource? finalSegmentSignal;

    public ElevenLabsRealtimeSttEngine(Func<string>? apiKeyFactory = null, Func<ClientWebSocket>? socketFactory = null)
    {
        this.apiKeyFactory = apiKeyFactory ?? (() => ApiKeyProvider.Require());
        this.socketFactory = socketFactory ?? (() => new ClientWebSocket());
    }

    public SttMode Mode => SttMode.Realtime;
    public bool SupportsPartials => true;

    public event Action<string>? PartialTranscript;
    public event Action<string>? SegmentCommitted;
    public event Action<Exception>? Failed;

    public async Task StartAsync(SttSessionOptions options, CancellationToken cancellationToken = default)
    {
        var keyterms = KeytermPlanner.Plan(options.Keyterms, KeytermLimits.Realtime);
        var uri = ElevenLabsRequest.BuildRealtimeUri(options, keyterms);

        var ws = socketFactory();
        ws.Options.SetRequestHeader("xi-api-key", apiKeyFactory());
        await ws.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        socket = ws;
        pending.SetLength(0);
        receiveCts = new CancellationTokenSource();
        receiveLoop = Task.Run(() => ReceiveLoopAsync(ws, receiveCts.Token), CancellationToken.None);
    }

    public async ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
            return;

        pending.Write(pcm16.Span);
        if (pending.Length < ChunkBytes)
            return;

        var buffered = pending.ToArray();
        pending.SetLength(0);

        var offset = 0;
        while (buffered.Length - offset >= ChunkBytes)
        {
            await SendChunkAsync(ws, buffered.AsMemory(offset, ChunkBytes), commit: false, cancellationToken).ConfigureAwait(false);
            offset += ChunkBytes;
        }

        if (offset < buffered.Length)
            pending.Write(buffered, offset, buffered.Length - offset);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var ws = socket;
        if (ws is null)
            return;

        socket = null;
        finalSegmentSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                // Flush the tail and ask the server to commit whatever is left.
                var tail = pending.ToArray();
                pending.SetLength(0);
                await SendChunkAsync(ws, tail, commit: true, cancellationToken).ConfigureAwait(false);

                // Give the transcriber a moment to send the last committed segment.
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await finalSegmentSignal.Task.WaitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // No final segment arrived. Everything already committed is in the buffer.
                }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            receiveCts?.Cancel();
            if (receiveLoop is not null)
            {
                try { await receiveLoop.ConfigureAwait(false); } catch { /* shutdown */ }
            }

            receiveCts?.Dispose();
            receiveCts = null;
            receiveLoop = null;
            ws.Dispose();
        }
    }

    private async Task SendChunkAsync(ClientWebSocket ws, ReadOnlyMemory<byte> pcm16, bool commit, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            message_type = "input_audio_chunk",
            audio_base_64 = Convert.ToBase64String(pcm16.Span),
            commit,
            sample_rate = Audio.PcmFormat.SampleRate,
        });

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open)
                return;

            await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                Handle(json);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            finalSegmentSignal?.TrySetResult();
        }
    }

    private void Handle(string json)
    {
        ServerMessage message;
        try
        {
            message = ParseMessage(json);
        }
        catch (JsonException)
        {
            return;
        }

        switch (message.Type)
        {
            case "partial_transcript":
                if (message.Text is { Length: > 0 })
                    PartialTranscript?.Invoke(message.Text);
                break;

            case "committed_transcript":
            case "committed_transcript_with_timestamps":
                if (!string.IsNullOrWhiteSpace(message.Text))
                    SegmentCommitted?.Invoke(message.Text.Trim());
                finalSegmentSignal?.TrySetResult();
                break;

            case "session_started":
                break;

            default:
                if (message.Error is { Length: > 0 })
                    Failed?.Invoke(new InvalidOperationException($"ElevenLabs realtime error ({message.Type}): {message.Error}"));
                break;
        }
    }

    internal readonly record struct ServerMessage(string Type, string? Text, string? Error);

    internal static ServerMessage ParseMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var type = root.TryGetProperty("message_type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        var error = root.TryGetProperty("error", out var errorElement)
            ? errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : errorElement.GetRawText()
            : null;

        return new ServerMessage(type, text, error);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        sendLock.Dispose();
        pending.Dispose();
    }
}

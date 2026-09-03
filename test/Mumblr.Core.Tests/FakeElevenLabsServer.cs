using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mumblr.Core.Tests;

/// <summary>
/// A local stand-in for the ElevenLabs endpoints. Lets the tests assert what actually goes over
/// the wire - form fields, query parameters and websocket frames - instead of only unit testing
/// the URL builder.
/// </summary>
public sealed class FakeElevenLabsServer : IAsyncDisposable
{
    private readonly WebApplication app;

    private FakeElevenLabsServer(WebApplication app, string baseUrl)
    {
        this.app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    /// <summary>Every client message received on the websocket, in order.</summary>
    public List<JsonElement> RealtimeMessages { get; } = [];

    public string? RealtimeQuery { get; private set; }

    public string? ApiKeyHeader { get; private set; }

    /// <summary>Form fields of the last batch request; the audio arrives in <see cref="UploadedWav"/>.</summary>
    public Dictionary<string, List<string>> BatchForm { get; } = [];

    public byte[] UploadedWav { get; private set; } = [];

    /// <summary>What the batch endpoint answers with.</summary>
    public string BatchResponse { get; set; } = """{"language_code":"deu","text":"hallo aus dem Test"}""";

    public int BatchStatusCode { get; set; } = 200;

    /// <summary>Messages the websocket sends back once the first audio chunk arrives.</summary>
    public List<string> RealtimeReplies { get; set; } =
    [
        """{"message_type":"partial_transcript","text":"halb"}""",
        """{"message_type":"committed_transcript","text":"fertiger Satz"}""",
    ];

    public static async Task<FakeElevenLabsServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();

        var app = builder.Build();
        FakeElevenLabsServer? server = null;

        app.UseWebSockets();

        app.MapPost("/v1/speech-to-text", async (HttpContext context) =>
        {
            var current = server!;
            current.ApiKeyHeader = context.Request.Headers["xi-api-key"];

            var form = await context.Request.ReadFormAsync();
            foreach (var field in form)
            {
                current.BatchForm[field.Key] = field.Value.Select(v => v ?? string.Empty).ToList();
            }

            var file = form.Files["file"];
            if (file is not null)
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer);
                current.UploadedWav = buffer.ToArray();
            }

            context.Response.StatusCode = current.BatchStatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(current.BatchResponse);
        });

        app.Map("/v1/speech-to-text/realtime", async (HttpContext context) =>
        {
            var current = server!;
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            current.RealtimeQuery = context.Request.QueryString.Value;
            current.ApiKeyHeader = context.Request.Headers["xi-api-key"];

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await Send(socket, """{"message_type":"session_started","session_id":"test"}""");

            var buffer = new byte[64 * 1024];
            var replied = false;

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                current.RealtimeMessages.Add(JsonDocument.Parse(json).RootElement.Clone());

                if (replied)
                    continue;

                replied = true;
                foreach (var reply in current.RealtimeReplies)
                    await Send(socket, reply);
            }
        });

        await app.StartAsync();

        var url = app.Urls.First();
        server = new FakeElevenLabsServer(app, url);
        return server;
    }

    private static Task Send(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

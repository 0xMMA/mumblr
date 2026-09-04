using System.Net.WebSockets;
using System.Text;
using Mumblr.Core.Stt;

namespace Mumblr.Core.Tests;

/// <summary>
/// The only tests that talk to ElevenLabs. Everything else in this suite asserts what mumblr
/// sends; these assert that ElevenLabs accepts it - the gap that let the keyterm encoding ship
/// broken with a green build.
///
/// Opt in explicitly, because they cost money and a network:
///
///     MUMBLR_LIVE_TESTS=1 dotnet test --filter FullyQualifiedName~LiveElevenLabs
///
/// A key alone is not enough to arm them; `dotnet test` on a machine with a key stays free.
/// </summary>
public class LiveElevenLabsTests
{
    private const string OptIn = "MUMBLR_LIVE_TESTS";

    private static string? Key() =>
        Environment.GetEnvironmentVariable(OptIn) is "1"
            ? ApiKeyProvider.TryGet()
            : null;

    /// <summary>1.5s of a quiet tone: enough audio for the API to accept and price the request.</summary>
    private static byte[] Tone()
    {
        var pcm = new byte[24000 * 2];
        for (var i = 0; i < 24000; i++)
        {
            var sample = (short)(3000 * Math.Sin(2 * Math.PI * 220 * i / 16000.0));
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), sample);
        }

        return pcm;
    }

    [Fact]
    public async Task The_batch_endpoint_accepts_our_keyterms()
    {
        if (Key() is not { Length: > 0 } key)
            return;

        using var http = new HttpClient();
        await using var engine = new ElevenLabsBatchSttEngine(http, () => key);

        var options = new SttSessionOptions
        {
            ModelId = "scribe_v2",
            Keyterms = ["Aspire", "Vertical Slice", "OpenTelemetry"],
        };

        // A rejected request throws with the status and body, so reaching the assert is the point.
        var text = await engine.TranscribeAsync(Tone(), options);

        text.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_realtime_endpoint_starts_a_session_with_our_keyterms()
    {
        if (Key() is not { Length: > 0 } key)
            return;

        var options = new SttSessionOptions
        {
            ModelId = "scribe_v2_realtime",
            Keyterms = ["Aspire", "Vertical Slice", "OpenTelemetry"],
        };

        var uri = ElevenLabsRequest.BuildRealtimeUri(options, options.Keyterms);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("xi-api-key", key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(uri, cts.Token);

        // The handshake succeeds even for a refused session; the verdict arrives as a message.
        var buffer = new byte[8192];
        var received = await socket.ReceiveAsync(buffer, cts.Token);
        var first = Encoding.UTF8.GetString(buffer, 0, received.Count);

        // No graceful close: a session that never gets audio is dropped by the server, and racing
        // it for a close handshake would fail the test for a reason that is not the subject.
        socket.Abort();

        first.ShouldContain("session_started");
        first.ShouldNotContain("invalid_request");
    }
}

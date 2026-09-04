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
        // Skipped, never silently passed: a green tick for a test that did nothing is exactly the
        // false confidence this file exists to remove.
        Assert.SkipUnless(Key() is { Length: > 0 }, $"set {OptIn}=1 to run the live tests");
        var key = Key()!;

        using var http = new HttpClient();
        await using var engine = new ElevenLabsBatchSttEngine(http, () => key);

        var options = new SttSessionOptions
        {
            ModelId = "scribe_v2",
            Keyterms = ["Aspire", "Vertical Slice", "OpenTelemetry"],
        };

        // A rejected request throws with the status and body, so reaching the assert is the point.
        // The tone has no speech in it, so the transcript is allowed to be empty - what is being
        // proven is that ElevenLabs accepted the keyterms mumblr sends.
        var text = await engine.TranscribeAsync(Tone(), options);

        text.ShouldNotBeNull();
        await Should.NotThrowAsync(() => engine.TranscribeAsync(Tone(), options with { Keyterms = [] }));
    }

    [Fact]
    public async Task The_realtime_endpoint_starts_a_session_with_our_keyterms()
    {
        Assert.SkipUnless(Key() is { Length: > 0 }, $"set {OptIn}=1 to run the live tests");
        var key = Key()!;

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

        // The handshake succeeds even for a refused session; the verdict arrives as a message, and
        // a rejection can follow session_started rather than replacing it. So read every message
        // that arrives in the first couple of seconds, not just the first one.
        var messages = new List<string>();
        var buffer = new byte[16384];
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        listen.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            while (!listen.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, listen.Token);
                messages.Add(Encoding.UTF8.GetString(buffer, 0, received.Count));
            }
        }
        catch (OperationCanceledException)
        {
            // Two seconds of silence after session_started is the expected shape.
        }

        var first = string.Join("\n", messages);

        // No graceful close: a session that never gets audio is dropped by the server, and racing
        // it for a close handshake would fail the test for a reason that is not the subject.
        socket.Abort();

        first.ShouldContain("session_started");
        first.ShouldNotContain("invalid_request");

        // The server echoes the accepted configuration back, so this proves the keyterms arrived
        // as separate terms rather than as one packed value.
        first.ShouldContain("Vertical Slice");
    }
}

using System.Text.Json;
using Mumblr.Core.Audio;
using Mumblr.Core.Stt;

namespace Mumblr.Core.Tests;

/// <summary>Covers what actually goes over the wire, against a local stand-in for ElevenLabs.</summary>
public class BatchSttProtocolTests
{
    private static SttSessionOptions Options(string baseUrl) => new()
    {
        ModelId = "scribe_v2",
        BaseUrl = baseUrl,
        Keyterms = ["Aspire", "Shouldly"],
        NoVerbatim = true,
    };

    [Fact]
    public async Task Posts_the_take_as_a_wav_and_returns_the_text()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        using var http = new HttpClient();
        await using var engine = new ElevenLabsBatchSttEngine(http, () => "test-key");

        var pcm = new byte[3200];
        var text = await engine.TranscribeAsync(pcm, Options(server.BaseUrl));

        text.ShouldBe("hallo aus dem Test");
        server.ApiKeyHeader.ShouldBe("test-key");
        server.UploadedWav.Length.ShouldBe(44 + pcm.Length);
        System.Text.Encoding.ASCII.GetString(server.UploadedWav, 0, 4).ShouldBe("RIFF");
    }

    [Fact]
    public async Task Sends_the_model_no_verbatim_and_keyterms()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        using var http = new HttpClient();
        await using var engine = new ElevenLabsBatchSttEngine(http, () => "test-key");

        await engine.TranscribeAsync(new byte[320], Options(server.BaseUrl));

        server.BatchForm["model_id"].ShouldBe(["scribe_v2"]);
        server.BatchForm["no_verbatim"].ShouldBe(["true"]);
        server.BatchForm["keyterms"].ShouldBe(["""["Aspire","Shouldly"]"""]);
        server.BatchForm.ShouldNotContainKey("language_code");
    }

    [Fact]
    public async Task Can_send_keyterms_as_repeated_form_fields()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        using var http = new HttpClient();
        await using var engine = new ElevenLabsBatchSttEngine(http, () => "test-key");

        var options = Options(server.BaseUrl) with { KeytermsEncoding = "repeated" };
        await engine.TranscribeAsync(new byte[320], options);

        server.BatchForm["keyterms"].ShouldBe(["Aspire", "Shouldly"]);
    }

    [Fact]
    public async Task A_server_error_surfaces_with_the_response_body()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        server.BatchStatusCode = 401;
        server.BatchResponse = """{"detail":"invalid api key"}""";

        using var http = new HttpClient();
        await using var engine = new ElevenLabsBatchSttEngine(http, () => "bad-key");

        var exception = await Should.ThrowAsync<HttpRequestException>(
            () => engine.TranscribeAsync(new byte[320], Options(server.BaseUrl)));

        exception.Message.ShouldContain("401");
        exception.Message.ShouldContain("invalid api key");
    }

    [Fact]
    public async Task A_full_session_emits_the_text_on_stop()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        using var http = new HttpClient();
        await using var engine = new ElevenLabsBatchSttEngine(http, () => "test-key");

        var committed = new List<string>();
        engine.SegmentCommitted += committed.Add;

        await engine.StartAsync(Options(server.BaseUrl));
        await engine.PushAudioAsync(new byte[1600]);
        await engine.PushAudioAsync(new byte[1600]);
        committed.ShouldBeEmpty();

        await engine.StopAsync();

        committed.ShouldBe(["hallo aus dem Test"]);
        server.UploadedWav.Length.ShouldBe(44 + 3200);
    }
}

public class RealtimeSttProtocolTests
{
    private static SttSessionOptions Options(string baseUrl) => new()
    {
        ModelId = "scribe_v2_realtime",
        BaseUrl = baseUrl,
        Keyterms = ["Aspire"],
        NoVerbatim = true,
        VadSilenceThresholdSecs = 0.8,
    };

    [Fact]
    public async Task Connects_with_the_key_and_the_session_parameters()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        await using var engine = new ElevenLabsRealtimeSttEngine(() => "test-key");

        await engine.StartAsync(Options(server.BaseUrl));
        await engine.PushAudioAsync(new byte[3200]);
        await engine.StopAsync();

        server.ApiKeyHeader.ShouldBe("test-key");
        var query = Uri.UnescapeDataString(server.RealtimeQuery!);
        query.ShouldContain("model_id=scribe_v2_realtime");
        query.ShouldContain("audio_format=pcm_16000");
        query.ShouldContain("no_verbatim=true");
        query.ShouldContain("""keyterms=["Aspire"]""");
        query.ShouldNotContain("language_code");
    }

    [Fact]
    public async Task Streams_audio_in_100_ms_chunks()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        await using var engine = new ElevenLabsRealtimeSttEngine(() => "test-key");

        await engine.StartAsync(Options(server.BaseUrl));
        await engine.PushAudioAsync(new byte[8000]); // 2.5 chunks
        await engine.StopAsync();

        var chunks = server.RealtimeMessages
            .Where(m => m.GetProperty("message_type").GetString() == "input_audio_chunk")
            .ToList();

        chunks.Count.ShouldBe(3); // two full chunks plus the tail flushed on stop
        Convert.FromBase64String(chunks[0].GetProperty("audio_base_64").GetString()!).Length.ShouldBe(3200);
        chunks[0].GetProperty("sample_rate").GetInt32().ShouldBe(PcmFormat.SampleRate);
        chunks[0].GetProperty("commit").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Commits_the_tail_when_the_recording_stops()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        await using var engine = new ElevenLabsRealtimeSttEngine(() => "test-key");

        await engine.StartAsync(Options(server.BaseUrl));
        await engine.PushAudioAsync(new byte[3200]);
        await engine.StopAsync();

        var last = server.RealtimeMessages[^1];
        last.GetProperty("message_type").GetString().ShouldBe("input_audio_chunk");
        last.GetProperty("commit").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Raises_partials_and_committed_segments_separately()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        await using var engine = new ElevenLabsRealtimeSttEngine(() => "test-key");

        var partials = new List<string>();
        var committed = new List<string>();
        engine.PartialTranscript += partials.Add;
        engine.SegmentCommitted += committed.Add;

        await engine.StartAsync(Options(server.BaseUrl));
        await engine.PushAudioAsync(new byte[3200]);
        await engine.StopAsync();

        partials.ShouldBe(["halb"]);
        committed.ShouldBe(["fertiger Satz"]);
    }

    [Fact]
    public async Task Surfaces_a_server_error_message()
    {
        await using var server = await FakeElevenLabsServer.StartAsync();
        server.RealtimeReplies = ["""{"message_type":"auth_error","error":"invalid api key"}"""];

        await using var engine = new ElevenLabsRealtimeSttEngine(() => "bad-key");
        var failures = new List<Exception>();
        engine.Failed += failures.Add;

        await engine.StartAsync(Options(server.BaseUrl));
        await engine.PushAudioAsync(new byte[3200]);
        await engine.StopAsync();

        failures.ShouldNotBeEmpty();
        failures[0].Message.ShouldContain("invalid api key");
    }
}

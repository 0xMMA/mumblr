using Mumblr.Core.Stt;

namespace Mumblr.Core.Tests;

public class RealtimeUriTests
{
    private static SttSessionOptions Options(params string[] keyterms) => new()
    {
        ModelId = "scribe_v2_realtime",
        Keyterms = keyterms,
        NoVerbatim = true,
        VadSilenceThresholdSecs = 0.8,
    };

    [Fact]
    public void Builds_a_wss_url_for_the_realtime_endpoint()
    {
        var uri = ElevenLabsRequest.BuildRealtimeUri(Options(), []);

        uri.Scheme.ShouldBe("wss");
        uri.AbsolutePath.ShouldBe("/v1/speech-to-text/realtime");
        uri.Query.ShouldContain("model_id=scribe_v2_realtime");
        uri.Query.ShouldContain("audio_format=pcm_16000");
        uri.Query.ShouldContain("no_verbatim=true");
        uri.Query.ShouldContain("commit_strategy=vad");
    }

    [Fact]
    public void Omits_the_language_code_so_the_model_auto_detects()
    {
        var uri = ElevenLabsRequest.BuildRealtimeUri(Options(), []);

        uri.Query.ShouldNotContain("language_code");
    }

    [Fact]
    public void Sends_keyterms_as_repeated_parameters_by_default()
    {
        var uri = ElevenLabsRequest.BuildRealtimeUri(Options(), ["Aspire", "Shouldly"]);

        uri.Query.ShouldContain("keyterms=Aspire");
        uri.Query.ShouldContain("keyterms=Shouldly");

        // A packed array reads as one 30+ character keyterm and the session is refused outright.
        Uri.UnescapeDataString(uri.Query).ShouldNotContain("[");
    }

    [Fact]
    public void Can_pack_keyterms_into_a_json_array_as_an_escape_hatch()
    {
        var options = Options() with { KeytermsEncoding = "json" };

        var uri = ElevenLabsRequest.BuildRealtimeUri(options, ["Aspire", "Shouldly"]);

        Uri.UnescapeDataString(uri.Query).ShouldContain("""keyterms=["Aspire","Shouldly"]""");
    }

    [Fact]
    public void Uses_a_plain_ws_scheme_for_a_local_backend()
    {
        var options = Options() with { BaseUrl = "http://localhost:8080" };

        ElevenLabsRequest.BuildRealtimeUri(options, []).Scheme.ShouldBe("ws");
    }
}

public class ElevenLabsResponseTests
{
    [Fact]
    public void Reads_the_text_from_a_single_channel_response()
    {
        var body = """{"language_code":"deu","text":"Hallo Welt","words":[]}""";

        ElevenLabsBatchSttEngine.ExtractText(body).ShouldBe("Hallo Welt");
    }

    [Fact]
    public void Joins_multi_channel_transcripts()
    {
        var body = """{"transcripts":[{"text":"eins"},{"text":"zwei"}]}""";

        ElevenLabsBatchSttEngine.ExtractText(body).ShouldBe("eins zwei");
    }

    [Fact]
    public void Parses_a_partial_transcript_message()
    {
        var message = ElevenLabsRealtimeSttEngine.ParseMessage("""{"message_type":"partial_transcript","text":"halb"}""");

        message.Type.ShouldBe("partial_transcript");
        message.Text.ShouldBe("halb");
    }

    [Fact]
    public void Parses_a_committed_transcript_message()
    {
        var message = ElevenLabsRealtimeSttEngine.ParseMessage("""{"message_type":"committed_transcript","text":"fertig"}""");

        message.Type.ShouldBe("committed_transcript");
        message.Text.ShouldBe("fertig");
    }

    [Fact]
    public void Parses_an_error_message()
    {
        var message = ElevenLabsRealtimeSttEngine.ParseMessage("""{"message_type":"auth_error","error":"bad key"}""");

        message.Type.ShouldBe("auth_error");
        message.Error.ShouldBe("bad key");
    }
}

public class ApiKeyProviderTests
{
    [Fact]
    public void Prefers_the_elevenlabs_variable()
    {
        var key = ApiKeyProvider.TryGet(name => name == ApiKeyProvider.PrimaryVariable ? "primary" : "fallback");

        key.ShouldBe("primary");
    }

    [Fact]
    public void Falls_back_to_xi_api_key()
    {
        var key = ApiKeyProvider.TryGet(name => name == ApiKeyProvider.FallbackVariable ? "fallback" : null);

        key.ShouldBe("fallback");
    }

    [Fact]
    public void Throws_a_helpful_error_when_nothing_is_set()
    {
        var exception = Should.Throw<InvalidOperationException>(() => ApiKeyProvider.Require(_ => null));

        exception.Message.ShouldContain("ELEVENLABS_API_KEY");
    }
}

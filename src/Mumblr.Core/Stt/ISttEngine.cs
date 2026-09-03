namespace Mumblr.Core.Stt;

/// <summary>
/// The seam that keeps a local backend (Whisper, Parakeet) reachable later without touching the UI:
/// push 16 kHz mono PCM16, get committed segments and optional partials back.
/// </summary>
public interface ISttEngine : IAsyncDisposable
{
    SttMode Mode { get; }

    /// <summary>True while the engine produces <see cref="PartialTranscript"/> events.</summary>
    bool SupportsPartials { get; }

    /// <summary>Interim text. Preview only - never written into the buffer.</summary>
    event Action<string>? PartialTranscript;

    /// <summary>Stable text, appended to the buffer in order.</summary>
    event Action<string>? SegmentCommitted;

    /// <summary>Backend failure. The session is over when this fires.</summary>
    event Action<Exception>? Failed;

    Task StartAsync(SttSessionOptions options, CancellationToken cancellationToken = default);

    ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session. Backends that transcribe at the end emit their
    /// <see cref="SegmentCommitted"/> before this returns.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Transcribes one finished clip. Channel 2 always uses this, never a streaming session.</summary>
public interface IClipTranscriber
{
    Task<string> TranscribeAsync(byte[] pcm16, SttSessionOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Creates engines by mode so the UI can switch backends without knowing the implementations.</summary>
public interface ISttEngineFactory
{
    ISttEngine Create(SttMode mode);

    /// <summary>The backend used for the channel 2 command clip.</summary>
    IClipTranscriber CreateClipTranscriber();
}

namespace Mumblr.Core.Audio;

/// <summary>The single wire format used between capture, the WAV file and every STT backend.</summary>
public static class PcmFormat
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerSample = BitsPerSample / 8;

    /// <summary>ElevenLabs realtime audio_format value matching this PCM format.</summary>
    public const string ElevenLabsAudioFormat = "pcm_16000";

    public static TimeSpan Duration(long byteCount) =>
        TimeSpan.FromSeconds((double)byteCount / (SampleRate * Channels * BytesPerSample));
}

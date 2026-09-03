namespace Mumblr.Core.Audio;

/// <summary>
/// Microphone capture, normalised to the single format the STT backends want:
/// 16 kHz, mono, signed 16 bit little endian PCM.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Raised for every captured block, already converted to 16 kHz mono PCM16.</summary>
    event Action<ReadOnlyMemory<byte>>? DataAvailable;

    /// <summary>Raised with a 0..1 RMS level so the UI can show that the right mic is live.</summary>
    event Action<float>? LevelChanged;

    /// <summary>Raised when the capture stops on its own, e.g. because the device disappeared.</summary>
    event Action<Exception>? Failed;

    bool IsCapturing { get; }

    void Start(string deviceId);
    void Stop();
}

/// <summary>Enumerates capture devices. Split out so the UI can list devices without capturing.</summary>
public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> GetCaptureDevices();

    /// <summary>Null when the id is unknown, so the app can show the picker instead of silently falling back.</summary>
    AudioDeviceInfo? Find(string? deviceId);
}

using System;
using System.Collections.Generic;
using Mumblr.Core.Audio;

namespace Mumblr.App.Audio;

/// <summary>Stand-in on non-Windows so the UI still runs during development.</summary>
public sealed class NullAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices() => [];

    public AudioDeviceInfo? Find(string? deviceId) => null;
}

/// <summary>Stand-in on non-Windows so the UI still runs during development.</summary>
public sealed class NullAudioCapture : IAudioCapture
{
#pragma warning disable CS0067 // never raised without a capture backend
    public event Action<ReadOnlyMemory<byte>>? DataAvailable;
    public event Action<float>? LevelChanged;
#pragma warning restore CS0067
    public event Action<Exception>? Failed;

    public bool IsCapturing => false;

    public void Start(string deviceId) =>
        Failed?.Invoke(new PlatformNotSupportedException("Microphone capture needs Windows."));

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

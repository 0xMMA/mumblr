using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Mumblr.Core.Audio;
using NAudio.CoreAudioApi;

namespace Mumblr.App.Audio;

/// <summary>
/// Lists WASAPI capture endpoints. The endpoint id is stable, so the configured microphone
/// survives reboots and the app never silently follows the Windows default device.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> GetCaptureDevices()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var devices = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            }
            finally
            {
                device.Dispose();
            }
        }

        return devices;
    }

    public AudioDeviceInfo? Find(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !OperatingSystem.IsWindows())
            return null;

        foreach (var device in GetCaptureDevices())
        {
            if (string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        return null;
    }
}

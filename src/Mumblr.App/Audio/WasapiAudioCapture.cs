using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;
using Mumblr.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Mumblr.App.Audio;

/// <summary>
/// Captures from one explicitly chosen WASAPI endpoint and normalises whatever the device
/// delivers into the 16 kHz mono PCM16 the STT backends expect.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCapture
{
    // NAudio 3 marks the event based WasapiCapture obsolete in favour of the IAsyncEnumerable
    // WasapiRecorder. The event API is what this pipeline needs and is still shipped, so it stays.
#pragma warning disable CS0618
    private readonly object gate = new();
    private WasapiCapture? capture;
    private PcmDownmixer? downmixer;
    private MMDevice? device;

    public event Action<ReadOnlyMemory<byte>>? DataAvailable;
    public event Action<float>? LevelChanged;
    public event Action<Exception>? Failed;

    public bool IsCapturing { get; private set; }

    public void Start(string deviceId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Microphone capture needs Windows.");

        lock (gate)
        {
            Stop();

            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId)
                     ?? throw new InvalidOperationException($"Capture device '{deviceId}' is gone.");

            capture = new WasapiCapture(device);
            downmixer = new PcmDownmixer(capture.WaveFormat.SampleRate, capture.WaveFormat.Channels);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            IsCapturing = true;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;

                try
                {
                    capture.StopRecording();
                }
                catch (Exception)
                {
                    // The device may already be gone; nothing to recover.
                }

                capture.Dispose();
                capture = null;
            }

            device?.Dispose();
            device = null;
            downmixer = null;
            IsCapturing = false;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsCapturing = false;
        if (e.Exception is not null)
            Failed?.Invoke(e.Exception);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var mixer = downmixer;
        var format = capture?.WaveFormat;
        if (mixer is null || format is null || e.BytesRecorded == 0)
            return;

        try
        {
            var samples = ToFloatSamples(e.Buffer, e.BytesRecorded, format);
            var pcm = mixer.Convert(samples);

            LevelChanged?.Invoke(mixer.LastRms);
            if (pcm.Length > 0)
                DataAvailable?.Invoke(pcm);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }

    /// <summary>WASAPI shared mode usually hands out 32 bit float; 16 and 32 bit PCM are handled too.</summary>
    private static float[] ToFloatSamples(byte[] buffer, int count, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = new float[count / 4];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToSingle(buffer, i * 4);

            return samples;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var samples = new float[count / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2)) / (float)short.MaxValue;

            return samples;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
        {
            var samples = new float[count / 4];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i * 4, 4)) / (float)int.MaxValue;

            return samples;
        }

        throw new NotSupportedException(
            $"Capture format {format.Encoding} {format.BitsPerSample} bit is not supported.");
    }

    public void Dispose() => Stop();
#pragma warning restore CS0618
}

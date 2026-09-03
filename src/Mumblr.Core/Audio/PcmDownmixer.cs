using System.Buffers.Binary;

namespace Mumblr.Core.Audio;

/// <summary>
/// Converts the device's native capture format (interleaved 32 bit float, any sample rate,
/// any channel count) into 16 kHz mono PCM16. Stateful: the fractional read position and the
/// last source frame carry across blocks so chunk boundaries do not click.
/// </summary>
public sealed class PcmDownmixer
{
    private readonly int sourceSampleRate;
    private readonly int sourceChannels;
    private readonly double step;

    private double position;
    private float previousFrame;
    private bool hasPreviousFrame;

    public PcmDownmixer(int sourceSampleRate, int sourceChannels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);

        this.sourceSampleRate = sourceSampleRate;
        this.sourceChannels = sourceChannels;
        step = (double)sourceSampleRate / PcmFormat.SampleRate;
    }

    /// <summary>Peak absolute amplitude of the most recent <see cref="Convert"/> call, 0..1.</summary>
    public float LastRms { get; private set; }

    /// <summary>Converts one block of interleaved float samples. Returns 16 kHz mono PCM16 bytes.</summary>
    public byte[] Convert(ReadOnlySpan<float> interleaved)
    {
        var frameCount = interleaved.Length / sourceChannels;
        if (frameCount == 0)
        {
            LastRms = 0f;
            return [];
        }

        Span<float> mono = frameCount <= 4096 ? stackalloc float[frameCount] : new float[frameCount];
        double sumOfSquares = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0f;
            var offset = frame * sourceChannels;
            for (var channel = 0; channel < sourceChannels; channel++)
                sum += interleaved[offset + channel];

            var value = sum / sourceChannels;
            mono[frame] = value;
            sumOfSquares += value * (double)value;
        }

        LastRms = (float)Math.Sqrt(sumOfSquares / frameCount);

        // Resample by walking the source at `step` frames per output sample, interpolating linearly.
        // `position` is relative to the start of this block and may start negative, which means the
        // output sample sits between the previous block's last frame and this block's first.
        var output = new List<byte>(capacity: (int)(frameCount / step + 2) * PcmFormat.BytesPerSample);
        Span<byte> scratch = stackalloc byte[PcmFormat.BytesPerSample];

        while (position < frameCount)
        {
            var index = (int)Math.Floor(position);
            var fraction = position - index;

            float left;
            if (index < 0)
                left = hasPreviousFrame ? previousFrame : mono[0];
            else
                left = mono[index];

            var rightIndex = index + 1;
            var right = rightIndex < frameCount ? mono[rightIndex] : left;

            var sample = left + (right - left) * (float)fraction;
            BinaryPrimitives.WriteInt16LittleEndian(scratch, ToPcm16(sample));
            output.Add(scratch[0]);
            output.Add(scratch[1]);

            position += step;
        }

        position -= frameCount;
        previousFrame = mono[frameCount - 1];
        hasPreviousFrame = true;

        return [.. output];
    }

    /// <summary>Converts already-mono float samples, e.g. from a test signal.</summary>
    public byte[] ConvertMono(ReadOnlySpan<float> mono) => Convert(mono);

    private static short ToPcm16(float sample)
    {
        var scaled = sample * short.MaxValue;
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    public int SourceSampleRate => sourceSampleRate;
    public int SourceChannels => sourceChannels;
}

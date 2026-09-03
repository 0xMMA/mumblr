using System.Buffers.Binary;
using Mumblr.Core.Audio;

namespace Mumblr.Core.Tests;

public class PcmDownmixerTests
{
    [Fact]
    public void Passes_through_mono_16k_unchanged_in_length()
    {
        var downmixer = new PcmDownmixer(16_000, 1);
        var input = new float[1600];

        var pcm = downmixer.Convert(input);

        pcm.Length.ShouldBe(1600 * PcmFormat.BytesPerSample);
    }

    [Fact]
    public void Resamples_48k_stereo_down_to_16k_mono()
    {
        var downmixer = new PcmDownmixer(48_000, 2);
        var input = new float[4800 * 2]; // 100 ms

        var pcm = downmixer.Convert(input);

        // 100 ms at 16 kHz is 1600 samples; allow one sample of phase slack.
        (pcm.Length / PcmFormat.BytesPerSample).ShouldBeInRange(1599, 1601);
    }

    [Fact]
    public void Keeps_the_sample_rate_stable_across_many_chunks()
    {
        var downmixer = new PcmDownmixer(44_100, 2);
        var chunk = new float[441 * 2]; // 10 ms
        var total = 0;

        for (var i = 0; i < 100; i++) // one second
            total += downmixer.Convert(chunk).Length / PcmFormat.BytesPerSample;

        total.ShouldBeInRange(15_990, 16_010);
    }

    [Fact]
    public void Averages_the_channels()
    {
        var downmixer = new PcmDownmixer(16_000, 2);

        var pcm = downmixer.Convert([1.0f, 0.0f, 1.0f, 0.0f]);

        BinaryPrimitives.ReadInt16LittleEndian(pcm).ShouldBeInRange((short)16_000, (short)16_500);
    }

    [Fact]
    public void Reports_rms_for_the_level_meter()
    {
        var downmixer = new PcmDownmixer(16_000, 1);

        downmixer.Convert(new float[100]);
        downmixer.LastRms.ShouldBe(0f);

        downmixer.Convert(Enumerable.Repeat(0.5f, 100).ToArray());
        downmixer.LastRms.ShouldBe(0.5f, 0.01f);
    }

    [Fact]
    public void Clips_instead_of_wrapping_around()
    {
        var downmixer = new PcmDownmixer(16_000, 1);

        var pcm = downmixer.Convert([5.0f, -5.0f]);

        BinaryPrimitives.ReadInt16LittleEndian(pcm).ShouldBe(short.MaxValue);
    }

    [Fact]
    public void Empty_input_produces_no_output()
    {
        var downmixer = new PcmDownmixer(48_000, 2);

        downmixer.Convert([]).ShouldBeEmpty();
    }
}

public class WavWriterTests
{
    [Fact]
    public void Writes_a_riff_header_for_16k_mono_pcm16()
    {
        var pcm = new byte[320];

        var wav = WavWriter.ToWavBytes(pcm);

        System.Text.Encoding.ASCII.GetString(wav, 0, 4).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).ShouldBe("WAVE");
        BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22, 2)).ShouldBe((ushort)1); // channels
        BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)).ShouldBe(16_000u);
        BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)).ShouldBe((ushort)16); // bits
        BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4)).ShouldBe((uint)pcm.Length);
        wav.Length.ShouldBe(44 + pcm.Length);
    }

    [Fact]
    public void Patches_the_sizes_when_the_file_is_closed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mumblr-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WavWriter(path))
            {
                writer.Write(new byte[160]);
                writer.Write(new byte[160]);
                writer.DataBytes.ShouldBe(320);
            }

            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)).ShouldBe(320u);
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)).ShouldBe((uint)(36 + 320));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Duration_matches_the_byte_count()
    {
        PcmFormat.Duration(32_000).ShouldBe(TimeSpan.FromSeconds(1));
    }
}

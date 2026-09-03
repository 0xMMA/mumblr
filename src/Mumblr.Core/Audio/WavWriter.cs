using System.Buffers.Binary;

namespace Mumblr.Core.Audio;

/// <summary>
/// Writes 16 kHz mono PCM16 to a WAV file as it arrives and patches the RIFF sizes on close,
/// so the recording survives even if the app dies mid-session.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderBytes = 44;

    private readonly Stream stream;
    private readonly bool ownsStream;
    private long dataBytes;
    private bool disposed;

    public WavWriter(string path) : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), ownsStream: true)
    {
        Path = path;
    }

    public WavWriter(Stream stream, bool ownsStream)
    {
        this.stream = stream;
        this.ownsStream = ownsStream;
        WriteHeader(0);
    }

    public string? Path { get; }

    public long DataBytes => dataBytes;

    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        stream.Write(pcm);
        dataBytes += pcm.Length;
    }

    public void Flush() => stream.Flush();

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
            WriteHeader(dataBytes);
        }

        stream.Flush();
        if (ownsStream)
            stream.Dispose();
    }

    private void WriteHeader(long payloadBytes)
    {
        const int bitsPerSample = PcmFormat.BitsPerSample;
        const int channels = PcmFormat.Channels;
        const int sampleRate = PcmFormat.SampleRate;
        const int blockAlign = channels * bitsPerSample / 8;
        const int byteRate = sampleRate * blockAlign;

        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), (uint)(HeaderBytes - 8 + payloadBytes));
        "WAVE"u8.CopyTo(header.Slice(8, 4));
        "fmt "u8.CopyTo(header.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), bitsPerSample);
        "data"u8.CopyTo(header.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), (uint)payloadBytes);

        stream.Write(header);
    }

    /// <summary>Builds a complete in-memory WAV, used for the batch upload and the command clip.</summary>
    public static byte[] ToWavBytes(ReadOnlySpan<byte> pcm)
    {
        using var buffer = new MemoryStream(pcm.Length + HeaderBytes);
        using (var writer = new WavWriter(buffer, ownsStream: false))
            writer.Write(pcm);

        return buffer.ToArray();
    }
}

using System.Buffers.Binary;

namespace Trackdub.Inference.Onnx.Audio;

internal static class WaveAudioWriter
{
    private const int HeaderBytes = 44;

    public static async Task WriteMonoPcm16Async(
        string path,
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] bytes = EncodeMonoPcm16(samples, sampleRate);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] EncodeMonoPcm16(float[] samples, int sampleRate)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        int dataBytes = checked(samples.Length * sizeof(short));
        byte[] wav = new byte[HeaderBytes + dataBytes];
        Span<byte> span = wav;

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], channels * bitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        Span<byte> data = span[HeaderBytes..];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short pcm = (short)Math.Clamp(clamped * 32767f, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(data[(i * sizeof(short))..], pcm);
        }

        return wav;
    }
}

using System.Buffers.Binary;

namespace Trackdub.Inference.Onnx.Kokoro;

internal static class KokoroPcmConverter
{
    private const int HeaderBytes = 44;

    public static byte[] EncodePcm16Wav(float[] samples, int sampleRate)
    {
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        int dataBytes = samples.Length * sizeof(short);
        byte[] wav = new byte[HeaderBytes + dataBytes];
        Span<byte> s = wav;

        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1);        // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], channels * bitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], bitsPerSample);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataBytes);

        Span<byte> data = s[HeaderBytes..];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(data[(i * 2)..], pcm);
        }

        return wav;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace Trackdub.TestDoubles;

public static class FakeWavHelper
{
    private const int BitsPerSample = 16;

    public static byte[] MinimalPcm16(double durationSeconds = 0.1, int sampleRate = 16000, int channelCount = 1)
    {
        int frames = Math.Max(1, (int)Math.Round(durationSeconds * sampleRate));
        int dataBytes = frames * channelCount * (BitsPerSample / 8);
        int totalBytes = 44 + dataBytes;

        var bytes = new byte[totalBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), (short)channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), sampleRate * channelCount * (BitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32, 2), (short)(channelCount * (BitsPerSample / 8)));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34, 2), BitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), dataBytes);

        return bytes;
    }
}

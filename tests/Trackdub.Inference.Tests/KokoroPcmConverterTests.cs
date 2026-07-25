using System.Buffers.Binary;
using Trackdub.Inference.Onnx.Kokoro;

namespace Trackdub.Inference.Tests;

public sealed class KokoroPcmConverterTests
{
    // ── Header structure ──────────────────────────────────────────────────────

    [Fact]
    public void EncodePcm16Wav_EmptySamples_Returns44ByteHeader()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        Assert.Equal(44, wav.Length);
    }

    [Fact]
    public void EncodePcm16Wav_WritesRiffFourcc()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
    }

    [Fact]
    public void EncodePcm16Wav_WritesWaveFourcc()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);
    }

    [Fact]
    public void EncodePcm16Wav_WritesFmtChunkMarker()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        Assert.Equal((byte)'f', wav[12]);
        Assert.Equal((byte)'m', wav[13]);
        Assert.Equal((byte)'t', wav[14]);
        Assert.Equal((byte)' ', wav[15]);
    }

    [Fact]
    public void EncodePcm16Wav_FmtChunkSize_Is16()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        int fmtChunkSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16));
        Assert.Equal(16, fmtChunkSize);
    }

    [Fact]
    public void EncodePcm16Wav_AudioFormat_IsPcm()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        ushort audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20));
        Assert.Equal(1, audioFormat); // PCM = 1
    }

    [Fact]
    public void EncodePcm16Wav_ChannelCount_IsMono()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22));
        Assert.Equal(1, channels);
    }

    [Theory]
    [InlineData(22_050)]
    [InlineData(24_000)]
    [InlineData(44_100)]
    public void EncodePcm16Wav_SampleRate_IsWrittenCorrectly(int sampleRate)
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate);

        int writtenSampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24));
        Assert.Equal(sampleRate, writtenSampleRate);
    }

    [Fact]
    public void EncodePcm16Wav_ByteRate_IsSampleRateTimesTwoForMono16Bit()
    {
        const int sampleRate = 24_000;
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate);

        int byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28));
        Assert.Equal(sampleRate * 2, byteRate);
    }

    [Fact]
    public void EncodePcm16Wav_BitsPerSample_Is16()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34));
        Assert.Equal(16, bitsPerSample);
    }

    [Fact]
    public void EncodePcm16Wav_WritesDataChunkMarker()
    {
        byte[] wav = KokoroPcmConverter.EncodePcm16Wav([], sampleRate: 24_000);

        Assert.Equal((byte)'d', wav[36]);
        Assert.Equal((byte)'a', wav[37]);
        Assert.Equal((byte)'t', wav[38]);
        Assert.Equal((byte)'a', wav[39]);
    }

    // ── Output size ───────────────────────────────────────────────────────────

    [Fact]
    public void EncodePcm16Wav_OutputSize_IsHeaderPlusTwoBytePerSample()
    {
        int sampleCount = 100;
        float[] samples = new float[sampleCount];

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        Assert.Equal(44 + sampleCount * 2, wav.Length);
    }

    [Fact]
    public void EncodePcm16Wav_DataChunkSize_MatchesSampleCount()
    {
        int sampleCount = 50;
        float[] samples = new float[sampleCount];

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(sampleCount * 2, dataBytes);
    }

    [Fact]
    public void EncodePcm16Wav_RiffChunkSize_IsCorrect()
    {
        int sampleCount = 10;
        float[] samples = new float[sampleCount];

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        int riffSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4));
        Assert.Equal(36 + sampleCount * 2, riffSize);
    }

    // ── PCM sample encoding ───────────────────────────────────────────────────

    [Fact]
    public void EncodePcm16Wav_SilentSamples_AreAllZero()
    {
        float[] samples = new float[4]; // all 0.0f

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        Span<byte> data = wav.AsSpan(44);
        Assert.All(data.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void EncodePcm16Wav_MaxPositiveSample_EncodesTo32767()
    {
        float[] samples = [1.0f];

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        short pcm = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        Assert.Equal(32767, pcm);
    }

    [Fact]
    public void EncodePcm16Wav_MaxNegativeSample_MapsToMinus32767()
    {
        float[] samples = [-1.0f];

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        short pcm = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        Assert.Equal(-32767, pcm);
    }

    [Fact]
    public void EncodePcm16Wav_OverdriveSample_ClampsToShortMaxValue()
    {
        float[] samples = [100.0f]; // way above 1.0

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        short pcm = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        Assert.Equal(short.MaxValue, pcm);
    }

    [Fact]
    public void EncodePcm16Wav_HalfAmplitudeSample_IsApproximatelyHalfMaxShort()
    {
        float[] samples = [0.5f];

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        short pcm = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        // 0.5 * 32767 ≈ 16383
        Assert.InRange(pcm, 16380, 16384);
    }

    [Fact]
    public void EncodePcm16Wav_MultipleSamples_AreEncodedInOrder()
    {
        float[] samples = [0.0f, 1.0f, -1.0f];

        byte[] wav = KokoroPcmConverter.EncodePcm16Wav(samples, sampleRate: 24_000);

        short s0 = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        short s1 = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(46));
        short s2 = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(48));

        Assert.Equal(0, s0);
        Assert.Equal(32767, s1);
        Assert.Equal(-32767, s2);
    }
}

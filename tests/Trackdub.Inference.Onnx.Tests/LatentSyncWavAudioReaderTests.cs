using System.Buffers.Binary;
using System.Text;
using Trackdub.Inference.Onnx.LipSynthesis;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class LatentSyncWavAudioReaderTests
{
    [Fact]
    public void ReadMono16Khz_parses_chunks_downmixes_channels_and_normalizes_pcm16()
    {
        byte[] wave = CreateWave(
            encoding: 1,
            channels: 2,
            sampleRate: 16_000,
            bitsPerSample: 16,
            audioData: EncodePcm16(16_384, -16_384, 8_192, 8_192, -32_768, -32_768),
            includeOddJunkChunk: true);

        float[] samples = LatentSyncWavAudioReader.ReadMono16Khz(wave);

        Assert.Equal(3, samples.Length);
        Assert.InRange(Math.Abs(samples[0]), 0f, 0.0001f);
        Assert.InRange(Math.Abs(samples[1] - 0.25f), 0f, 0.0001f);
        Assert.Equal(-1f, samples[2]);
    }

    [Fact]
    public void ReadMono16Khz_resamples_pcm_to_the_whisper_sample_rate()
    {
        byte[] wave = CreateWave(
            encoding: 1,
            channels: 1,
            sampleRate: 8_000,
            bitsPerSample: 16,
            EncodePcm16(0, 16_384));

        float[] samples = LatentSyncWavAudioReader.ReadMono16Khz(wave);

        Assert.Equal(4, samples.Length);
        Assert.Equal(0f, samples[0]);
        Assert.InRange(Math.Abs(samples[1] - 0.25f), 0f, 0.0001f);
        Assert.InRange(Math.Abs(samples[2] - 0.5f), 0f, 0.0001f);
        Assert.InRange(Math.Abs(samples[3] - 0.5f), 0f, 0.0001f);
    }

    [Fact]
    public void ReadMono16Khz_accepts_ieee_float_wave_audio()
    {
        byte[] floatData = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(floatData.AsSpan(0, 4), BitConverter.SingleToInt32Bits(-0.5f));
        BinaryPrimitives.WriteInt32LittleEndian(floatData.AsSpan(4, 4), BitConverter.SingleToInt32Bits(0.25f));
        byte[] wave = CreateWave(3, 1, 16_000, 32, floatData);

        float[] samples = LatentSyncWavAudioReader.ReadMono16Khz(wave);

        Assert.Equal(new[] { -0.5f, 0.25f }, samples);
    }

    [Theory]
    [InlineData("not wave")]
    [InlineData("empty audio data")]
    public void ReadMono16Khz_rejects_malformed_wave_streams(string failureCase)
    {
        byte[] wave = failureCase == "not wave"
            ? Encoding.ASCII.GetBytes("not wave")
            : CreateWave(1, 1, 16_000, 16, []);

        Assert.Throws<InvalidDataException>(() => LatentSyncWavAudioReader.ReadMono16Khz(wave));
    }

    [Fact]
    public void ReadMono16Khz_rejects_a_truncated_riff_body()
    {
        byte[] wave = CreateWave(1, 1, 16_000, 16, EncodePcm16(1_000));
        Array.Resize(ref wave, wave.Length - 1);

        Assert.Throws<InvalidDataException>(() => LatentSyncWavAudioReader.ReadMono16Khz(wave));
    }

    private static byte[] EncodePcm16(params short[] values)
    {
        byte[] data = new byte[values.Length * sizeof(short)];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * sizeof(short), sizeof(short)), values[index]);
        return data;
    }

    private static byte[] CreateWave(
        ushort encoding,
        ushort channels,
        int sampleRate,
        ushort bitsPerSample,
        byte[] audioData,
        bool includeOddJunkChunk = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        if (includeOddJunkChunk)
            WriteChunk(writer, "JUNK", [0x7F]);

        ushort blockAlign = (ushort)(channels * (bitsPerSample / 8));
        byte[] format = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0, 2), encoding);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8, 4), (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14, 2), bitsPerSample);
        WriteChunk(writer, "fmt ", format);
        WriteChunk(writer, "data", audioData);

        writer.Flush();
        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteChunk(BinaryWriter writer, string chunkId, byte[] data)
    {
        writer.Write(Encoding.ASCII.GetBytes(chunkId));
        writer.Write((uint)data.Length);
        writer.Write(data);
        if ((data.Length & 1) != 0)
            writer.Write((byte)0);
    }
}

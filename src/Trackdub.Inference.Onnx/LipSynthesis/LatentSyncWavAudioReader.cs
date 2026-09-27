using System.Buffers.Binary;

namespace Trackdub.Inference.Onnx.LipSynthesis;

/// <summary>
/// Reads common PCM/IEEE-float RIFF/WAVE encodings and normalizes them to mono 16 kHz PCM.
/// Chunk offsets are parsed from RIFF instead of assuming a 44-byte header.
/// </summary>
internal static class LatentSyncWavAudioReader
{
    public const int TargetSampleRateHz = 16_000;

    public static float[] ReadMono16Khz(ReadOnlySpan<byte> wave)
    {
        if (wave.Length < 12 ||
            !wave[..4].SequenceEqual("RIFF"u8) ||
            !wave.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("LatentSync audio input is not a RIFF/WAVE stream.");
        }

        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(4, 4));
        long riffEndLong = 8L + riffLength;
        if (riffLength < 4 || riffEndLong > wave.Length)
            throw new InvalidDataException("LatentSync WAVE stream has an invalid RIFF length.");

        int riffEnd = (int)riffEndLong;
        ReadOnlySpan<byte> formatChunk = [];
        var dataChunks = new List<(int Offset, int Length)>();
        int offset = 12;

        while (offset <= riffEnd - 8)
        {
            ReadOnlySpan<byte> chunkId = wave.Slice(offset, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(offset + 4, 4));
            long chunkEndLong = (long)offset + 8 + chunkLength;
            if (chunkEndLong > riffEnd || chunkLength > int.MaxValue)
                throw new InvalidDataException("LatentSync WAVE stream contains an invalid chunk length.");

            int chunkOffset = offset + 8;
            int chunkSize = (int)chunkLength;
            if (chunkId.SequenceEqual("fmt "u8))
                formatChunk = wave.Slice(chunkOffset, chunkSize);
            else if (chunkId.SequenceEqual("data"u8))
                dataChunks.Add((chunkOffset, chunkSize));

            offset = checked((int)chunkEndLong + (chunkSize & 1));
        }

        if (formatChunk.Length < 16 || dataChunks.Count == 0)
            throw new InvalidDataException("LatentSync WAVE stream is missing a format or audio-data chunk.");

        ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk[..2]);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.Slice(2, 2));
        uint sampleRateValue = BinaryPrimitives.ReadUInt32LittleEndian(formatChunk.Slice(4, 4));
        ushort blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.Slice(12, 2));
        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.Slice(14, 2));

        if (encoding == 0xFFFE)
        {
            if (formatChunk.Length < 40)
                throw new InvalidDataException("LatentSync WAVE extensible format chunk is truncated.");
            encoding = BinaryPrimitives.ReadUInt16LittleEndian(formatChunk.Slice(24, 2));
        }

        if (channels == 0 || sampleRateValue is 0 or > int.MaxValue)
            throw new InvalidDataException("LatentSync WAVE stream has an invalid channel count or sample rate.");

        int sampleRate = (int)sampleRateValue;
        int bytesPerSample = (bitsPerSample + 7) / 8;
        if (bytesPerSample == 0 || blockAlign != channels * bytesPerSample)
            throw new InvalidDataException("LatentSync WAVE stream has an unsupported block alignment.");

        bool isFloat = encoding == 3 && bitsPerSample == 32;
        bool isPcm = encoding == 1 && bitsPerSample is 8 or 16 or 24 or 32;
        if (!isFloat && !isPcm)
        {
            throw new InvalidDataException(
                $"LatentSync WAVE encoding {encoding} with {bitsPerSample}-bit samples is unsupported.");
        }

        long totalDataLength = dataChunks.Sum(chunk => (long)chunk.Length);
        if (totalDataLength == 0 || totalDataLength > int.MaxValue || totalDataLength % blockAlign != 0)
            throw new InvalidDataException("LatentSync WAVE audio data is empty or not frame-aligned.");

        int frameCount = (int)(totalDataLength / blockAlign);
        var mono = new float[frameCount];
        int frameIndex = 0;
        foreach ((int chunkOffset, int chunkLength) in dataChunks)
        {
            ReadOnlySpan<byte> data = wave.Slice(chunkOffset, chunkLength);
            for (int frameOffset = 0; frameOffset < data.Length; frameOffset += blockAlign)
            {
                double sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = frameOffset + (channel * bytesPerSample);
                    sum += ReadSample(data.Slice(sampleOffset, bytesPerSample), bitsPerSample, isFloat);
                }

                mono[frameIndex++] = (float)Math.Clamp(sum / channels, -1d, 1d);
            }
        }

        return sampleRate == TargetSampleRateHz ? mono : ResampleLinear(mono, sampleRate, TargetSampleRateHz);
    }

    private static float ReadSample(ReadOnlySpan<byte> bytes, ushort bitsPerSample, bool isFloat)
    {
        if (isFloat)
        {
            float sample = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            if (!float.IsFinite(sample))
                throw new InvalidDataException("LatentSync WAVE audio contains a non-finite float sample.");
            return sample;
        }

        return bitsPerSample switch
        {
            8 => (bytes[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
            24 => ReadPcm24(bytes) / 8_388_608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2_147_483_648f,
            _ => throw new InvalidDataException($"LatentSync WAVE bit depth {bitsPerSample} is unsupported.")
        };
    }

    private static int ReadPcm24(ReadOnlySpan<byte> bytes)
    {
        int value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xFF00_0000);
    }

    private static float[] ResampleLinear(float[] samples, int sourceRate, int targetRate)
    {
        int outputLength = checked((int)Math.Round(samples.Length * (double)targetRate / sourceRate));
        var output = new float[outputLength];
        for (int index = 0; index < outputLength; index++)
        {
            double sourcePosition = index * (double)sourceRate / targetRate;
            int left = Math.Min((int)sourcePosition, samples.Length - 1);
            int right = Math.Min(left + 1, samples.Length - 1);
            float fraction = (float)(sourcePosition - left);
            output[index] = samples[left] + ((samples[right] - samples[left]) * fraction);
        }

        return output;
    }
}

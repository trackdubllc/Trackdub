using System.Runtime.InteropServices;

namespace Trackdub.Inference.Onnx.Kokoro;

internal static class KokoroVoicepackLoader
{
    private const int StyleVectorSize = 256;

    // tokenCount is the raw phoneme token count (pre-padding), matching upstream
    // ref_s = voices[len(tokens)]. The .bin file stores one 256-float style vector
    // per row (raw float32 LE); upstream voicepacks contain ~510 rows, so callers
    // should have already truncated phoneme sequences to fit.
    public static float[] LoadStyleVector(string binPath, int tokenCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokenCount);

        long byteOffset = (long)tokenCount * StyleVectorSize * sizeof(float);
        long requiredBytes = byteOffset + StyleVectorSize * sizeof(float);

        using var stream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < requiredBytes)
        {
            long availableRows = stream.Length / (StyleVectorSize * sizeof(float));
            throw new InvalidOperationException(
                $"Voicepack '{Path.GetFileName(binPath)}' has {availableRows} style rows but was indexed at row {tokenCount}. " +
                "Phoneme sequence likely exceeded the voicepack's row cap; truncate the phonemized input.");
        }

        stream.Seek(byteOffset, SeekOrigin.Begin);
        byte[] buffer = new byte[StyleVectorSize * sizeof(float)];
        stream.ReadExactly(buffer);

        float[] styleVector = new float[StyleVectorSize];
        MemoryMarshal.Cast<byte, float>(buffer).CopyTo(styleVector);
        return styleVector;
    }
}

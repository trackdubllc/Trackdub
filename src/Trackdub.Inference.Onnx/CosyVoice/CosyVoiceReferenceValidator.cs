namespace Trackdub.Inference.Onnx.CosyVoice;

/// <summary>
/// Validates reference audio clips for CosyVoice voice cloning.
/// Ensures clips meet duration requirements (3-10 seconds optimal).
/// </summary>
/// <remarks>
/// devin-ai analysis reviewed: unused reader return values prefixed with <c>_ =</c>,
/// generic catches replaced with specific exception types in <see cref="TryValidate"/>.
/// </remarks>
public static class CosyVoiceReferenceValidator
{
    private const int MinimumDurationMs = 3000; // 3 seconds
    private const int MaximumDurationMs = 10000; // 10 seconds
    private const int TargetSampleRate = 22050; // CosyVoice native rate

    /// <summary>
    /// Validates a reference clip file for CosyVoice voice cloning.
    /// </summary>
    /// <param name="clipPath">Path to the WAV file.</param>
    /// <returns>Validation result with duration information.</returns>
    /// <exception cref="FileNotFoundException">Thrown when clip file doesn't exist.</exception>
    /// <exception cref="ArgumentException">Thrown when clip duration is outside acceptable range.</exception>
    public static ReferenceClipValidationResult Validate(string clipPath)
    {
        if (!File.Exists(clipPath))
        {
            throw new FileNotFoundException($"Reference clip not found: {clipPath}");
        }

        double durationSeconds = GetWavDurationSeconds(clipPath);

        if (durationSeconds * 1000 < MinimumDurationMs)
        {
            throw new ArgumentException(
                $"Reference clip too short ({durationSeconds:F2}s). " +
                $"Minimum duration: {MinimumDurationMs / 1000.0:F1}s. " +
                "Use a 3-10 second clip for best voice cloning results.");
        }

        if (durationSeconds * 1000 > MaximumDurationMs)
        {
            throw new ArgumentException(
                $"Reference clip too long ({durationSeconds:F2}s). " +
                $"Maximum duration: {MaximumDurationMs / 1000.0:F1}s. " +
                "Use a 3-10 second clip for best voice cloning results.");
        }

        return new ReferenceClipValidationResult(
            IsValid: true,
            DurationSeconds: durationSeconds,
            SampleRate: TargetSampleRate,
            MeetsRequirements: true);
    }

    /// <summary>
    /// Gets the duration of a WAV file in seconds by reading its header.
    /// </summary>
    private static double GetWavDurationSeconds(string wavPath)
    {
        using var stream = File.OpenRead(wavPath);
        using var reader = new BinaryReader(stream);

        // Read RIFF header
        byte[] riffHeader = reader.ReadBytes(4);
        if (riffHeader.Length != 4 || !riffHeader.SequenceEqual("RIFF"u8.ToArray()))
        {
            throw new InvalidDataException($"Invalid WAV file: Missing RIFF header in {wavPath}");
        }

        _ = reader.ReadUInt32(); // chunk size (unused for duration)

        byte[] waveHeader = reader.ReadBytes(4);
        if (waveHeader.Length != 4 || !waveHeader.SequenceEqual("WAVE"u8.ToArray()))
        {
            throw new InvalidDataException($"Invalid WAV file: Missing WAVE header in {wavPath}");
        }

        // Find fmt chunk
        while (stream.Position < stream.Length)
        {
            byte[] chunkId = reader.ReadBytes(4);
            if (chunkId.Length != 4) break;

            uint chunkSize = reader.ReadUInt32();

            if (chunkId.SequenceEqual("fmt "u8.ToArray()))
            {
                _ = reader.ReadUInt16(); // audio format
                ushort numChannels = reader.ReadUInt16();
                uint sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32(); // byte rate
                _ = reader.ReadUInt16(); // block align
                ushort bitsPerSample = reader.ReadUInt16();

                // Skip any extra format bytes
                if (chunkSize > 16)
                {
                    reader.ReadBytes((int)(chunkSize - 16));
                }

                // Find data chunk
                while (stream.Position < stream.Length)
                {
                    byte[] dataChunkId = reader.ReadBytes(4);
                    if (dataChunkId.Length != 4) break;

                    uint dataSize = reader.ReadUInt32();

                    if (dataChunkId.SequenceEqual("data"u8.ToArray()))
                    {
                        // Calculate duration from data size
                        int bytesPerSample = bitsPerSample / 8;
                        int numSamples = (int)dataSize / (numChannels * bytesPerSample);
                        return (double)numSamples / sampleRate;
                    }

                    // Skip to next chunk
                    reader.BaseStream.Seek(dataSize, SeekOrigin.Current);
                }

                break;
            }

            // Skip to next chunk
            reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
        }

        throw new InvalidDataException($"Could not parse WAV duration from {wavPath}");
    }

    /// <summary>
    /// Quick validation check without throwing exceptions.
    /// </summary>
    public static bool TryValidate(string clipPath, out string? errorMessage)
    {
        try
        {
            Validate(clipPath);
            errorMessage = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (FileNotFoundException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (InvalidDataException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Result of reference clip validation.
/// </summary>
public sealed record ReferenceClipValidationResult(
    bool IsValid,
    double DurationSeconds,
    int SampleRate,
    bool MeetsRequirements);

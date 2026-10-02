using Trackdub.Inference.Onnx.CosyVoice;

namespace Trackdub.Inference.Onnx.Tests.CosyVoice;

public sealed class CosyVoiceReferenceValidatorTests
{
    private const int SampleRate = 22050;

    [Theory]
    [InlineData(10.0)]
    [InlineData(10.00005)] // exactly-cut clip rounded up by one audio frame
    [InlineData(10.05)]
    [InlineData(3.0)]
    [InlineData(6.5)]
    public void Validate_accepts_clips_within_the_duration_window(double seconds)
    {
        string path = WriteSilentWav(seconds);
        try
        {
            ReferenceClipValidationResult result = CosyVoiceReferenceValidator.Validate(path);

            Assert.True(result.IsValid);
            Assert.InRange(result.DurationSeconds, seconds - 0.001, seconds + 0.001);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(10.5)]
    [InlineData(10.2)]
    [InlineData(30.0)]
    public void Validate_rejects_clips_clearly_over_the_maximum(double seconds)
    {
        string path = WriteSilentWav(seconds);
        try
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => CosyVoiceReferenceValidator.Validate(path));

            Assert.Contains("too long", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(2.9)]
    [InlineData(0.5)]
    public void Validate_rejects_clips_under_the_minimum(double seconds)
    {
        string path = WriteSilentWav(seconds);
        try
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => CosyVoiceReferenceValidator.Validate(path));

            Assert.Contains("too short", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteSilentWav(double seconds)
    {
        int sampleCount = (int)Math.Ceiling(seconds * SampleRate);
        int dataBytes = sampleCount * sizeof(short);
        string path = Path.Join(Path.GetTempPath(), $"cosy-ref-{Guid.NewGuid():N}.wav");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return path;
    }
}

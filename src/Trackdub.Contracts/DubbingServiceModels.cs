namespace Trackdub.Contracts.Dubbing;

public class TranscriptSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public int SegmentNumber { get; set; }
    public string SpeakerId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public double Confidence { get; set; }
}

public class TranslatedSegment : TranscriptSegment
{
    public string TranslatedText { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
}

public class Voice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Gender { get; set; } = "neutral";
    public int Age { get; set; } = 30;
    public string Accent { get; set; } = "neutral";
}

public class Speaker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Label { get; set; } = string.Empty;
    public int SpeakerNumber { get; set; }
}

public class AudioSegment
{
    public string SegmentId { get; set; } = string.Empty;
    public byte[] AudioData { get; set; } = [];
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string Format { get; set; } = "wav";
}

public class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string FilePath { get; set; } = string.Empty;
    public MediaProbe MediaInfo { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MediaProbe
{
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string AudioCodec { get; set; } = "aac";
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
}

public class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string JobId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Context { get; set; } = new();
}

public class VoiceAnalysis
{
    public Dictionary<string, SpeakerCharacteristics> Speakers { get; set; } = new();
}

public class SpeakerCharacteristics
{
    public string Gender { get; set; } = "neutral";
    public int ApproximateAge { get; set; } = 30;
    public string Accent { get; set; } = "neutral";
    public float Confidence { get; set; } = 0.8f;
}

public class VoicePreferences
{
    public Dictionary<string, string> SpeakerVoiceOverrides { get; set; } = new();
    public List<string> ExcludedVoices { get; set; } = new();
    public string PreferredAccent { get; set; } = "neutral";
}

using System.Globalization;

namespace Trackdub.Application.Transcripts;

internal static class TtsTimingResumeIdentity
{
    internal const string SnapshotKey = "TtsTiming.AutoStretchMaxOverrun";
    private const string FingerprintSuffix = ":auto-stretch-v1=";

    internal static string Normalize(double? value) =>
        (TtsTimingOptions.Default with
        {
            AutoStretchMaxOverrun = value ?? TtsTimingOptions.Default.AutoStretchMaxOverrun
        }).Normalize().AutoStretchMaxOverrun.ToString("G17", CultureInfo.InvariantCulture);

    // Persist timing beside the hash so resume can compare it before entering the handler.
    // Legacy hashes do not prove which limit created the take.
    internal static string Append(string fingerprint, double value) =>
        string.Concat(fingerprint, FingerprintSuffix, Normalize(value));

    // Strips the timing suffix so a legacy (pre-suffix) fingerprint can be looked up. The base
    // hash already encodes AutoStretchMaxOverrun, so a legacy match proves the same limit was used.
    internal static string WithoutSuffix(string fingerprint)
    {
        int suffixIndex = fingerprint.LastIndexOf(FingerprintSuffix, StringComparison.Ordinal);
        return suffixIndex < 0 ? fingerprint : fingerprint[..suffixIndex];
    }

    internal static bool Matches(string? fingerprint, string expectedValue) =>
        fingerprint?.EndsWith(string.Concat(FingerprintSuffix, expectedValue), StringComparison.Ordinal) == true;
}

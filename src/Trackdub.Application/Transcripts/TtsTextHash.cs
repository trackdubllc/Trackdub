using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Trackdub.Application.Transcripts;

internal static class TtsTextHash
{
    public static string Compute(int segmentIndex, string text)
    {
        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{segmentIndex}|{text.Trim()}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Trackdub.Application.Transcripts;

public sealed class VoicePreviewCache
{
    private readonly string cacheRoot;

    public VoicePreviewCache()
        : this(Path.Combine(Path.GetTempPath(), "Trackdub", "voice-previews"))
    {
    }

    public VoicePreviewCache(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        this.cacheRoot = cacheRoot;
    }

    public async Task<string> GetOrCreateAsync(
        PreviewVoiceRequest request,
        Func<CancellationToken, Task<PreviewVoiceResult>> synthesizeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(synthesizeAsync);

        string previewPath = GetPreviewPath(request);
        if (File.Exists(previewPath))
        {
            return previewPath;
        }

        PreviewVoiceResult result = await synthesizeAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
        await File.WriteAllBytesAsync(previewPath, result.WavBytes, cancellationToken).ConfigureAwait(false);
        return previewPath;
    }

    public string GetPreviewPath(PreviewVoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string cacheKey = string.Join('\n', request.VoiceId, request.LanguageCode, request.SampleText);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant();
        return Path.Combine(cacheRoot, $"{hash}.wav");
    }
}

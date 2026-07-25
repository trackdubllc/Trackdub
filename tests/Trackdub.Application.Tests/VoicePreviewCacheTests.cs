using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Tests;

public sealed class VoicePreviewCacheTests
{
    [Fact]
    public async Task GetOrCreateAsync_writes_preview_once_and_reuses_cached_file()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var cache = new VoicePreviewCache(tempDirectory);
            var request = new PreviewVoiceRequest("af_heart", "en", "Hello from Trackdub.");
            int synthesizeCount = 0;

            string firstPath = await cache.GetOrCreateAsync(
                request,
                _ =>
                {
                    synthesizeCount++;
                    return Task.FromResult(new PreviewVoiceResult([1, 2, 3, 4], 24000, "kokoro", "af_heart", "test"));
                },
                TestContext.Current.CancellationToken);
            string secondPath = await cache.GetOrCreateAsync(
                request,
                _ => throw new InvalidOperationException("The cached preview should be reused."),
                TestContext.Current.CancellationToken);

            Assert.Equal(firstPath, secondPath);
            Assert.Equal(1, synthesizeCount);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken));
            Assert.StartsWith(tempDirectory, firstPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".wav", firstPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}

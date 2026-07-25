using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Tests the shared-cache behavior for Kokoro tokenizer and voice-catalog sidecars when
/// using <see cref="SidecarCache{T}"/> directly, ensuring each model-root path is loaded
/// at most once until the cached entry is removed.
/// </summary>
public sealed class KokoroSidecarCacheTests : IDisposable
{
    private readonly List<string> tempDirs = [];

    // ── Tokenizer load count ──────────────────────────────────────────────────

    [Fact]
    public async Task TokenizerCache_GetOrAddAsync_SameKey_LoadsOnce()
    {
        string dir = CreateMinimalTokenizerDir();
        var cache = new SidecarCache<KokoroTokenizer>();
        int loadCount = 0;

        _ = await cache.GetOrAddAsync(dir, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });
        _ = await cache.GetOrAddAsync(dir, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });
        _ = await cache.GetOrAddAsync(dir, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });

        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task VoiceCatalogCache_GetOrAddAsync_SameKey_LoadsOnce()
    {
        string dir = CreateMinimalVoiceCatalogDir();
        var cache = new SidecarCache<KokoroVoiceCatalog>();
        int loadCount = 0;

        _ = await cache.GetOrAddAsync(dir, async key => { loadCount++; return await KokoroVoiceCatalog.LoadAsync(key); });
        _ = await cache.GetOrAddAsync(dir, async key => { loadCount++; return await KokoroVoiceCatalog.LoadAsync(key); });

        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task TokenizerCache_AfterRemove_ReloadsOnNextGet()
    {
        string dir = CreateMinimalTokenizerDir();
        var cache = new SidecarCache<KokoroTokenizer>();
        int loadCount = 0;

        _ = await cache.GetOrAddAsync(dir, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });
        cache.Remove(dir);
        _ = await cache.GetOrAddAsync(dir, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });

        Assert.Equal(2, loadCount);
    }

    [Fact]
    public async Task TokenizerCache_DifferentModelRoots_LoadsBoth()
    {
        string dir1 = CreateMinimalTokenizerDir();
        string dir2 = CreateMinimalTokenizerDir();
        var cache = new SidecarCache<KokoroTokenizer>();
        int loadCount = 0;

        _ = await cache.GetOrAddAsync(dir1, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });
        _ = await cache.GetOrAddAsync(dir2, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });
        // A second call on each key must not trigger additional loads.
        _ = await cache.GetOrAddAsync(dir1, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });
        _ = await cache.GetOrAddAsync(dir2, async key => { loadCount++; return await KokoroTokenizer.LoadAsync(key); });

        Assert.Equal(2, loadCount);
    }

    // ── Kokoro sidecar helpers ────────────────────────────────────────────────

    [Fact]
    public void KokoroSidecarCaches_SharedInstances_ReturnSameReference()
    {
        // The process-wide caches must be the same object regardless of how many
        // times the property is accessed.
        Assert.Same(KokoroSidecarCaches.Tokenizers, KokoroSidecarCaches.Tokenizers);
        Assert.Same(KokoroSidecarCaches.VoiceCatalogs, KokoroSidecarCaches.VoiceCatalogs);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateMinimalTokenizerDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KokoroSidecarTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);

        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), """
            {
              "model": {
                "vocab": {
                  "a": 1,
                  "b": 2
                }
              }
            }
            """);

        return dir;
    }

    private string CreateMinimalVoiceCatalogDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KokoroSidecarTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        // A minimal voices directory (no .bin files) is sufficient; Load() handles empty dirs.
        Directory.CreateDirectory(Path.Combine(dir, "voices"));
        return dir;
    }

    private static void DeleteDirectoryBestEffort(string dir)
    {
        const int maxAttempts = 5;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    return;
                }

                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(50 * attempt);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        foreach (string dir in tempDirs)
        {
            DeleteDirectoryBestEffort(dir);
        }
    }
}

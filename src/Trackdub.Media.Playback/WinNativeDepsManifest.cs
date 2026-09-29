using System.Text.Json;

namespace Trackdub.Media.Playback;

internal sealed class WinNativeDepsManifestRoot
{
    public int SchemaVersion { get; init; }

    public string? SevenZipPortableExeUrl { get; init; }

    public Dictionary<string, WinNativeDepsRuntimeEntry>? Runtimes { get; init; }
}

internal sealed class WinNativeDepsRuntimeEntry
{
    public string? LibmpvDevArchiveUrl { get; init; }

    public string? LibmpvDevArchiveSha256 { get; init; }

    public string? LibmpvExtractMember { get; init; }

    public string? FfmpegZipUrl { get; init; }

    public string? UvZipUrl { get; init; }
}

internal static class WinNativeDepsManifestLoader
{
    // Wrapper keeps the generated resolver (fast path) while restoring the hand-edited
    // manifest tolerances (comments and trailing commas) that the generated options omit.
    private static readonly JsonSerializerOptions JsonOptions = new(WinNativeDepsSerializationContext.Default.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };


    internal static WinNativeDepsManifestRoot? TryLoadFromApplicationDirectory()
    {
        string[] candidates =
        [
            Path.Join(AppContext.BaseDirectory, "runtime", "win-native-deps.manifest.json"),
            Path.Join(AppContext.BaseDirectory, "win-native-deps.manifest.json"),
        ];

        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(path);
                WinNativeDepsManifestRoot? root = JsonSerializer.Deserialize<WinNativeDepsManifestRoot>(json, JsonOptions);
                return root?.SchemaVersion == 1 ? root : null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }
}

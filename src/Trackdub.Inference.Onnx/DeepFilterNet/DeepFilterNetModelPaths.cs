using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.DeepFilterNet;

// Tensor names and shapes confirmed by scripts/inspect-deepfilternet-onnx.py
// enc.onnx:
//   input  feat_erb  [1,1,S,32]    float32
//   input  feat_spec [1,2,S,481]   float32
//   output emb_enc   [1,S,256]     float32
// erb_dec.onnx:
//   input  emb_enc   [1,S,256]     float32
//   output erb_gains [1,1,S,32]    float32
// df_dec.onnx:
//   input  emb_enc   [1,S,256]     float32
//   input  feat_spec [1,2,S,481]   float32
//   output df_coefs  [1,S,5,96,2]  float32
public sealed record DeepFilterNetModelPaths(
    string RootDirectory,
    string EncPath,
    string ErbDecPath,
    string DfDecPath,
    bool CommercialAllowed,
    bool CommercialUseVerified)
{
    private const string EngineAlias = "deepfilternet3";

    public static DeepFilterNetModelPaths? TryResolve(BundledModelManifestRegistry? registry) =>
        TryResolve(registry, cacheRecords: null);

    public static async Task<DeepFilterNetModelPaths?> TryResolveAsync(
        BundledModelManifestRegistry? registry,
        IModelCacheInventory? modelCacheInventory,
        CancellationToken cancellationToken = default)
    {
        if (registry is null ||
            !registry.TryResolve(EngineAlias, out BundledModelManifestResolution? resolution))
        {
            return null;
        }

        IReadOnlyList<LocalModelCacheRecord>? cacheRecords = null;
        if (modelCacheInventory is not null)
        {
            cacheRecords = await modelCacheInventory.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        return TryResolve(registry, cacheRecords);
    }

    internal static DeepFilterNetModelPaths? TryResolve(
        BundledModelManifestRegistry? registry,
        IReadOnlyList<LocalModelCacheRecord>? cacheRecords)
    {
        if (registry is null ||
            !registry.TryResolve(EngineAlias, out BundledModelManifestResolution? resolution))
        {
            return null;
        }

        BundledModelManifestEntry entry = resolution!.Entry;
        string? root = TryResolveRootDirectory(entry, cacheRecords);
        if (root is null)
        {
            return null;
        }

        return new DeepFilterNetModelPaths(
            root,
            Path.Combine(root, "enc.onnx"),
            Path.Combine(root, "erb_dec.onnx"),
            Path.Combine(root, "df_dec.onnx"),
            entry.CommercialAllowed,
            entry.CommercialUseVerified);
    }

    public bool IsCommercialSafe => CommercialAllowed && CommercialUseVerified;

    public bool AllFilesExist() =>
        File.Exists(EncPath) && File.Exists(ErbDecPath) && File.Exists(DfDecPath);

    private static string? TryResolveRootDirectory(
        BundledModelManifestEntry entry,
        IReadOnlyList<LocalModelCacheRecord>? cacheRecords)
    {
        if (AllOnnxFilesExist(entry.RootDirectory))
        {
            return entry.RootDirectory;
        }

        if (cacheRecords is null)
        {
            return null;
        }

        foreach (LocalModelCacheRecord cacheRecord in cacheRecords)
        {
            if (!cacheRecord.ModelId.Equals(entry.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AllOnnxFilesExist(cacheRecord.RootPath))
            {
                return cacheRecord.RootPath;
            }
        }

        return null;
    }

    private static bool AllOnnxFilesExist(string rootDirectory) =>
        File.Exists(Path.Combine(rootDirectory, "enc.onnx")) &&
        File.Exists(Path.Combine(rootDirectory, "erb_dec.onnx")) &&
        File.Exists(Path.Combine(rootDirectory, "df_dec.onnx"));
}

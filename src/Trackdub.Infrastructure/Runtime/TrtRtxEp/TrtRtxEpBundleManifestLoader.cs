using System.Text.Json;

namespace Trackdub.Infrastructure.Runtime.TrtRtxEp;

public static class TrtRtxEpBundleManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TrtRtxEpBundleManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("TensorRT RTX EP bundle manifest file was not found.", fullPath);
        }

        using FileStream stream = File.OpenRead(fullPath);
        TrtRtxEpBundleManifestDto? dto = JsonSerializer.Deserialize<TrtRtxEpBundleManifestDto>(stream, JsonOptions);
        if (dto is null || dto.Packages is null || dto.Packages.Count == 0)
        {
            throw new InvalidOperationException("TensorRT RTX EP bundle manifest does not contain any packages.");
        }

        if (dto.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported TensorRT RTX EP bundle manifest schemaVersion {dto.SchemaVersion} (expected 1).");
        }

        var packages = new Dictionary<string, TrtRtxEpBundlePackage>(StringComparer.OrdinalIgnoreCase);
        foreach ((string rid, TrtRtxEpBundlePackageDto packageDto) in dto.Packages)
        {
            if (string.IsNullOrWhiteSpace(packageDto.ArchiveUrl))
            {
                throw new InvalidOperationException($"TensorRT RTX EP manifest package '{rid}' is missing archiveUrl.");
            }

            packages[rid] = new TrtRtxEpBundlePackage(
                packageDto.ArchiveUrl,
                packageDto.ArchiveKind ?? "zip",
                packageDto.Sha256 ?? string.Empty,
                packageDto.SizeBytes);
        }

        return new TrtRtxEpBundleManifest(
            dto.SchemaVersion,
            dto.Version ?? string.Empty,
            dto.CudaVariant ?? string.Empty,
            dto.LicenseUrl ?? string.Empty,
            packages);
    }

    private sealed class TrtRtxEpBundleManifestDto
    {
        public int SchemaVersion { get; set; }

        public string? Version { get; set; }

        public string? CudaVariant { get; set; }

        public string? LicenseUrl { get; set; }

        public Dictionary<string, TrtRtxEpBundlePackageDto>? Packages { get; set; }
    }

    private sealed class TrtRtxEpBundlePackageDto
    {
        public string? ArchiveUrl { get; set; }

        public string? ArchiveKind { get; set; }

        public string? Sha256 { get; set; }

        public long SizeBytes { get; set; }
    }
}

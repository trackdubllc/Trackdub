namespace Trackdub.Application.LipSynthesis;

/// <summary>
/// Manifest aliases required by the M23 lip-synthesis stage in addition to the primary LatentSync bundle.
/// </summary>
public static class LipSynthesisModelRequirements
{
    public const string ScfrdManifestAlias = "InsightFace/scrfd-500m";
    public const string LandmarkManifestAlias = "InsightFace/2d106det";

    public static readonly IReadOnlyList<string> CompanionManifestAliases =
    [
        ScfrdManifestAlias,
        LandmarkManifestAlias
    ];
}

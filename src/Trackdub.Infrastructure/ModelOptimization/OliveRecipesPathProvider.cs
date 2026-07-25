using Trackdub.Contracts.ModelOptimization;

namespace Trackdub.Infrastructure.ModelOptimization;

/// <summary>
/// Resolves the root directory for olive recipes. Checks (in order):
/// 1. TRACKDUB_OLIVE_RECIPES_ROOT environment variable (for custom recipes)
/// 2. Bundled recipes directory within the app (default fallback)
/// </summary>
public sealed class OliveRecipesPathProvider : IOliveRecipesPathProvider
{
    public const string RecipesRootEnvironmentVariable = "TRACKDUB_OLIVE_RECIPES_ROOT";

    public string? TryGetRecipesRoot()
    {
        // Check environment variable first (allows users to override with custom recipes)
        string? fromEnvironment = Environment.GetEnvironmentVariable(RecipesRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            string fullPath = Path.GetFullPath(fromEnvironment.Trim());
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        // Fall back to bundled recipes directory
        string? assemblyLocation = typeof(OliveRecipesPathProvider).Assembly.Location;
        string assemblyDir = string.IsNullOrWhiteSpace(assemblyLocation)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
        string bundledRecipesPath = Path.Combine(assemblyDir, "resources", "olive-recipes");

        return Directory.Exists(bundledRecipesPath) ? bundledRecipesPath : null;
    }
}

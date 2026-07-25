using Trackdub.Application.ModelOptimization;
using Trackdub.Contracts.ModelOptimization;
using Trackdub.Domain;

namespace Trackdub.Application.Tests.ModelOptimization;

public sealed class OliveRecipeResolverTests : IDisposable
{
    private readonly string _recipesRoot;
    private readonly OliveRecipeResolver _resolver = new();

    public OliveRecipeResolverTests()
    {
        _recipesRoot = Path.Combine(Path.GetTempPath(), "Trackdub.OliveRecipeResolver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_recipesRoot);
    }

    [Fact]
    public void Resolve_uses_explicit_override_when_file_exists()
    {
        string overridePath = WriteRecipe("override.json", "{}");

        OliveRecipeResolution resolution = _resolver.Resolve(
            "openai/whisper-tiny",
            "whisper-genai",
            [],
            OliveExecutionProvider.Dml,
            "int8",
            _recipesRoot,
            overridePath);

        Assert.True(resolution.UseRecipe);
        Assert.Equal(overridePath, resolution.RecipeConfigPath);
        Assert.Equal("modellab-override", resolution.Source);
        Assert.False(resolution.IsHardFailure);
    }

    [Fact]
    public void Resolve_fails_hard_when_override_missing()
    {
        OliveRecipeResolution resolution = _resolver.Resolve(
            "openai/whisper-tiny",
            "whisper-genai",
            [],
            OliveExecutionProvider.Dml,
            "int8",
            _recipesRoot,
            explicitRecipeConfigPath: Path.Combine(_recipesRoot, "missing.json"));

        Assert.False(resolution.UseRecipe);
        Assert.True(resolution.IsHardFailure);
    }

    [Fact]
    public void Resolve_matches_manifest_binding_for_pilot_family()
    {
        string configPath = WriteRecipe("openai-whisper-tiny/cpu/whisper-tiny_cpu_int8.json", "{}");
        var bindings = new[]
        {
            new ModelOptimizationRecipeBinding("openai-whisper-tiny/cpu/whisper-tiny_cpu_int8.json", "dml", "int8")
        };

        OliveRecipeResolution resolution = _resolver.Resolve(
            "openai/whisper-tiny",
            "whisper-genai",
            bindings,
            OliveExecutionProvider.Dml,
            "int8",
            _recipesRoot);

        Assert.True(resolution.UseRecipe);
        Assert.Equal(configPath, resolution.RecipeConfigPath);
        Assert.Equal("manifest-binding", resolution.Source);
    }

    [Fact]
    public void Resolve_falls_back_to_auto_opt_when_recipes_root_missing()
    {
        var bindings = new[]
        {
            new ModelOptimizationRecipeBinding("openai-whisper-tiny/cpu/whisper-tiny_cpu_int8.json", "dml", "int8")
        };

        OliveRecipeResolution resolution = _resolver.Resolve(
            "openai/whisper-tiny",
            "whisper-genai",
            bindings,
            OliveExecutionProvider.Dml,
            "int8",
            recipesRoot: null);

        Assert.False(resolution.UseRecipe);
        Assert.Contains("not configured", resolution.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_fails_when_binding_missing_and_policy_is_not_auto_opt()
    {
        var bindings = new[]
        {
            new ModelOptimizationRecipeBinding(
                "openai-whisper-tiny/dml/missing_recipe.json",
                "dml",
                "int8",
                FallbackPolicy: ModelOptimizationFallbackPolicy.BaseVariantAllowed)
        };

        OliveRecipeResolution resolution = _resolver.Resolve(
            "openai/whisper-tiny",
            "whisper-genai",
            bindings,
            OliveExecutionProvider.Dml,
            "int8",
            _recipesRoot,
            profileFallbackPolicy: ModelOptimizationFallbackPolicy.AutoOptAllowed);

        Assert.False(resolution.UseRecipe);
        Assert.True(resolution.IsHardFailure);
        Assert.Contains("Recipe config missing", resolution.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_falls_back_when_engine_family_outside_pilot()
    {
        OliveRecipeResolution resolution = _resolver.Resolve(
            "example/kokoro",
            "kokoro",
            [new ModelOptimizationRecipeBinding("any.json", "cpu", "fp32")],
            OliveExecutionProvider.Cpu,
            "fp32",
            _recipesRoot);

        Assert.False(resolution.UseRecipe);
        Assert.Contains("outside the recipe pilot", resolution.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    private string WriteRecipe(string relativePath, string contents)
    {
        string fullPath = Path.Combine(_recipesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_recipesRoot))
        {
            Directory.Delete(_recipesRoot, recursive: true);
        }
    }
}

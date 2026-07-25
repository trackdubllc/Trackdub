using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackArchitectureTests
{
    [Fact]
    public async Task Bundled_starter_pack_json_loads_via_catalog()
    {
        var catalog = new StarterPackCatalog();
        IReadOnlyList<StarterPackDefinition> packs = await catalog.ListDefinitionsAsync();
        Assert.NotEmpty(packs);
        Assert.Contains(packs, pack => string.Equals(pack.Id, "basic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packs, pack => string.Equals(pack.Id, "balanced", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packs, pack => string.Equals(pack.Id, "premium", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packs, pack => string.Equals(pack.Id, "cloud", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Bundled_shipping_packs_have_apply_contract_entries()
    {
        var catalog = new StarterPackCatalog();
        IReadOnlyList<StarterPackDefinition> packs = await catalog.ListDefinitionsAsync();
        foreach (StarterPackDefinition pack in packs.Where(p => StarterPackShippingIds.IsShippingPack(p.Id)))
        {
            foreach (StarterPackProfileDefinition profile in pack.Profiles)
            {
                StarterPackApplySettings settings = StarterPackApplyContract.Resolve(pack.Id, profile.Id);
                Assert.NotNull(settings);
            }
        }
    }

    [Fact]
    public void Shell_projects_do_not_embed_starter_pack_json_paths()
    {
        string repoRoot = FindRepositoryRoot();
        string cliProject = Path.Combine(repoRoot, "src", "Trackdub.Cli", "Trackdub.Cli.csproj");

        string cliContents = File.ReadAllText(cliProject);

        Assert.DoesNotContain("StarterPacks\\", cliContents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StarterPacks/", cliContents, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "Trackdub.slnx")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}

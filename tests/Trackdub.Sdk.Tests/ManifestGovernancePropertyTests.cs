using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Sdk;
using Trackdub.Sdk.Composition;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Property-based tests verifying manifest governance: the SDK loads
/// <c>bundled-models.manifest.json</c> at build time and rejects unknown models
/// before session creation.
///
/// **Validates: Requirements 9.1, 9.2, 10.2**
/// </summary>
public sealed class ManifestGovernancePropertyTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    /// <summary>
    /// After building a factory via the headless composition root, the
    /// <see cref="BundledModelManifestRegistry"/> is available in the DI container
    /// and contains at least one entry (manifest loaded successfully).
    ///
    /// **Validates: Requirements 9.1, 9.2, 10.2**
    /// </summary>
    [Fact]
    public void Factory_ResolvesManifestRegistry_WithEntries()
    {
        using var factory = CreateFactory();
        string tempDir = CreateTempProjectDir();
        using var session = factory.CreateSession(tempDir);

        var registry = session.GetServiceProvider()!.GetRequiredService<BundledModelManifestRegistry>();

        Assert.NotNull(registry);
        Assert.NotEmpty(registry.Entries);
    }

    /// <summary>
    /// Property 7: Manifest governance — for any random string that is not a known model alias,
    /// the registry does not resolve it. This ensures unknown models are rejected.
    ///
    /// **Validates: Requirements 9.1, 9.2, 10.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public bool UnknownModelAlias_IsNotResolved_ByRegistry(NonEmptyString randomAlias)
    {
        string alias = randomAlias.Get;

        using var factory = CreateFactory();
        string tempDir = CreateTempProjectDir();
        using var session = factory.CreateSession(tempDir);

        var registry = session.GetServiceProvider()!.GetRequiredService<BundledModelManifestRegistry>();

        // Skip if the random string happens to match a real alias (extremely unlikely but possible)
        var knownAliases = registry.Entries
            .SelectMany(e => e.Aliases)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (knownAliases.Contains(alias))
            return true; // vacuously true — this is a known alias, not an unknown one

        bool resolved = registry.TryResolve(alias, out _);
        return !resolved;
    }

    /// <summary>
    /// The registry is registered as a singleton — the same instance is returned
    /// across multiple sessions created from the same factory.
    ///
    /// **Validates: Requirements 9.1, 9.2, 10.2**
    /// </summary>
    [Fact]
    public void Registry_IsSingleton_AcrossSessions()
    {
        using var factory = CreateFactory();

        string tempDir1 = CreateTempProjectDir();
        string tempDir2 = CreateTempProjectDir();

        using var session1 = factory.CreateSession(tempDir1);
        using var session2 = factory.CreateSession(tempDir2);

        var registry1 = session1.GetServiceProvider()!.GetRequiredService<BundledModelManifestRegistry>();
        var registry2 = session2.GetServiceProvider()!.GetRequiredService<BundledModelManifestRegistry>();

        Assert.Same(registry1, registry2);
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically,
    /// ensuring test discovery works with xunit.runner.visualstudio v3.
    /// Tests the manifest governance property: unknown aliases are never resolved.
    ///
    /// **Validates: Requirements 9.1, 9.2, 10.2**
    /// </summary>
    [Fact]
    public void ManifestGovernance_PropertyCheck_ViaFact()
    {
        using var factory = CreateFactory();
        string tempDir = CreateTempProjectDir();
        using var session = factory.CreateSession(tempDir);

        var registry = session.GetServiceProvider()!.GetRequiredService<BundledModelManifestRegistry>();
        var knownAliases = registry.Entries
            .SelectMany(e => e.Aliases)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Prop.ForAll(Arb.From<NonEmptyString>(), randomAlias =>
        {
            string alias = randomAlias.Get;

            // Skip known aliases — we only test unknown ones
            if (knownAliases.Contains(alias))
                return true;

            bool resolved = registry.TryResolve(alias, out _);
            return !resolved;
        }).QuickCheckThrowOnFailure();
    }

    private TrackdubSessionFactory CreateFactory()
    {
        var options = new TrackdubOptions();
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(options);
        var provider = services.BuildServiceProvider();
        return new TrackdubSessionFactory(provider);
    }

    private string CreateTempProjectDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        string parentDir = Path.Combine(Path.GetTempPath(), "TrackdubTests");
        try
        {
            if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                Directory.Delete(parentDir);
        }
        catch { /* best-effort cleanup */ }
    }
}

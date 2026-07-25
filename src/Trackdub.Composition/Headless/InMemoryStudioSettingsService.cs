using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Composition.Headless;

/// <summary>
/// In-memory implementation of <see cref="IStudioSettingsService"/> for headless usage.
/// Stores all mutations in memory without file I/O.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class InMemoryStudioSettingsService : IStudioSettingsService
{
    private const int RecentProjectLimit = 10;

    private readonly object _lock = new();
    private StudioSettings _settings;

    public InMemoryStudioSettingsService(HeadlessTrackdubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _settings = StudioSettings.Default with
        {
            HardwareOverrides = options.HardwareOverrides is not null
                ? new Dictionary<string, ExecutionProviderKind>(options.HardwareOverrides)
                : new Dictionary<string, ExecutionProviderKind>(),
            WindowsMlExecutionDevicePolicy = options.WindowsMlExecutionDevicePolicy,
        };
    }

    public Task<StudioSettings> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_settings);
        }
    }

    public Task SaveAsync(StudioSettings settings, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    public Task<StudioSettings> TouchRecentProjectAsync(
        string projectPath,
        string projectName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        lock (_lock)
        {
            string normalizedPath = Path.GetFullPath(projectPath);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            RecentProjectEntry entry = new(projectName.Trim(), normalizedPath, now);

            RecentProjectEntry[] updatedRecentProjects =
            [
                entry,
                .. _settings.RecentProjects
                    .Where(candidate => !string.Equals(candidate.ProjectPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(candidate => candidate.LastOpenedAtUtc)
                    .Take(RecentProjectLimit - 1)
            ];

            _settings = _settings with { RecentProjects = updatedRecentProjects };
            return Task.FromResult(_settings);
        }
    }
}

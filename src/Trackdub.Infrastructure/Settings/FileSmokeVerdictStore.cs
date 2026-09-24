using System.Text.Json;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Settings;

/// <summary>
/// Tiny JSON-file-backed <see cref="ISmokeVerdictStore"/>. Holds one environment's verdicts at a
/// time: recording under a new GPU-arch/driver/TRT-RTX-EP fingerprint drops older environments, so
/// a component change both misses lookups and reclaims the file.
/// </summary>
public sealed class FileSmokeVerdictStore : ISmokeVerdictStore
{
    private const int MaxEntries = 512;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string filePath;
    private readonly object gate = new();
    private Dictionary<string, string> entries;
    private bool loaded;

    public FileSmokeVerdictStore(string filePath)
    {
        this.filePath = !string.IsNullOrWhiteSpace(filePath)
            ? Path.GetFullPath(filePath)
            : throw new ArgumentException("Smoke verdict store path must not be empty.", nameof(filePath));
        entries = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public bool IsVerified(SmokeVerdictKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (gate)
        {
            EnsureLoaded();
            return entries.ContainsKey(key.ToStableString());
        }
    }

    public void RecordVerified(SmokeVerdictKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (gate)
        {
            EnsureLoaded();

            // Drop verdicts recorded under a different GPU arch / driver / TRT RTX EP version.
            // Those are exactly the components that invalidate compiled engine cache entries.
            string environment = key.EnvironmentFingerprint;
            foreach (string stale in entries
                         .Where(entry => !entry.Key.EndsWith(environment, StringComparison.Ordinal))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                entries.Remove(stale);
            }

            entries[key.ToStableString()] = DateTimeOffset.UtcNow.ToString("O");
            EvictOldestBeyondCapacity();
            Save();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            loaded = true;
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: an empty in-memory set is still authoritative for this process.
            }
        }
    }

    private void EnsureLoaded()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        entries = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            SmokeVerdictFilePayload? payload = JsonSerializer.Deserialize<SmokeVerdictFilePayload>(
                File.ReadAllText(filePath),
                SerializerOptions);
            if (payload?.Verified is null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> entry in payload.Verified)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    entries[entry.Key] = entry.Value ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable store: treat as empty rather than failing planning.
        }
    }

    private void EvictOldestBeyondCapacity()
    {
        if (entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (string oldest in entries
                     .OrderBy(entry => entry.Value, StringComparer.Ordinal)
                     .Take(entries.Count - MaxEntries)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            entries.Remove(oldest);
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new SmokeVerdictFilePayload
            {
                Version = 1,
                Verified = entries,
            };
            File.WriteAllText(filePath, JsonSerializer.Serialize(payload, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort: a failed write only costs a re-smoke next launch.
        }
    }

    private sealed class SmokeVerdictFilePayload
    {
        public int Version { get; set; }

        public Dictionary<string, string>? Verified { get; set; }
    }
}

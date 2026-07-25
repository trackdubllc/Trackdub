using System.Text.Json;

namespace Trackdub.Benchmarks;

public sealed class BenchmarkSelectionDefaultsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string defaultsPath;
    private readonly Dictionary<string, string> defaults;

    private BenchmarkSelectionDefaultsStore(string defaultsPath, Dictionary<string, string> defaults)
    {
        this.defaultsPath = defaultsPath;
        this.defaults = defaults;
    }

    public static BenchmarkSelectionDefaultsStore LoadDefault()
    {
        string overridePath = Environment.GetEnvironmentVariable("TRACKDUB_BENCHMARK_DEFAULTS_PATH") ?? string.Empty;
        string defaultsPath = !string.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Trackdub",
                "benchmark-defaults.json");

        if (!File.Exists(defaultsPath))
        {
            return new BenchmarkSelectionDefaultsStore(defaultsPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            string json = File.ReadAllText(defaultsPath);
            Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions);
            return new BenchmarkSelectionDefaultsStore(
                defaultsPath,
                values is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return new BenchmarkSelectionDefaultsStore(defaultsPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    public bool TryGet(string scopeKey, out string? candidateKey) =>
        defaults.TryGetValue(scopeKey, out candidateKey);

    public void Set(string scopeKey, string candidateKey) =>
        defaults[scopeKey] = candidateKey;

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(defaultsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(defaultsPath);
        await JsonSerializer.SerializeAsync(stream, defaults, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }
}

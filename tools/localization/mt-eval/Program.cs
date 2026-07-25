using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Trackdub.Composition.Runtime.Planning;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Phi;
using Trackdub.Inference.Onnx.QwenTextRefinement;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.LocalizationEval;

// Throwaway pilot: Phi-4-mini (blind translation) -> Qwen2.5 (translation-polish cleanup),
// run through the REAL production engines (PhiGenAiTranslationEngine, QwenTextRefinementEngine)
// on a representative slice of the English master resx, for a handful of pilot locales.
// Output feeds an external LLM-judge pass; this program only produces candidates.
internal static class Program
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string MasterResxPath = Path.Combine(RepoRoot, "src", "Trackdub.App.Avalonia", "Resources", "App.resx");
    private static readonly string GlossaryPath = Path.Combine(RepoRoot, "tools", "localization", "glossary.md");
    private static readonly string OutputDir = Path.Combine(RepoRoot, "tools", "localization", "mt-eval", "output");

    // Pilot locales agreed with Tony: es (Latin/easy), ja + zh (CJK), ar (RTL).
    private static readonly string[] PilotLanguages = ["es", "ja", "zh", "ar"];

    private static async Task<int> Main(string[] args)
    {
        int sliceSize = 50;
        if (args.Length > 0 && int.TryParse(args[0], out int requested))
        {
            sliceSize = requested;
        }

        Directory.CreateDirectory(OutputDir);

        Console.WriteLine("Trackdub localization MT pilot (Phi-4-mini -> Qwen2.5 cleanup)");
        Console.WriteLine($"Master:   {MasterResxPath}");
        Console.WriteLine($"Output:   {OutputDir}");
        Console.WriteLine($"Locales:  {string.Join(", ", PilotLanguages)}");

        List<ResxEntry> allEntries = ParseResx(MasterResxPath);
        List<TranslationGlossaryHint> glossaryHints = ParseGlossary(GlossaryPath);
        List<ResxEntry> slice = SelectRepresentativeSlice(allEntries, sliceSize, glossaryHints);
        Console.WriteLine($"Parsed:   {allEntries.Count} strings total, {slice.Count} selected for the pilot slice");

        // --- Wire the REAL production runtime graph (same objects Composition would give the app) ---
        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? manifestError))
        {
            Console.Error.WriteLine($"Failed to load bundled model manifest: {manifestError}");
            return 1;
        }

        var hardwareProfileProvider = new MachineHardwareProfileProvider();
        var executionProviderDiscovery = new OnnxExecutionProviderDiscovery();
        var smokeTester = new OnnxExecutionProviderSmokeTester();
        var modelCacheInventory = new BundledManifestModelCacheInventory(registry!);
        var runtimePlanner = new RuntimePlanner(
            registry!,
            hardwareProfileProvider,
            executionProviderDiscovery,
            smokeTester,
            modelCacheInventory);
        BenchmarkModelPathResolver modelPathResolver = BenchmarkModelPathResolver.CreateDefault();

        var phiEngine = new PhiGenAiTranslationEngine(runtimePlanner, modelPathResolver);
        var qwenEngine = new QwenTextRefinementEngine(runtimePlanner, modelPathResolver);

        foreach (string lang in PilotLanguages)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {lang} ===");
            await RunLanguageAsync(lang, slice, glossaryHints, phiEngine, qwenEngine).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("Done. Candidates + compare JSON written under:");
        Console.WriteLine($"  {OutputDir}");
        Console.WriteLine("Next: run the LLM-judge pass over compare.<lang>.json (V0 baseline vs V1 phi vs V2 phi+qwen).");
        return 0;
    }

    private static async Task RunLanguageAsync(
        string lang,
        List<ResxEntry> slice,
        List<TranslationGlossaryHint> glossaryHints,
        PhiGenAiTranslationEngine phiEngine,
        QwenTextRefinementEngine qwenEngine)
    {
        var segments = slice
            .Select((entry, index) => new TranslationInputSegment(index, 0, 0, entry.Value))
            .ToList();

        Console.WriteLine($"[{lang}] Phi-4-mini blind translation ({segments.Count} strings)...");
        var translationRequest = new TranslationRequest(
            SourceLanguage: "en",
            TargetLanguage: lang,
            Segments: segments,
            GlossaryHints: glossaryHints,
            PreferredModelAlias: "phi-4-mini-genai");

        IReadOnlyList<TranslatedTextSegment> v1 = await phiEngine.TranslateAsync(translationRequest, CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine($"[{lang}] Phi-4-mini done. Engine summary: {phiEngine.LastExecutionSummary?.BootstrapDetail}");

        Console.WriteLine($"[{lang}] Qwen2.5 translation-polish cleanup...");
        var refinementSegments = v1
            .Select(t => new TextRefinementInputSegment(t.Index, 0, 0, t.Text))
            .ToList();
        var refinementRequest = new TextRefinementRequest(
            Segments: refinementSegments,
            Scope: TextRefinementScope.Translation,
            SourceLanguage: "en",
            TargetLanguage: lang);

        IReadOnlyList<RefinedTextSegment> v2 = await qwenEngine.RefineAsync(refinementRequest, CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine($"[{lang}] Qwen2.5 done. Engine summary: {qwenEngine.LastExecutionSummary?.BootstrapDetail}");

        Dictionary<string, string> baseline = LoadBaseline(lang);

        var compareRows = new List<CompareRow>(slice.Count);
        for (int i = 0; i < slice.Count; i++)
        {
            string key = slice[i].Name;
            baseline.TryGetValue(key, out string? v0);
            compareRows.Add(new CompareRow(
                Key: key,
                English: slice[i].Value,
                Baseline: v0 ?? string.Empty,
                Phi: v1[i].Text,
                PhiQwen: v2[i].DisplayedText));
        }

        WriteResx(lang, "phi", slice, v1.Select(t => t.Text).ToList());
        WriteResx(lang, "phiqwen", slice, v2.Select(t => t.DisplayedText).ToList());
        WriteCompareJson(lang, compareRows);
    }

    // --- English master (509 strings) is too much for a pilot. Pick a compact, diverse slice: ---
    // placeholder strings, glossary-term strings, then evenly spaced fill to reach the target count.
    private static List<ResxEntry> SelectRepresentativeSlice(
        List<ResxEntry> all,
        int targetCount,
        List<TranslationGlossaryHint> glossaryHints)
    {
        var picked = new List<ResxEntry>();
        var pickedNames = new HashSet<string>(StringComparer.Ordinal);

        void Add(ResxEntry entry)
        {
            if (pickedNames.Add(entry.Name))
            {
                picked.Add(entry);
            }
        }

        foreach (ResxEntry entry in all.Where(e => e.Value.Contains('{')))
        {
            Add(entry);
        }

        foreach (ResxEntry entry in all)
        {
            if (glossaryHints.Any(h => entry.Value.Contains(h.SourceTerm, StringComparison.OrdinalIgnoreCase)))
            {
                Add(entry);
            }
        }

        if (picked.Count < targetCount)
        {
            int remaining = targetCount - picked.Count;
            int step = Math.Max(1, all.Count / Math.Max(1, remaining));
            for (int i = 0; i < all.Count && picked.Count < targetCount; i += step)
            {
                Add(all[i]);
            }
        }

        return picked.Take(targetCount).ToList();
    }

    private static Dictionary<string, string> LoadBaseline(string lang)
    {
        string suffix = lang switch
        {
            "zh" => "zh-hans",
            _ => lang
        };
        string path = Path.Combine(RepoRoot, "src", "Trackdub.App.Avalonia", "Resources", $"App.{suffix}.resx");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return ParseResx(path).ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);
    }

    private static List<ResxEntry> ParseResx(string path)
    {
        XDocument doc = XDocument.Load(path);
        var entries = new List<ResxEntry>();
        foreach (XElement dataElement in doc.Root!.Elements("data"))
        {
            string? name = dataElement.Attribute("name")?.Value;
            string? value = dataElement.Element("value")?.Value;
            if (!string.IsNullOrEmpty(name) && value is not null)
            {
                entries.Add(new ResxEntry(name, value));
            }
        }

        return entries;
    }

    private static List<TranslationGlossaryHint> ParseGlossary(string path)
    {
        var hints = new List<TranslationGlossaryHint>();
        if (!File.Exists(path))
        {
            return hints;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string term = trimmed[..eq].Trim();
            string replacement = trimmed[(eq + 1)..].Trim();
            if (term.Length > 0 && replacement.Length > 0)
            {
                hints.Add(new TranslationGlossaryHint(term, replacement, IsCaseSensitive: false));
            }
        }

        return hints;
    }

    private static void WriteResx(string lang, string variant, List<ResxEntry> slice, List<string> values)
    {
        var root = new XElement("root");
        for (int i = 0; i < slice.Count; i++)
        {
            root.Add(new XElement("data",
                new XAttribute("name", slice[i].Name),
                new XAttribute(XNamespace.Xml + "space", "preserve"),
                new XElement("value", values[i])));
        }

        string outPath = Path.Combine(OutputDir, $"App.{lang}.{variant}.resx");
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var writer = new StreamWriter(outPath, false, new UTF8Encoding(false));
        doc.Save(writer);
    }

    private static void WriteCompareJson(string lang, List<CompareRow> rows)
    {
        string outPath = Path.Combine(OutputDir, $"compare.{lang}.json");
        string json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json, new UTF8Encoding(false));
        Console.WriteLine($"[{lang}] wrote {outPath}");
    }

    private static string ResolveRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Trackdub.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not locate repo root (Trackdub.sln) from " + dir);
    }

    private sealed record ResxEntry(string Name, string Value);

    private sealed record CompareRow(string Key, string English, string Baseline, string Phi, string PhiQwen);
}

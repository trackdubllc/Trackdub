using System.Text.RegularExpressions;

namespace Trackdub.Architecture.Tests;

/// <summary>
/// Enforces that every <c>StageRunRecord.Start</c> call site passes a <c>StageNames.*</c>
/// constant rather than an inline string literal.
///
/// Adding a new stage requires two steps:
///   1. Add a constant to <c>StageNames</c> in <c>Trackdub.Domain</c>.
///   2. Use that constant at every call site.
///
/// If step 2 is skipped, this test catches the regression immediately.
/// </summary>
public sealed class StageNameConsistencyTests
{
    /// <summary>
    /// The set of known stage name values defined in <c>StageNames</c>.
    /// Keep this in sync with <c>Trackdub.Domain.StageRuns.StageNames</c>.
    /// The test itself enforces that no literal from this set reaches a call site —
    /// extending StageNames without updating this list simply means the new value
    /// won't be caught, so always add new values here when adding to StageNames.
    /// </summary>
    private static readonly IReadOnlyList<string> KnownStageNameValues =
    [
        "vad",
        "asr",
        "diarization",
        "speaker-assignment",
        "translation",
        "text-refinement",
        "tts",
        "separation",
        "speech-enhancement",
        "audio-preparation",
        "preview-mix",
        "voice-cloning",
        "export",
        "lip-sync",
        "lip-synthesis",
        "overlap-rescue",
        "text-refinement-asr",
        "text-refinement-translation",
    ];

    // Marker used to find StageRunRecord.Start call sites before balanced-parenthesis parsing.
    private const string StartCallMarker = "StageRunRecord.Start(";

    [Fact]
    public void StageRunRecord_Start_never_receives_inline_string_literal()
    {
        string repoRoot = FindRepoRoot();
        string srcRoot = Path.Combine(repoRoot, "src");
        var knownValues = new HashSet<string>(KnownStageNameValues, StringComparer.Ordinal);

        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            foreach (var candidate in EnumerateStartCallInlineStringLiterals(source))
            {
                // Skip lines that are comments or inside string interpolation noise.
                int lineNumber = GetLineNumber(source, candidate.Index);
                string trimmed = GetLineText(source, lineNumber).TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                string literal = candidate.Literal;
                if (knownValues.Contains(literal))
                {
                    string relativePath = Path.GetRelativePath(repoRoot, file);
                    offenders.Add($"  {relativePath}:{lineNumber}  \"{literal}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "StageRunRecord.Start() was called with an inline stage name literal instead of a StageNames.* constant.\n" +
            "Replace each literal with the corresponding StageNames constant:\n" +
            string.Join("\n", offenders));
    }

    private static IEnumerable<(string Literal, int Index)> EnumerateStartCallInlineStringLiterals(string source)
    {
        int searchStart = 0;
        while (true)
        {
            int markerIndex = source.IndexOf(StartCallMarker, searchStart, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                yield break;
            }

            int openParenIndex = markerIndex + StartCallMarker.Length - 1;
            if (TryFindMatchingParen(source, openParenIndex, out int closeParenIndex))
            {
                int argsStart = openParenIndex + 1;
                int argsLength = closeParenIndex - argsStart;
                string argsText = source.Substring(argsStart, argsLength);

                foreach (var literal in ExtractStringLiterals(argsText, argsStart))
                {
                    yield return literal;
                }

                searchStart = closeParenIndex + 1;
            }
            else
            {
                searchStart = openParenIndex + 1;
            }
        }
    }

    private static bool TryFindMatchingParen(string source, int openParenIndex, out int closeParenIndex)
    {
        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = openParenIndex; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (inChar)
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '\'')
                {
                    inChar = false;
                }
                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    closeParenIndex = i;
                    return true;
                }
            }
        }

        closeParenIndex = -1;
        return false;
    }

    private static IEnumerable<(string Literal, int Index)> ExtractStringLiterals(string text, int offset)
    {
        bool inString = false;
        int start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                    start = i + 1;
                }
                continue;
            }

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                string literal = text.Substring(start, i - start);
                yield return (literal, offset + start);
                inString = false;
            }
        }
    }

    /// <summary>
    /// Verifies that every value defined in <c>StageNames</c> (read from source)
    /// is also present in <see cref="KnownStageNameValues"/>, so this test class
    /// stays in sync with the domain constant file.
    /// </summary>
    [Fact]
    public void KnownStageNameValues_covers_all_StageNames_constants()
    {
        string repoRoot = FindRepoRoot();
        string stageNamesPath = Path.Combine(
            repoRoot, "src", "Trackdub.Domain", "StageRuns", "StageNames.cs");

        Assert.True(
            File.Exists(stageNamesPath),
            $"StageNames.cs was not found at the expected path: {stageNamesPath}");

        // Extract the string values assigned to public const string fields.
        string source = File.ReadAllText(stageNamesPath);
        var constPattern = new Regex(
            @"public\s+const\s+string\s+\w+\s*=\s*""(?<value>[^""]+)""",
            RegexOptions.CultureInvariant);

        var definedValues = constPattern.Matches(source)
            .Select(m => m.Groups["value"].Value)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        var knownValues = KnownStageNameValues
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        var missingFromKnown = definedValues.Except(knownValues, StringComparer.Ordinal).ToList();

        Assert.True(
            missingFromKnown.Count == 0,
            "The following stage name values are defined in StageNames.cs but are missing from " +
            $"{nameof(StageNameConsistencyTests)}.{nameof(KnownStageNameValues)}. " +
            "Add them so inline-literal detection stays complete:\n  " +
            string.Join("\n  ", missingFromKnown.Select(v => $"\"{v}\"")));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Trackdub.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Trackdub.slnx from the test runner base directory.");
    }

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLineNumber(string source, int index) =>
        source.AsSpan(0, index).Count('\n') + 1;

    private static string GetLineText(string source, int lineNumber)
    {
        int currentLine = 1;
        int lineStart = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (currentLine == lineNumber && source[i] is '\r' or '\n')
            {
                return source[lineStart..i];
            }

            if (source[i] is not '\n')
            {
                continue;
            }

            currentLine++;
            lineStart = i + 1;
        }

        return currentLine == lineNumber
            ? source[lineStart..]
            : string.Empty;
    }
}

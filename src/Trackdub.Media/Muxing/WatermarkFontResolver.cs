namespace Trackdub.Media.Muxing;

/// <summary>
/// Locates a system font for the export watermark's drawtext filter. drawtext without
/// an explicit <c>fontfile</c> relies on fontconfig to pick a default font, which fails
/// on machines without a fontconfig setup (notably Windows), crashing the export mux.
/// </summary>
internal static class WatermarkFontResolver
{
    /// <summary>
    /// Returns the first existing system font path, or null when none is found.
    /// Pure filesystem probe: no process, no network, safe to call from the export stage.
    /// </summary>
    internal static string? ResolveFontPath() =>
        CandidateFontPaths().FirstOrDefault(File.Exists);

    internal static IEnumerable<string> CandidateFontPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            string windowsFonts = Path.Join(
                Environment.GetEnvironmentVariable("WINDIR") is { Length: > 0 } windir
                    ? windir
                    : @"C:\Windows",
                "Fonts");
            yield return Path.Join(windowsFonts, "arial.ttf");
            yield return Path.Join(windowsFonts, "calibri.ttf");
            yield return Path.Join(windowsFonts, "segoeui.ttf");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts/Helvetica.ttc";
            yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
            yield break;
        }

        yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
        yield return "/usr/share/fonts/TTF/DejaVuSans.ttf";
    }
}

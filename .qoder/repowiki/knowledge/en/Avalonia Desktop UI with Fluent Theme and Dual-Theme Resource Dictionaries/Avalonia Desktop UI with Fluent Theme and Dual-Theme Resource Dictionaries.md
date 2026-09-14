---
kind: frontend_style
name: Avalonia Desktop UI with Fluent Theme and Dual-Theme Resource Dictionaries
category: frontend_style
scope:
    - '**'
source_files:
    - src/DubBench/App.axaml
    - src/DubBench/Trackdub.DubBench.csproj
    - docs/reference/design-standards.md
---

Trackdub's desktop UI is built with **Avalonia** (XAML-based cross-platform .NET UI framework). The only in-repo UI surface is the `DubBench` benchmark harness under `src/DubBench`, which serves as the canonical reference for Trackdub's application styling. The main product shell (`Trackdub.App.Avalonia`) is referenced in documentation but not present in this public core repository.

### Styling system
- **Avalonia XAML** is the markup language; views live in `src/DubBench/Views/*.axaml` and the application root in `App.axaml`.
- **Fluent theme base**: `Avalonia.Themes.Fluent` is applied via `<FluentTheme />` in `Application.Styles`, providing the default control styling.
- **Dual resource dictionaries**: Dark and Light themes are defined as separate `ResourceDictionary.ThemeDictionaries` entries (`DubBenchTheme.axaml` and `DubBenchThemeLight.axaml`), selected through `RequestedThemeVariant="Dark"` on the Application element.
- **Custom brushes**: Colors are exposed as named brushes (`ThemeBackgroundBrush`, `ThemeSurfaceBrush`, `ThemeAccentBrush`, etc.) rather than inline hex values, enabling theme switching.

### Design tokens & brand alignment
- A comprehensive design standards document (`docs/reference/design-standards.md`) defines the canonical brand palette from `trackdub.com` (Tailwind CSS v4, oklch color space, semantic tokens like `--background`, `--accent`, `--destructive`).
- The Avalonia teal accent (`#00BFA5` / `#009688`) is explicitly marked as **legacy migration debt**; new UI must adopt the marketing site's amber/gold/burgundy palette instead.
- Typography relies on system fonts in Avalonia; the marketing site uses IBM Plex Sans, Instrument Serif, and JetBrains Mono.

### CLI terminal UI
- The CLI (`src/Trackdub.Cli/Tui/`) uses **Spectre.Console** for its terminal UI, independent of the Avalonia theme — it uses Spectre.Console's default named colors (Blue, Grey, Cyan1).

### Conventions observed
- Views follow a consistent layout: left sidebar (50 px) with emoji icon tab buttons, `ContentControl` main area with 16 px margin, `ScrollViewer` + vertical `StackPanel` with `Spacing="12"`.
- Cards use `CornerRadius="6"`, border thickness 1, padding 12, background from `ThemeSurfaceBrush` / `ContentBackgroundBrush`.
- Buttons use fixed dimensions with `ButtonPrimaryBackground` / `ButtonPrimaryText` brushes.
- Hardcoded colors outside theme brushes (e.g., green banner backgrounds in `LeaderboardTabView.axaml`) are flagged for migration to theme resources.
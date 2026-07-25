# Adding a New Language

This guide explains how to add a new display language to Trackdub. No code changes are required — only a resource file.

## Steps

### 1. Create the resource file

Copy `src/Trackdub.App.Avalonia/Resources/App.resx` and rename it using the BCP-47 culture code:

```
src/Trackdub.App.Avalonia/Resources/App.{culture}.resx
```

Examples:
- `App.fr.resx` — French
- `App.de.resx` — German
- `App.ja.resx` — Japanese
- `App.pt-BR.resx` — Brazilian Portuguese

The culture code must be a valid .NET `CultureInfo` name.

### 2. Translate all keys

Open the new `.resx` file and translate every `<data>` entry. The English file (`App.resx`) is the source of truth — your new file must contain the same set of keys with translated values.

Key naming convention:

| Prefix | Scope | Example |
|--------|-------|---------|
| `Transport.*` | Playback bar tooltips/labels | `Transport.ToolTip_Play` |
| `Titlebar.*` | Title bar buttons | `Titlebar.ToolTip_Settings` |
| `Speakers.*` | Voices & Speakers panel | `Speakers.Header` |
| `Speaker.*` | Individual speaker card | `Speaker.Label_Voice` |
| `Timeline.*` | Timeline dock | `Timeline.Header` |
| `Pipeline.*` | Pipeline stages | `Pipeline.Status_Running` |
| `Settings.*` | Settings window | `Settings.Label_Language` |
| `Dialog.*` | Dialog titles/messages | `Dialog.Title_VoiceCloneConsent` |
| `Common.*` | Shared strings | `Common.Close`, `Common.Cancel` |

Some values contain format placeholders (e.g. `{0} speakers`). Preserve the `{0}`, `{1}`, etc. tokens in your translation.

### 3. Build

```powershell
dotnet build Trackdub.sln -m:1
```

The build compiles the `.resx` into a satellite assembly placed in a culture-named subdirectory (e.g. `bin/.../fr/Trackdub.App.Avalonia.resources.dll`).

### 4. Verify

Run the resource key parity property test to confirm your file has all required keys with non-empty values:

```powershell
dotnet test tests/Trackdub.App.Avalonia.Tests --filter "FullyQualifiedName~ResourceKeyParity" --no-restore -m:1
```

## How auto-discovery works

At startup, `AvaloniaAppLanguageService` scans subdirectories under the application base directory. Any subdirectory whose name is a valid culture code and contains `Trackdub.App.Avalonia.resources.dll` is registered as an available language. English is always included as a hardcoded fallback.

The language selector in Settings automatically displays all discovered languages using their native display name (e.g. "Français", "日本語"). No code changes, no registration, no configuration — just the resource file.

## What you do NOT need to change

- No modifications to `IAppLanguageService` or its implementation
- No changes to XAML views or view models
- No updates to the language selector UI
- No changes to the startup sequence

The only deliverable is the `App.{culture}.resx` file with complete translations.

## RTL languages

If the new language is right-to-left (Arabic, Hebrew, etc.), Avalonia automatically applies `FlowDirection.RightToLeft` to the main window based on `CultureInfo.TextInfo.IsRightToLeft`. No additional configuration is needed.

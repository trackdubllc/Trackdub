# Design Standards

This document collects the visual design tokens and patterns used by Trackdub. It combines evidence from the marketing site (`trackdub.com`) and the Avalonia/CLI UI code in this repository.

## Canonical source

**trackdub.com** (Autumn Harvest palette, Instrument Serif / IBM Plex Sans / JetBrains Mono) is the canonical brand language for all Trackdub designs: marketing, portal, desktop, and future surfaces.

| Source | Role |
|---|---|
| [trackdub.com](https://trackdub.com) + `trackdub.com/src/styles.css` | Living implementation of the brand |
| [Figma: Trackdub Design System](https://www.figma.com/design/vUAGF65aDO1a83u5COYWBG/Untitled) (`vUAGF65aDO1a83u5COYWBG`) | Design tokens, components, and annotated requirements for review |
| This document | Written inventory and migration notes |

Rename the Figma file to `Trackdub — Design System` in the Figma UI if it still shows as Untitled (plugin API cannot rename the file).

### Qodo Design Review

Qodo can compare frontend PRs to linked Figma requirements and surface **UX deviation** findings.

1. Qodo Portal → **Integrations → Documentation & Design → Connect Figma**
2. **Configuration → code review → Advanced → enable Design Review**
3. Include **exactly one** Figma URL in the PR body (first Figma URL wins), for example:

```text
Design: https://www.figma.com/design/vUAGF65aDO1a83u5COYWBG/Untitled
```

Prefer linking a specific requirements frame on page `03 Requirements` when the PR is scoped (Hero, Pipeline, Pricing).

## 1. Design Principles

Trackdub's product voice is built around a small set of principles. These appear repeatedly in the marketing copy and should guide UI decisions:

- **Local by default**: media, transcripts, and generated audio stay on the user's disk.
- **Deterministic runs**: the same manifest plus the same models produces the same output.
- **Cross-platform**: Windows, macOS, and Linux share the same project format.
- **Open manifest**: every bundled model is itemized with source, checksum, and license.
- **No account required**: the tool does not depend on cloud identity.
- **Per-line regen**: a single line, speaker, or stage can be regenerated without redoing everything.
- **Resumable jobs**: checkpoints let the app close and continue from the last completed stage.
- **CPU fallback, always**: every stage can run without an accelerator.
- **Your disk, your files**: the user owns and controls project output.

Source: `trackdub.com/` landing page, value-pillar copy, 2026-07-26.

## 2. Brand & Voice

- **Tagline**: "Dub videos into other languages without giving up control."
- **Meta description**: "Trackdub is a local-first desktop workstation for dubbing video into other languages. Editable stages, deterministic runs, your media stays on your machine."
- **Open Graph image**: `https://trackdub.com/og.png`, 1200 x 630.
- **Site structure** (navigation): Pipeline, Try it, Resume, Control, Performance, Local-first, Manifest, Pricing, FAQ, Launch list.
- **Page narrative**: "A desktop workstation", "Built for control at every stage", "Edit a line. Watch what invalidates.", "Pause anything. Edit one stage. Resume only what changed.", "Runs on the hardware you already have.", "Your media is yours. Here is the whole map."

Source: `trackdub.com/src/routes/index.tsx` and `__root.tsx` head meta, 2026-07-26.

## 3. Color

### Marketing site palette

The site uses **Tailwind CSS v4** with `@theme inline` and CSS custom properties. The default light theme is described in `styles.css` as the warm-cream "Autumn Harvest" persona. A `.dark` class provides a dark blue theme. Individual page sections also override these with "chapter" utilities.

#### Brand anchors

| Token | oklch | Approximate hex | Role |
|---|---|---|---|
| `--paper` | `oklch(0.955 0.024 78)` | `#f5ede0` | warm cream background |
| `--paper-2` | `oklch(0.915 0.032 74)` | | deeper cream |
| `--ink-raw` | `oklch(0.14 0.028 45)` | | charred brown-black |
| `--ash` | `oklch(0.215 0.028 46)` | | warm graphite |
| `--burgundy` | `oklch(0.32 0.115 32)` | | deep wine |
| `--rust` | `oklch(0.44 0.14 42)` | | mid rust |
| `--amber` | `oklch(0.74 0.17 62)` | | warm amber accent |
| `--gold` | `oklch(0.86 0.15 82)` | | soft gold accent |
| `--cream` | `oklch(0.965 0.024 78)` | | text on dark chapters |

#### Default (paper) semantic tokens

| Token | Value | Role |
|---|---|---|
| `--background` | `var(--paper)` | page background |
| `--foreground` / `--ink` | `var(--ink-raw)` | body text |
| `--surface` | `var(--paper-2)` | elevated surface |
| `--surface-2` | `var(--ash)` | dark surface / accent block |
| `--card` | `color-mix(in oklch, var(--paper) 92%, var(--ink-raw) 8%)` | card background |
| `--card-foreground` | `var(--ink-raw)` | card text |
| `--popover` | `var(--paper)` | popover background |
| `--primary` | `var(--ink-raw)` | primary button / link |
| `--primary-foreground` | `var(--paper)` | text on primary |
| `--secondary` | `var(--paper-2)` | secondary background |
| `--muted` | `var(--paper-2)` | muted background |
| `--muted-foreground` | `color-mix(in oklch, var(--ink-raw) 72%, var(--paper) 28%)` | secondary text |
| `--accent` | `var(--amber)` | accent color |
| `--accent-foreground` | `var(--ink-raw)` | text on accent |
| `--destructive` | `oklch(0.55 0.2 27)` | error / destructive |
| `--border` / `--hairline` | `color-mix(in oklch, var(--ink-raw) 28%, var(--paper) 72%)` | borders |
| `--input` | `color-mix(in oklch, var(--ink-raw) 22%, var(--paper) 78%)` | input borders |
| `--ring` | `var(--amber)` | focus rings |
| `--radius` | `0.25rem` (4 px) | corner radius |

#### Dark semantic tokens

The `.dark` class uses a slate-blue base (`oklch(0.129 0.042 264.695)` background, `oklch(0.984 0.003 247.858)` foreground) with the same Tailwind semantic map (`--background`, `--foreground`, `--primary`, `--secondary`, `--muted`, `--accent`, `--destructive`, `--border`, `--input`, `--ring`, etc.).

#### Chapter themes

`styles.css` defines six chapter utilities that override semantic tokens per section. They are applied to section IDs on the home page.

| Utility | Background | Surface 2 | Foreground | Accent | Sections |
|---|---|---|---|---|---|
| `chapter-paper` | `var(--paper)` | `var(--ash)` | `var(--ink-raw)` | `var(--amber)` | `#top`, `#pipeline`, `#architecture` |
| `chapter-cream` | paper + gold mix | paper + gold mix | `var(--ink-raw)` | `var(--burgundy)` | `#control`, `#pricing` |
| `chapter-ash` | `var(--ash)` | `var(--ink-raw)` | `var(--cream)` | `var(--gold)` | (fallback for alternate sections) |
| `chapter-ink` | `var(--ink-raw)` | ink + gold mix | `var(--cream)` | `var(--gold)` | `#walkthrough`, `#performance`, `#faq` |
| `chapter-burgundy` | `var(--burgundy)` | `var(--ink-raw)` | `var(--cream)` | `var(--gold)` | `#resume`, `#privacy` |
| `chapter-rust` | ash + rust mix | `var(--ink-raw)` | `var(--cream)` | `var(--gold)` | `#requirements` |

Selection is tinted with the current accent: `background: color-mix(in oklch, var(--accent) 40%, transparent)`.

#### Waveform accents

SVG waveform graphics on the landing page use these `oklch()` values (they predate or coexist with the CSS chapter system):

| Token | oklch | Suggested role |
|---|---|---|
| `--wave-muted` | `oklch(0.48 0.02 245)` | quiet / background waveform |
| `--wave-base` | `oklch(0.55 0.03 240)` | dark waveform base |
| `--accent-blue` | `oklch(0.72 0.15 258)` | primary accent |
| `--accent-amber` | `oklch(0.72 0.15 50)` | secondary / warning accent |
| `--accent-gold` | `oklch(0.78 0.17 62)` | highlight / active accent |

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`, 2026-07-26.

### Application palette (Avalonia)

The `DubBench` Avalonia app ships two theme dictionaries. These are the canonical application color tokens.

Dark theme (`src/DubBench/Resources/DubBenchTheme.axaml`):

| Brush | Hex | Usage |
|---|---|---|
| `ThemeBackgroundBrush` | `#1A1A1A` | window / app background |
| `ThemeSurfaceBrush` | `#2D2D2D` | elevated surfaces |
| `ThemeAccentBrush` | `#00BFA5` | primary accent |
| `ThemeTextPrimaryBrush` | `#E8E8E8` | body text |
| `ThemeTextSecondaryBrush` | `#9E9E9E` | muted text |
| `ThemeBorderBrush` / `BorderBrush` | `#3D3D3D` | borders |
| `ThemeErrorBrush` | `#FF5252` | errors |
| `TabActiveBrush` | `#00BFA5` | active tab |
| `TabInactiveBrush` | `#6B6B6B` | inactive tab |
| `ThemeForegroundBrush` | `#E8E8E8` | foreground |
| `SidebarBackgroundBrush` | `#1E1E1E` | sidebar background |
| `ContentBackgroundBrush` | `#252525` | content area |
| `ButtonPrimaryBackground` | `#00BFA5` | primary button |
| `ButtonPrimaryText` | `#1A1A1A` | primary button text |
| `SuccessGreenBrush` | `#4CAF50` | success |
| `WarningOrangeBrush` | `#FF9800` | warning |

Light theme (`src/DubBench/Resources/DubBenchThemeLight.axaml`) maps the same keys to lighter equivalents; the accent becomes `#009688` and backgrounds flip to off-white / white.

Source: `src/DubBench/Resources/DubBenchTheme.axaml`, `src/DubBench/Resources/DubBenchThemeLight.axaml`.

### Hardcoded UI accents

A few views use colors that are not yet in the theme dictionaries:

| Color | Location | Usage |
|---|---|---|
| `#3A5535` | `LeaderboardTabView.axaml` | disclaimer banner background |
| `#2D5A27` | `LeaderboardTabView.axaml` | score badge background |
| `#CCFFCC` | `LeaderboardTabView.axaml` | score badge foreground |

These should be migrated to theme brushes if they become part of the shipping UI.

Source: `src/DubBench/Views/LeaderboardTabView.axaml`.

### Terminal UI colors

The CLI TUI uses Spectre.Console's default named colors:

- `Color.Blue`: main header border (`Trackdub`)
- `Color.Grey`: footer and placeholder panels
- `Color.Cyan1`: inline picker border

These are not currently tied to the Avalonia theme.

Source: `src/Trackdub.Cli/Tui/TrackdubTuiApp.cs`, `src/Trackdub.Cli/Tui/TuiInlinePicker.cs`.

### Palette migration (canonical = marketing)

**Canonical brand accents** come from trackdub.com: chapter-dependent `var(--amber)` / `var(--gold)`, with `var(--burgundy)` primary buttons on paper/cream chapters.

The Avalonia / DubBench accent teal (`#00BFA5` / `#009688`) is **legacy migration debt**. New UI (web, portal, Figma, and eventual app shell theming) must not introduce teal as a brand accent. Existing Avalonia theme brushes remain until an explicit theme migration lands; treat them as temporary, not as the design system source of truth.

## 4. Typography

### Marketing site

The site self-hosts three font families in `public/fonts/`:

| Family | Weights | Usage |
|---|---|---|
| **IBM Plex Sans** | 400, 500, 600, 700 (plus 400 italic) | body, headings, UI labels |
| **Instrument Serif** | 400 (plus 400 italic) | display headings, logo wordmark, numerals |
| **JetBrains Mono** | 400, 500 | code, timecode, captions, data |

The two display weights are preloaded in `__root.tsx`:

- `/fonts/instrument-serif-400.woff2`
- `/fonts/ibm-plex-sans-400.woff2`

CSS custom properties in `styles.css`:

```css
--font-sans: "IBM Plex Sans", ui-sans-serif, system-ui, -apple-system, sans-serif;
--font-serif: "Instrument Serif", "IBM Plex Serif", Georgia, ui-serif, serif;
--font-mono: "JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace;
```

`body` sets:

- `font-family: var(--font-sans)`
- `font-feature-settings: "ss01"`
- `-webkit-font-smoothing: antialiased`
- `text-rendering: optimizeLegibility`

Custom `@utility` helpers:

- `.font-serif`: family, `font-weight: 400`, `letter-spacing: -0.01em`, `font-feature-settings: normal`
- `.font-mono`: family only

#### Type scale (from `index.tsx`)

| Element | Classes | Notes |
|---|---|---|
| H1 (hero) | `font-serif text-5xl leading-[0.98] tracking-tight sm:text-6xl lg:text-[68px] xl:text-[76px]` | max-width container |
| H2 (section) | `font-serif text-4xl leading-[1.05] tracking-tight sm:text-5xl` | section headings |
| H3 | `font-serif text-3xl leading-[1.1] tracking-tight sm:text-4xl` | stage sub-headings |
| Logo wordmark | `font-serif text-2xl leading-none tracking-tight sm:text-[38px]` | masthead |
| Section number | `font-mono text-2xl leading-none tracking-tight text-accent sm:text-3xl` | chapter index |
| Section label | `font-serif text-[20px] leading-tight tracking-tight sm:text-[24px]` | chapter title |
| Body | `text-[17px] leading-relaxed` or `text-lg leading-relaxed` | paragraphs |
| Small body | `text-[16px] leading-relaxed`, `text-[15px]`, `text-[14px]` | cards, captions |
| Micro label | `font-mono text-[10px] uppercase tracking-[0.18em]` / `[0.22em]` / `[0.14em]` | chapter tags, metadata |
| Mono data | `font-mono text-[11px]`, `text-[12px]`, `text-[13px]` | job logs, UI lists |
| Price | `font-serif text-5xl tracking-tight` | pricing cards |

`tabular-nums` is used for timecode and numeric data.

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`, `trackdub.com/src/routes/__root.tsx`, `trackdub.com/public/fonts/`, 2026-07-26.

### Application

The Avalonia theme does not set a custom `FontFamily`; it relies on the system font. The theme defines the following type scale in `DubBenchTheme.axaml` and `DubBenchThemeLight.axaml`:

| Token | Size | Typical use |
|---|---|---|
| `FontSizeMicro` | 10 | micro labels |
| `CaptionFontSize` | 12 | captions |
| `MonospaceFontSize` | 13 | monospace / data |
| `BodyFontSize` | 14 | body text |
| `HeadingFontSize` | 18 | section headings |
| `FontSizeTitle` | 20 | page title |

In views, headings use `FontSize="18"` and `FontWeight="SemiBold"`.

Source: `src/DubBench/Resources/DubBenchTheme.axaml`, `src/DubBench/Views/*.axaml`.

## 5. Spacing & Sizing

### Marketing site

| Token / Pattern | Value | Usage |
|---|---|---|
| Section max-width | `max-w-[1600px]` | hero, full-bleed sections |
| Content max-width | `max-w-6xl` | `Container` component |
| Page padding | `px-6 sm:px-10` | horizontal page gutters |
| Section padding | `py-12 sm:py-9` (hero), `py-20 sm:py-28` (content) | vertical rhythm |
| Masthead height | `h-16 sm:h-[88px]` | site header |
| Masthead gap | `gap-6` | nav / actions spacing |
| Primary nav gap | `gap-x-7` | desktop nav links |
| Hero grid | `lg:grid-cols-[minmax(0,0.78fr)_minmax(0,1.22fr)]` | text + waveform |
| Content grid | `lg:grid-cols-12` with `lg:gap-16` | two-column sections |
| Content gap | `gap-10`, `gap-12`, `gap-14` | between text and media |
| Card padding | `p-8` | pricing cards |
| Button padding | `px-6 py-3` | primary CTA |
| Corner radius | `--radius: 0.25rem` (4 px) | cards, buttons, inputs |
| Focus ring | `ring-2 ring-accent ring-offset-2 ring-offset-background` | focus-visible state |

### Application

Common values from the Avalonia views:

| Token | Value | Usage |
|---|---|---|
| Small padding | 6, 8, 10, 12 | cards, list items, buttons |
| Section margin | `0,0,0,16` | bottom margin on scroll panels |
| Separator margin | `0,4` | horizontal separators |
| Corner radius | 4, 6 | cards, badges, banners |
| Border thickness | 1 | cards, sidebars |
| Button heights | 28, 32, 36 | secondary / primary actions |
| Button widths | 100, 120, 160, 200 | fixed-width action buttons |
| Sidebar width | 50 px | `BenchmarkWindow` left rail |
| ContentControl margin | 16 | main content area |
| Window size | 960 x 720 | `BenchmarkWindow` default |
| Window min size | 640 x 480 | `BenchmarkWindow` minimum |

Source: `trackdub.com/src/routes/index.tsx`, `trackdub.com/src/styles.css`, `src/DubBench/Views/BenchmarkWindow.axaml`, `src/DubBench/Views/*.axaml`.

## 6. Layout

### Application shell

- A 50 px left sidebar holds tab buttons (emoji icons, 36 x 36).
- The main content area is a `ContentControl` with 16 px margin.
- Each tab is a `ScrollViewer` wrapping a vertical `StackPanel` with `Spacing="12"`.
- Common vertical rhythm: heading (18, SemiBold), description text (Opacity 0.7), separator, input groups (`Spacing="4"`), action button, result card.

Source: `src/DubBench/Views/BenchmarkWindow.axaml`, `src/DubBench/Views/AudioPrepTabView.axaml`, etc.

### Marketing site layout

The landing page is a long-scrolling page with numbered chapters:

1. Hero + tagline
2. Pipeline (six editable stages)
3. Try it / interactive transcript demo
4. Resume / checkpoint explanation
5. Control / invalidation
6. Performance / hardware lanes
7. Local-first / data ownership
8. Manifest
9. Pricing (Personal / Pro / Teams)
10. FAQ
11. Waitlist / launch list

**Global components:**

- `Container`: `mx-auto w-full max-w-6xl px-6 sm:px-10`
- `SectionNumber`: chapter index, label, and underline tick
- `Rule`: `h-px w-full bg-border` horizontal separator
- `Masthead`: `border-b border-border bg-background`, `max-w-[1600px]`, `h-16 sm:h-[88px]`
- `SectionRail`: kinetic scroll progress rail on the left side (desktop)
- `Colophon`: 4-column footer with product, developers, company, and status links

**Grid conventions:**

- Hero: two-column `minmax` text / waveform grid.
- Content sections: `grid-cols-1 lg:grid-cols-12` with text in `lg:col-span-4` and content/media in `lg:col-span-8`.
- Pricing: `md:grid-cols-3` with `divide-x divide-border`.
- FAQ: `md:grid-cols-[220px_1fr]` for question / answer pairs.

Source: `trackdub.com/src/routes/index.tsx`, 2026-07-26.

## 7. Components

### Marketing site components

**`InkButton`**

- Base: `btn-sheen inline-flex items-center gap-2 px-6 py-3 text-[14px] font-medium` with sheen sweep on hover/focus.
- Primary: `bg-[var(--burgundy)] text-[var(--cream)] border-[var(--burgundy)] hover:bg-[var(--rust)]`.
- Ghost: `text-[var(--burgundy)] border-[var(--rust)] hover:bg-[var(--rust)] hover:text-[var(--cream)]`.

**`TextLink`**

- `inline-flex items-baseline gap-1 border-b border-foreground/30 pb-0.5 text-foreground hover:border-accent hover:text-accent`

**`MotionToggle`**

- Stores motion preference in `localStorage` under `trackdub:motion`.
- Respects `prefers-reduced-motion`.
- Adds `html.reduce-motion` class, which disables animations site-wide.

**`WaitlistForm`**

- Email input with client-side Zod validation.
- Cloudflare Turnstile rendered explicitly to avoid SSR race conditions.
- Posts to `/api/waitlist`, with cross-origin fallback to `https://trackdub.com` for `trackdub.dev`.

**Utility classes:**

- `.btn-sheen`: hover lift `translateY(-1px)`, sheen sweep animation `900ms cubic-bezier(0.22, 1, 0.36, 1)`.
- `.card-lift`: hover `translateY(-2px)`, top `border-top-color` transitions to `--accent`.
- `.shadow-panel`: `0 1px 0 oklch(1 0 0 / 0.03) inset, 0 24px 60px -30px oklch(0 0 0 / 0.45)`.
- `.hairline`: `border-color: var(--hairline)`.

### Avalonia components

#### Buttons

- Primary action: fixed width/height, `ButtonPrimaryBackground` / `ButtonPrimaryText`.
- Icon tab buttons: transparent background, `Foreground` bound to `TabActiveBrush` / `TabInactiveBrush`.

#### Cards

- Border: `BorderBrush`, thickness 1, `CornerRadius="6"`.
- Padding: 12.
- Background defaults to `ThemeSurfaceBrush` / `ContentBackgroundBrush`.
- Result/status cards use `Spacing="4"` for stacked labels.

#### Lists

- List items: `CornerRadius="4"`, `Padding="10"`, bottom `Margin="0,0,0,4"`.
- Background: `SidebarBackgroundBrush`.

#### Badges

- Score badge: background `#2D5A27`, foreground `#CCFFCC`, `CornerRadius="4"`, `Padding="8,4"`.

#### Banners

- Disclaimer banner: background `#3A5535`, `CornerRadius="6"`, `Padding="12"`.

#### Separators

- `Separator` with `Background="{DynamicResource BorderBrush}"` and `Margin="0,4"`.

#### Form groups

- `StackPanel Spacing="4"` with a `TextBlock FontWeight="SemiBold"` label and an input control (`TextBox`, `ComboBox`, `NumericUpDown`).

#### CLI panels

- `Panel` with `BorderColor` from Spectre.Console (`Blue`, `Grey`, `Cyan1`), 1 padding unit.

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`, `src/DubBench/Views/*.axaml`, `src/Trackdub.Cli/Tui/*.cs`.

## 8. Icons

- The Avalonia app uses emoji strings stored in `BenchmarkIcons.axaml`:
  - `OnnxModelIcon`: 🔬
  - `AudioPrepIcon`: 🎤
  - `DubbingIcon`: 🎬
  - `PresetsIcon`: ⚙️
  - `LeaderboardIcon`: 🏆
- The marketing site uses **Lucide** icons (`lucide-react`) and inline SVG waveforms.

Source: `src/DubBench/Resources/BenchmarkIcons.axaml`, `trackdub.com/src/routes/index.tsx`, `trackdub.com/package.json`.

## 9. Animation & Motion

### Marketing site

`styles.css` defines a focused, reduced-motion-aware kinetic layer.

| Animation | Duration / Easing | Usage |
|---|---|---|
| `fade-up` | `0.6s ease-out` | generic fade-up entrance |
| `reveal` | `320ms cubic-bezier(0.22, 1, 0.36, 1)` | section entrance |
| `reveal-child` | `300ms` same easing, delay `calc(40ms + var(--reveal-i, 0) * 30ms)` | staggered children |
| `wave-drift` | `3.6s ease-in-out infinite` | waveform bars |
| `ticker-scroll` | `38s linear infinite` | trust ticker tape |
| `stamp-thunk` | `460ms cubic-bezier(0.34, 1.2, 0.5, 1) 480ms backwards` | rubber-stamp entrance |
| `playhead-sweep` | `14s linear infinite` | rail playhead |
| Button sheen | `900ms cubic-bezier(0.22, 1, 0.36, 1)` | hover/focus sweep |
| Card lift | `260ms cubic-bezier(0.22, 1, 0.36, 1)` | hover translateY |
| Chapter h2 intro | `520ms` | per-palette heading flourish |
| Chapter children | `320-460ms` | `snap-in`, `margin-slide`, `tilt-in`, `drop-settle`, `wobble-in`, `ink-wipe`, `letter-rise` |

**Accessibility:**

- Respects `@media (prefers-reduced-motion: reduce)`.
- `html.reduce-motion` class forces `animation-duration` and `transition-duration` to `0.01ms`, disables `ticker-track`, `wave-bar`, and sheen.

Source: `trackdub.com/src/styles.css`, `trackdub.com/src/routes/index.tsx`.

## 10. Themes

### Website

- Default `:root` is the warm-cream "Autumn Harvest" light theme.
- `.dark` enables a slate-blue dark theme.
- Sections override the default with `chapter-paper`, `chapter-cream`, `chapter-ash`, `chapter-ink`, `chapter-burgundy`, `chapter-rust` utilities assigned by section ID.
- `shadcn/ui` base color is `slate` (`components.json`).

### Application

The application supports four persisted theme identifiers (`Trackdub.Contracts.AppThemeNames`):

- `dark`
- `light`
- `amber`
- `green`

Only `Dark` and `Light` resource dictionaries are currently present in the repository:

- `src/DubBench/Resources/DubBenchTheme.axaml` (Dark)
- `src/DubBench/Resources/DubBenchThemeLight.axaml` (Light)

`App.axaml` sets `RequestedThemeVariant="Dark"` and merges the icon dictionary plus the theme dictionaries via `ResourceDictionary.ThemeDictionaries`. It also applies the `FluentTheme` base style.

Source: `src/DubBench/App.axaml`, `src/Trackdub.Contracts/IStudioSettingsService.cs`, `trackdub.com/src/styles.css`, `trackdub.com/components.json`.

## 11. Implementation Notes

- The `DubBench` project in this repository is a benchmark harness, not the final Trackdub application shell. It does, however, define the current theme tokens and view patterns.
- The main app shell is shown on the marketing site (`app-shell-early-build.png`) but is not present in this public core repository.
- **Canonical palette = trackdub.com.** Avalonia teal is migration debt (see Palette migration above). Figma Design System file `vUAGF65aDO1a83u5COYWBG` is the design review source of truth for Qodo.
- The site uses Tailwind CSS v4 (`@import "tailwindcss"`, `@theme inline`, `@utility`) with `tw-animate-css`, not a traditional `tailwind.config.ts`.
- Custom self-hosted fonts replace Google Fonts to eliminate extra DNS/TLS/CSS round trips before text paints.

## 12. Sources

- `src/DubBench/App.axaml`
- `src/DubBench/Resources/BenchmarkIcons.axaml`
- `src/DubBench/Resources/DubBenchTheme.axaml`
- `src/DubBench/Resources/DubBenchThemeLight.axaml`
- `src/DubBench/Views/AudioPrepTabView.axaml`
- `src/DubBench/Views/BenchmarkWindow.axaml`
- `src/DubBench/Views/DubbingTabView.axaml`
- `src/DubBench/Views/LeaderboardTabView.axaml`
- `src/DubBench/Views/OnnxModelTabView.axaml`
- `src/DubBench/Views/PresetsTabView.axaml`
- `src/Trackdub.Cli/Tui/TrackdubTuiApp.cs`
- `src/Trackdub.Cli/Tui/TuiInlinePicker.cs`
- `src/Trackdub.Contracts/IStudioSettingsService.cs`
- `trackdub.com/components.json`
- `trackdub.com/package.json`
- `trackdub.com/public/fonts/`
- `trackdub.com/src/components/ui/button.tsx`
- `trackdub.com/src/routes/__root.tsx`
- `trackdub.com/src/routes/index.tsx`
- `trackdub.com/src/styles.css`
- `trackdub.com/` landing page, accessed 2026-07-26.

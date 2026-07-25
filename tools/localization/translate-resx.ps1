#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Throwaway DeepL-powered .resx localizer for Trackdub Avalonia UI strings.

.DESCRIPTION
  Reads the English master App.resx, protects placeholders + glossary terms using
  DeepL's XML tag handling, batches requests (≤45 texts), translates to the
  requested target languages with professional formality, strips protection tags,
  and emits correctly structured App.<lang>.resx files that preserve comments,
  headers, attribute order, and xml:space="preserve".

  This is intentionally a one-off / re-runnable tool. It lives under tools/ and
  does not become a permanent dependency.

  Requirements:
    - DeepL Free API key (env:DEEPL_API_KEY or -ApiKey). The key must end with :fx for free.
    - Google Cloud Translation API key (env:GOOGLE_API_KEY or -GoogleApiKey) when DeepL quota
      is exhausted or -Provider Google is used.
    - No key is ever logged or committed.

  Usage (after setting key):
    pwsh -File tools/localization/translate-resx.ps1 -Install

  DeepL quota exceeded — fall back to Google:
    $env:GOOGLE_API_KEY = "your-google-api-key"
    pwsh -File tools/localization/translate-resx.ps1 -Provider Google -Install

  To add more languages later:
    pwsh -File tools/localization/translate-resx.ps1 -Languages "IT,PL,NL" -Install

  Dry-run (no network, verifies parsing/protection/roundtrip):
    pwsh -File tools/localization/translate-resx.ps1 -DryRun -Verbose
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string[]] $Languages = @('ES','PT-BR','FR','DE','JA'),

    [Parameter()]
    [string] $MasterResx = 'src/Trackdub.App.Avalonia/Resources/App.resx',

    [Parameter()]
    [string] $OutputDir = 'tools/localization',

    [Parameter()]
    [string] $ApiKey = $env:DEEPL_API_KEY,

    [Parameter()]
    [string] $GoogleApiKey = $env:GOOGLE_API_KEY,

    [Parameter()]
    [ValidateSet('DeepL', 'Google', 'Auto')]
    [string] $Provider = 'Auto',

    [Parameter()]
    [int] $BatchSize = 20,

    [Parameter()]
    [switch] $Install,          # After successful generation, copy files into the Avalonia Resources folder

    [Parameter()]
    [switch] $DryRun,           # Parse + protect + unprotect roundtrip only. No API calls. Good for script verification.

    [Parameter()]
    [switch] $MockApi           # INTERNAL / for structure verification when no key: returns original text with [LANG] prefix. Never use for real releases.
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Normalize Languages: if passed as a single comma-separated string, split it
if ($Languages.Count -eq 1 -and $Languages[0] -match ',') {
    $Languages = $Languages[0] -split ',' | ForEach-Object { $_.Trim().ToUpper() }
}

# --- Early exit guards (Law of the Early Exit) ---
if (-not (Test-Path -LiteralPath $MasterResx)) {
    throw "Master resx not found at '$MasterResx'. Run from repo root or pass -MasterResx."
}

if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$resolvedOutput = (Resolve-Path -LiteralPath $OutputDir).Path

# --- Load glossary (brand + technical terms that must stay consistent) ---
$glossaryPath = Join-Path $PSScriptRoot 'glossary.md'
$glossary = [ordered]@{}
if (Test-Path -LiteralPath $glossaryPath) {
    Get-Content -LiteralPath $glossaryPath -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ($_ -match '^\s*([^#=]+?)\s*=\s*(.+?)\s*$') {
                $term = $matches[1]
                $replacement = $matches[2]
                if ($term) { $glossary[$term] = $replacement }
            }
        }
}

function Apply-Glossary {
    param([string] $Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $result = $Text
    # Process longer terms first to avoid partial matches
    $sorted = $glossary.Keys | Sort-Object { $_.Length } -Descending
    foreach ($term in $sorted) {
        $repl = $glossary[$term]
        $pattern = '\b' + [regex]::Escape($term) + '\b'
        $result = [regex]::Replace($result, $pattern, $repl, 'IgnoreCase')
    }
    return $result
}

# --- Placeholder protection for DeepL XML handling (critical requirement) ---
function Protect-Text {
    param([string] $Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }

    $t = Apply-Glossary $Text

    # Escape XML-unsafe chars BEFORE wrapping, so strings like "Voices & Speakers"
    # produce valid XML for DeepL's tag_handling=xml parser.
    $t = [System.Security.SecurityElement]::Escape($t)

    # Wrap all {0}, {1}, ... placeholders so DeepL never translates the token itself.
    $t = [regex]::Replace($t, '\{([0-9]+)\}', '<ph>{$1}</ph>')

    return $t
}

function Unprotect-Text {
    param([string] $Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }

    # Strip our protection tags cleanly. DeepL returns the inner content unchanged because of ignore_tags.
    $clean = $Text -replace '</?ph>', ''

    # DeepL may return XML entities; decode before Write-TranslatedResx escapes once for valid RESX.
    return [System.Net.WebUtility]::HtmlDecode($clean)
}

# --- Parse the master resx into ordered parts (comments + data) so we can emit identically ---
function Get-ResxParts {
    param([string] $Path)

    [xml]$doc = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop

    $parts = New-Object System.Collections.Generic.List[object]

    foreach ($node in $doc.root.ChildNodes) {
        if ($node -is [System.Xml.XmlComment]) {
            $parts.Add([pscustomobject]@{
                Kind = 'Comment'
                Text = $node.Value
            })
        }
        elseif ($node.LocalName -eq 'data') {
            $nameAttr = $node.Attributes['name']
            if ($nameAttr) {
                $name = $nameAttr.Value
                $valueNode = $node.SelectSingleNode('value')
                $original = if ($valueNode) { $valueNode.InnerText } else { '' }
                $parts.Add([pscustomobject]@{
                    Kind     = 'Data'
                    Name     = $name
                    Original = $original
                })
            }
        }
        # Ignore xsd:schema and resheader — they are static and emitted via the captured header.
    }

    return $parts
}

# --- Capture the exact static header block (schema + resheaders) from the master ---
# We capture everything from the start of the file through the last </resheader> line.
# This avoids a brittle hardcoded line count while preserving byte-style fidelity for the non-translated part.
$allLines = Get-Content -LiteralPath $MasterResx -ErrorAction Stop
$lastResHeaderIndex = -1
for ($i = 0; $i -lt $allLines.Count; $i++) {
    if ($allLines[$i] -match '</resheader>') {
        $lastResHeaderIndex = $i
    }
}

if ($lastResHeaderIndex -lt 0) {
    throw "Could not locate </resheader> in '$MasterResx'; cannot determine RESX header boundary."
}

$headerLines = $allLines[0..$lastResHeaderIndex]
$ResxHeader = ($headerLines -join [Environment]::NewLine) + [Environment]::NewLine


# --- Google Cloud Translation (fallback when DeepL quota is exhausted) ---
function Get-GoogleTargetLang {
    param([string] $DeepLLang)
    switch ($DeepLLang.ToUpperInvariant()) {
        'PT-BR'   { return 'pt' }
        'ZH-HANS' { return 'zh-CN' }
        default   { return $DeepLLang.ToLowerInvariant() }
    }
}

function Invoke-GoogleBatch {
    param(
        [string[]] $Texts,
        [string] $TargetLang,
        [string] $Key,
        [int] $MaxRetries = 4
    )

    if (-not $Key) {
        throw 'Google API key is required. Set GOOGLE_API_KEY or pass -GoogleApiKey.'
    }

    $googleTarget = Get-GoogleTargetLang -DeepLLang $TargetLang
    $uri = "https://translation.googleapis.com/language/translate/v2?key=$Key"
    $body = @{
        q      = @($Texts)
        source = 'en'
        target = $googleTarget
        format = 'html'
    }

    $headers = @{ 'Content-Type' = 'application/json' }

    for ($attempt = 0; $attempt -lt $MaxRetries; $attempt++) {
        try {
            $json = $body | ConvertTo-Json -Depth 4 -Compress
            $response = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $json -ErrorAction Stop
            return @($response.data.translations | ForEach-Object { $_.translatedText })
        }
        catch {
            $ex = $_.Exception
            $status = $null
            if ($ex.Response -and $ex.Response.StatusCode) {
                $status = [int]$ex.Response.StatusCode
            }

            if ($status -eq 429 -and $attempt -lt ($MaxRetries - 1)) {
                $backoff = [math]::Pow(2, $attempt) * 1.8
                Write-Warning "Google Translate returned 429 (rate limit). Backing off for $([math]::Round($backoff,1))s (attempt $($attempt+1)/$MaxRetries)..."
                Start-Sleep -Milliseconds ([int]($backoff * 1000))
                continue
            }

            $detail = if ($ex.Response) {
                try { $reader = New-Object System.IO.StreamReader($ex.Response.GetResponseStream()); $reader.ReadToEnd() } catch { $ex.Message }
            } else { $ex.Message }
            throw "Google Translate API error (lang=$TargetLang, attempt=$($attempt+1)): $status $detail"
        }
    }

    throw "Google Translate batch failed after $MaxRetries retries for $TargetLang"
}

function Test-DeepLQuotaExceeded {
    param($ErrorRecord)
    $msg = $ErrorRecord.Exception.Message
    return ($msg -match '\b456\b') -or ($msg -match 'Quota exceeded')
}

# --- DeepL call with batching + 429 retry (robust, fail loud) ---
function Invoke-DeepLBatch {
    param(
        [string[]] $Texts,
        [string] $TargetLang,
        [string] $Key,
        [int] $MaxRetries = 4
    )

    if (-not $Key) {
        throw "DeepL API key is required for real translation. Use -DryRun or -MockApi for verification."
    }

    $uri = 'https://api-free.deepl.com/v2/translate'

    $body = @{
        text               = $Texts
        source_lang        = 'EN'
        target_lang        = $TargetLang
        tag_handling       = 'xml'
        ignore_tags        = @('ph')
        preserve_formatting = $true
        formality          = 'prefer_more'   # professional desktop UI tone for the supported languages
    }

    $headers = @{
        'Authorization' = "DeepL-Auth-Key $Key"
        'Content-Type'  = 'application/json'
    }

    for ($attempt = 0; $attempt -lt $MaxRetries; $attempt++) {
        try {
            $json = $body | ConvertTo-Json -Depth 4 -Compress
            $response = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $json -ErrorAction Stop
            # Translations array is guaranteed to be in the same order as the sent "text" array.
            return $response.translations | ForEach-Object { $_.text }
        }
        catch {
            $ex = $_.Exception
            $status = $null
            if ($ex.Response -and $ex.Response.StatusCode) {
                $status = [int]$ex.Response.StatusCode
            }

            if ($status -eq 429 -and $attempt -lt ($MaxRetries - 1)) {
                $backoff = [math]::Pow(2, $attempt) * 1.8   # ~2s, 3.6s, 7s ...
                Write-Warning "DeepL returned 429 (rate limit). Backing off for $([math]::Round($backoff,1))s (attempt $($attempt+1)/$MaxRetries)..."
                Start-Sleep -Milliseconds ([int]($backoff * 1000))
                continue
            }

            # Fail loud with useful context (Law of Fail Fast)
            $detail = if ($ex.Response) {
                try { $reader = New-Object System.IO.StreamReader($ex.Response.GetResponseStream()); $reader.ReadToEnd() } catch { $ex.Message }
            } else { $ex.Message }
            throw "DeepL API error (lang=$TargetLang, attempt=$($attempt+1)): $status $detail"
        }
    }

    throw "DeepL batch failed after $MaxRetries retries for $TargetLang"
}

# --- Main translation pipeline for one language ---
function Translate-Language {
    param(
        [string] $TargetLang,
        [object[]] $Parts,
        [string] $Key,
        [string] $GoogleKey,
        [string] $ProviderName,
        [int] $BatchSize,
        [bool] $IsDryRun,
        [bool] $IsMock,
        [ref] $ActiveProvider
    )

    $dataItems = @($Parts | Where-Object { $_.Kind -eq 'Data' })
    if ($dataItems.Count -eq 0) {
        throw "No <data> entries found in master. Aborting."
    }

    Write-Verbose "[$TargetLang] Protecting + batching $($dataItems.Count) strings..."

    # Protect every data value (glossary + ph tags). Keep originals for char counting.
    $protected = @(foreach ($d in $dataItems) { Protect-Text -Text $d.Original })
    $originalChars = ($dataItems | ForEach-Object { $_.Original.Length } | Measure-Object -Sum).Sum

    if ($IsDryRun) {
        # Round-trip verification only
        $unprotected = @(foreach ($p in $protected) { Unprotect-Text -Text $p })
        # Basic sanity: placeholders that existed must still exist after roundtrip
        for ($i = 0; $i -lt $dataItems.Count; $i++) {
            $orig = $dataItems[$i].Original
            $back = $unprotected[$i]
            if ($orig -match '\{[0-9]+\}' -and $back -notmatch '\{[0-9]+\}') {
                Write-Warning "[$TargetLang] Placeholder lost in dry-run roundtrip for $($dataItems[$i].Name)"
            }
        }
        return @{
            TargetLang     = $TargetLang
            TranslatedData = $unprotected
            CharsSent      = $originalChars
            RequestCount   = 0
            WasMock        = $false
            WasDry         = $true
        }
    }

    if ($IsMock) {
        # Structure-only mock (honest: not real translation quality)
        $mockTranslated = @(foreach ($orig in $dataItems.Original) {
            # Keep placeholders and glossary intact even in mock
            $p = Protect-Text -Text $orig
            $u = Unprotect-Text -Text $p
            "[$TargetLang] $u"
        })
        return @{
            TargetLang     = $TargetLang
            TranslatedData = $mockTranslated
            CharsSent      = $originalChars
            RequestCount   = 0
            WasMock        = $true
            WasDry         = $false
        }
    }

    $providerInUse = $ActiveProvider.Value
    if ($ProviderName -eq 'Google') { $providerInUse = 'Google' }
    elseif ($ProviderName -eq 'DeepL') { $providerInUse = 'DeepL' }
    elseif (-not $Key -and $GoogleKey) { $providerInUse = 'Google' }
    elseif ($Key) { $providerInUse = 'DeepL' }
    else {
        throw 'No translation API key configured. Set DEEPL_API_KEY and/or GOOGLE_API_KEY.'
    }

    # --- Real translation path (batched) ---
    $allTranslated = New-Object System.Collections.Generic.List[string]
    $requestCount = 0
    $batches = for ($i = 0; $i -lt $protected.Count; $i += $BatchSize) {
        ,@($protected[$i..([math]::Min($i + $BatchSize - 1, $protected.Count - 1))])
    }

    foreach ($batch in $batches) {
        $requestCount++
        $batch = @($batch)  # ensure array even for single-item batches
        Write-Verbose "[$TargetLang] Sending batch $requestCount ($($batch.Count) texts) via $providerInUse..."
        try {
            $translatedBatch = if ($providerInUse -eq 'Google') {
                Invoke-GoogleBatch -Texts $batch -TargetLang $TargetLang -Key $GoogleKey
            } else {
                Invoke-DeepLBatch -Texts $batch -TargetLang $TargetLang -Key $Key
            }
        }
        catch {
            if ($providerInUse -eq 'DeepL' -and $ProviderName -eq 'Auto' -and $GoogleKey -and (Test-DeepLQuotaExceeded $_)) {
                Write-Warning "[$TargetLang] DeepL quota exceeded (456). Falling back to Google Translate for remaining batches."
                $providerInUse = 'Google'
                $ActiveProvider.Value = 'Google'
                $translatedBatch = Invoke-GoogleBatch -Texts $batch -TargetLang $TargetLang -Key $GoogleKey
            }
            else {
                throw
            }
        }
        foreach ($t in $translatedBatch) {
            $allTranslated.Add( (Unprotect-Text -Text $t) )
        }
    }

    # Final guard: count must match
    if ($allTranslated.Count -ne $dataItems.Count) {
        throw "Translation count mismatch for $TargetLang (sent $($dataItems.Count), got $($allTranslated.Count))."
    }

    # Verify placeholders survived real translation for the two strings that use them
    for ($i = 0; $i -lt $dataItems.Count; $i++) {
        $orig = $dataItems[$i].Original
        $back = $allTranslated[$i]
        if ($orig -match '\{[0-9]+\}' -and $back -notmatch '\{[0-9]+\}') {
            Write-Warning "[$TargetLang] $providerInUse response lost placeholder for key '$($dataItems[$i].Name)'. Value was: $back"
        }
    }

    return @{
        TargetLang     = $TargetLang
        TranslatedData = $allTranslated
        CharsSent      = $originalChars
        RequestCount   = $requestCount
        ProviderUsed   = $providerInUse
        WasMock        = $false
        WasDry         = $false
    }
}

# --- Emit a single locale .resx preserving exact structure + comments ---
function Write-TranslatedResx {
    param(
        [string] $TargetLang,
        [object[]] $Parts,
        [string[]] $TranslatedValues,
        [string] $OutPath
    )

    $dataIndex = 0
    $nodeText = New-Object System.Text.StringBuilder

    foreach ($part in $Parts) {
        if ($part.Kind -eq 'Comment') {
            [void]$nodeText.AppendLine("  <!-- $($part.Text) -->")
        }
        elseif ($part.Kind -eq 'Data') {
            $val = $TranslatedValues[$dataIndex]
            # Escape for XML element content ( & < > " ' )
            $escaped = [System.Security.SecurityElement]::Escape($val)
            [void]$nodeText.AppendLine(('  <data name="{0}" xml:space="preserve"><value>{1}</value></data>' -f $part.Name, $escaped))
            $dataIndex++
        }
    }

    $full = $ResxHeader + $nodeText.ToString() + "</root>"

    # UTF-8 without BOM to match existing resx files in the tree
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($OutPath, $full, $utf8NoBom)

    return $OutPath
}

# --- Entry point ---
Write-Host "Trackdub resx translator (DeepL + Google fallback)" -ForegroundColor Cyan
Write-Host "Master: $MasterResx"
Write-Host "Targets: $($Languages -join ', ')"
Write-Host "Output:  $resolvedOutput"
if ($DryRun) { Write-Host "Mode:    DRY-RUN (no network)" -ForegroundColor Yellow }
elseif ($MockApi) { Write-Host "Mode:    MOCK (structure verification only — not real translations)" -ForegroundColor Yellow }
elseif ($Provider -eq 'Google') { Write-Host "Mode:    LIVE Google Translate (html format + ph tag protection)" -ForegroundColor Green }
elseif ($Provider -eq 'DeepL') { Write-Host "Mode:    LIVE DeepL (formality=prefer_more, xml tag protection)" -ForegroundColor Green }
else { Write-Host "Mode:    LIVE Auto (DeepL first, Google on quota exceeded)" -ForegroundColor Green }

$parts = @(Get-ResxParts -Path $MasterResx)
$dataCount = (@($parts | Where-Object Kind -eq 'Data')).Count
$commentCount = (@($parts | Where-Object Kind -eq 'Comment')).Count
Write-Host "Parsed:  $dataCount strings, $commentCount comment blocks (order preserved)"

if (-not $DryRun -and -not $MockApi) {
    $needsDeepL = $Provider -eq 'DeepL' -or ($Provider -eq 'Auto' -and -not $GoogleApiKey)
    $needsGoogle = $Provider -eq 'Google' -or ($Provider -eq 'Auto' -and -not $ApiKey)

    if ($needsDeepL -and -not $ApiKey) {
        Write-Host ""
        Write-Host "DEEPL_API_KEY is not set (or -ApiKey not supplied)." -ForegroundColor Red
        Write-Host "Set the environment variable to your DeepL Free key (the value ending in :fx):" -ForegroundColor Yellow
        Write-Host '    $env:DEEPL_API_KEY = "your-key-here:fx"'
        Write-Host "Or use Google when DeepL quota is maxed:" -ForegroundColor Yellow
        Write-Host '    $env:GOOGLE_API_KEY = "your-google-api-key"'
        Write-Host '    pwsh -File tools/localization/translate-resx.ps1 -Provider Google -Install'
        Write-Host ""
        exit 1
    }

    if ($needsGoogle -and -not $GoogleApiKey) {
        Write-Host ""
        Write-Host "GOOGLE_API_KEY is not set (or -GoogleApiKey not supplied)." -ForegroundColor Red
        Write-Host "Set the environment variable to your Google Cloud Translation API key:" -ForegroundColor Yellow
        Write-Host '    $env:GOOGLE_API_KEY = "your-google-api-key"'
        Write-Host "Then re-run with -Provider Google (or -Provider Auto with both keys for fallback)."
        Write-Host ""
        exit 1
    }
}

if ($ApiKey -and $ApiKey.Length -lt 8) {
    throw "ApiKey looks too short. Provide a real DeepL key via env or -ApiKey."
}

if ($GoogleApiKey -and $GoogleApiKey.Length -lt 8) {
    throw "GoogleApiKey looks too short. Provide a real Google key via env or -GoogleApiKey."
}

$script:ActiveProvider = if ($Provider -eq 'Google') { 'Google' } elseif ($Provider -eq 'DeepL') { 'DeepL' } else { if ($ApiKey) { 'DeepL' } else { 'Google' } }

$results = @()
$totalChars = 0
$totalRequests = 0
$warnings = @()

foreach ($lang in $Languages) {
    $result = Translate-Language -TargetLang $lang `
                                 -Parts $parts `
                                 -Key $ApiKey `
                                 -GoogleKey $GoogleApiKey `
                                 -ProviderName $Provider `
                                 -BatchSize $BatchSize `
                                 -IsDryRun $DryRun.IsPresent `
                                 -IsMock $MockApi.IsPresent `
                                 -ActiveProvider ([ref]$script:ActiveProvider)

    $suffix = $lang.ToLowerInvariant()
    if ($lang -eq 'PT-BR') { $suffix = 'pt-BR' }
    if ($lang -eq 'ES') { $suffix = 'es' }  # explicit for clarity

    $fileName = "App.$suffix.resx"
    $outFile = Join-Path $resolvedOutput $fileName

    $written = Write-TranslatedResx -TargetLang $lang `
                                    -Parts $parts `
                                    -TranslatedValues $result.TranslatedData `
                                    -OutPath $outFile

    $results += [pscustomobject]@{
        Language   = $lang
        File       = $written
        Chars      = $result.CharsSent
        Requests   = $result.RequestCount
        Mock       = $result.WasMock
        Dry        = $result.WasDry
    }

    $totalChars += $result.CharsSent
    $totalRequests += $result.RequestCount

    $modeNote = if ($result.WasDry) { ' (dry-run roundtrip)' } elseif ($result.WasMock) { ' (MOCK — not real DeepL)' } else { '' }
    Write-Host "  ✓ $lang → $fileName  ($($result.CharsSent) chars, $($result.RequestCount) reqs)$modeNote" -ForegroundColor Green
}

# --- Summary (required) ---
Write-Host ""
Write-Host "=== Translation summary ===" -ForegroundColor Cyan
Write-Host "Languages processed : $($results.Language -join ', ')"
Write-Host "Total source chars  : $totalChars"
Write-Host "Total API requests  : $totalRequests"
if ($DryRun) {
    Write-Host "Mode                : DRY-RUN (no DeepL calls, protection roundtrips verified)" -ForegroundColor Yellow
}
elseif ($MockApi) {
    Write-Host "Mode                : MOCK API (structure + placeholder preservation only)" -ForegroundColor Yellow
    Write-Host "Note                : Re-run with a real DEEPL_API_KEY (no -MockApi) to obtain quality translations." -ForegroundColor Yellow
}
else {
    Write-Host "Provider            : $script:ActiveProvider (requested: $Provider)"
    if ($script:ActiveProvider -eq 'DeepL') {
        Write-Host "Formality           : prefer_more (all targets)"
        Write-Host "Protection          : <ph> wrappers + glossary + tag_handling=xml + ignore_tags=ph"
    }
    else {
        Write-Host "Protection          : <ph> wrappers + glossary + Google html format"
    }
}

$generated = $results.File
Write-Host "Files written       :"
$generated | ForEach-Object { Write-Host "  $_" }

if ($warnings.Count -gt 0) {
    Write-Host "Warnings:" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "  - $_" }
}

# --- Optional install step (copy into the real Resources folder for satellite assembly use) ---
if ($Install) {
    $destDir = 'src/Trackdub.App.Avalonia/Resources'
    if (-not (Test-Path -LiteralPath $destDir)) {
        throw "Destination Resources folder not found at '$destDir'."
    }
    $dest = (Resolve-Path -LiteralPath $destDir).Path
    foreach ($f in $generated) {
        Copy-Item -LiteralPath $f -Destination $dest -Force
    }
    Write-Host ""
    Write-Host "Installed (copied) to: $dest" -ForegroundColor Green
    Write-Host "The Avalonia project will now see the new satellite .resx files on next build."
}

Write-Host ""
Write-Host "Done. Use DEEPL_API_KEY (default) or GOOGLE_API_KEY with -Provider Google when DeepL quota is maxed." -ForegroundColor Cyan

# Return a small object for callers / CI if they want to capture output
return [pscustomobject]@{
    Languages     = $results.Language
    TotalChars    = $totalChars
    TotalRequests = $totalRequests
    Files         = $generated
    Installed     = $Install.IsPresent
}

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RawArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$agentCommands = @('codex', 'claude', 'devin', 'opencode', 'pi', 'mistral', 'dirac', 'autohand', 'agent')
$controlCommands = @('done', 'status', 'sync', 'check', 'verify', 'publish', 'run-avalonia')
$validCommands = $agentCommands + $controlCommands

if ($RawArgs.Count -eq 0) {
    throw "Usage: .\bs.ps1 <agent> <name> | $($controlCommands -join ' | ')  (agents: $($agentCommands -join ', '))"
}

$Command = $RawArgs[0].ToLowerInvariant()
if ($Command -notin $validCommands) {
    throw "Unknown command '$Command'. Valid agents: $($agentCommands -join ', '); valid commands: $($controlCommands -join ', ')"
}

$remainingArgs = @()
if ($RawArgs.Count -gt 1) {
    $remainingArgs = @($RawArgs[1..($RawArgs.Count - 1)])
}

$Name = ''
if ($remainingArgs.Count -gt 0 -and -not $remainingArgs[0].StartsWith('-')) {
    $Name = $remainingArgs[0]
    if ($remainingArgs.Count -gt 1) {
        $remainingArgs = @($remainingArgs[1..($remainingArgs.Count - 1)])
    }
    else {
        $remainingArgs = @()
    }
}

$Fast = $remainingArgs -contains 'fast'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$flow = Join-Path $repoRoot 'tools\agent-git-flow.ps1'

if (-not (Test-Path -LiteralPath $flow)) {
    throw "Missing helper: $flow"
}

function Invoke-Flow {
    & $flow @args
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

function Assert-Name {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw "Give this work a short name, for example: .\bs.ps1 $Command settings-ui"
    }
}

if ($agentCommands -contains $Command) {
    Assert-Name
    Invoke-Flow new -Agent $Command -Name $Name -AllowDirty
    exit
}

switch ($Command) {
    'done' {
        $flowArgs = @('finish')
        if ($Fast) {
            $flowArgs += '-NoTests'
        }

        Invoke-Flow @flowArgs
    }

    'status' {
        Invoke-Flow status
    }

    'sync' {
        Invoke-Flow sync-main
    }

    'check' {
        Invoke-Flow verify -NoTests
    }

    'verify' {
        Invoke-Flow verify
    }

    'run-avalonia' {
        & (Join-Path $repoRoot 'scripts\run-avalonia.ps1') @remainingArgs
    }

    'publish' {
        $flowArgs = @('publish-main')
        if ($Fast) {
            $flowArgs += '-NoTests'
        }

        Invoke-Flow @flowArgs
    }
}

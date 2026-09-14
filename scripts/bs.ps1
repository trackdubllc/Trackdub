param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RawArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$agentCommands = @('codex', 'claude', 'devin', 'opencode', 'pi', 'mistral', 'dirac', 'autohand', 'agent')
$controlCommands = @('done', 'status', 'sync', 'check', 'verify', 'publish')
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

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptsDir
$worktreesDir = Join-Path $repoRoot '.worktrees'

function Assert-Name {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw "Give this work a short name, for example: .\bs.ps1 $Command settings-ui"
    }
}

function Invoke-Dotnet {
    param([string[]]$Args)
    & dotnet @Args -m:1
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Invoke-Verify {
    param([switch]$NoTests)
    Set-Location $repoRoot
    Write-Host "Restoring..."
    Invoke-Dotnet @('restore', 'Trackdub.slnx')
    Write-Host "Building..."
    Invoke-Dotnet @('build', 'Trackdub.slnx', '--no-restore')
    if (-not $NoTests) {
        Write-Host "Testing..."
        Invoke-Dotnet @('test', 'Trackdub.slnx', '--no-build')
    }
    Write-Host "Verify passed."
}

if ($agentCommands -contains $Command) {
    Assert-Name
    $branch = "agent/$Command/$Name"
    $wtPath = Join-Path $worktreesDir "$Command-$Name"

    Set-Location $repoRoot
    & git fetch origin main --quiet
    if (Test-Path $wtPath) {
        Write-Host "Worktree already exists: $wtPath"
        Set-Location $wtPath
    }
    else {
        New-Item -ItemType Directory -Force -Path $worktreesDir | Out-Null
        & git worktree add -b $branch $wtPath origin/main
        if ($LASTEXITCODE -ne 0) { throw "git worktree add failed" }
        Set-Location $wtPath
    }
    Write-Host "Worktree ready: $wtPath (branch: $branch)"
    exit
}

switch ($Command) {
    'done' {
        Set-Location $repoRoot
        Write-Host "Rebasing onto origin/main..."
        & git fetch origin main --quiet
        & git rebase origin/main
        if ($LASTEXITCODE -ne 0) { throw "Rebase failed. Resolve conflicts and re-run." }
        Invoke-Verify -NoTests:$Fast
        & git push origin HEAD
        Write-Host "Done. Branch pushed."
    }

    'status' {
        Set-Location $repoRoot
        & git status --short --branch
        Write-Host ""
        & git worktree list
    }

    'sync' {
        Set-Location $repoRoot
        & git fetch origin main --quiet
        & git rebase origin/main
        if ($LASTEXITCODE -ne 0) { throw "Rebase failed. Resolve conflicts manually." }
        Write-Host "Synced with origin/main."
    }

    'check' {
        Invoke-Verify -NoTests
    }

    'verify' {
        Invoke-Verify
    }

    'publish' {
        Set-Location $repoRoot
        if (-not $Fast) {
            Invoke-Verify
        }
        & git push origin HEAD:main
        if ($LASTEXITCODE -ne 0) { throw "Push to main failed." }
        Write-Host "Published to origin/main."
    }
}

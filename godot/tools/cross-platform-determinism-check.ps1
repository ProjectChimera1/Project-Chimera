<#
.SYNOPSIS
    Windows<->Linux golden-checksum determinism gate (Story 1.10c, AR-37).

.DESCRIPTION
    Runs the Tier-1 Godot-free golden-checksum suite (ProjectChimera.Sim.Tests) on BOTH
    Windows and WSL/Ubuntu in VERIFY mode against the SAME committed *.golden.txt files
    (which ARE the Windows-recorded sequences). Green on both legs proves, tick by tick,
    that Linux computes byte-identical Fixed checksums to Windows — the only real proof
    Fixed-point determinism holds cross-platform (the #1-ship-risk-adjacent gate).

    "Diff the two sequences" is realized TRANSITIVELY: both OSes verify against the same
    committed golden, and GoldenChecksumReplay.CompareSequences (exact per-tick uint
    equality) IS the diff. A RED Linux leg means a genuine cross-platform determinism bug
    — fix the code; NEVER re-record a golden to make it green.

    Isolation: the WSL leg builds inside a WSL-native clone of committed HEAD (see
    cross-platform-determinism-check.wsl.sh), so it never reuses Windows obj/bin and never
    disturbs the Windows build. Neither leg installs Godot or sets CHIMERA_GOLDEN_RECORD.

.PARAMETER WslDistro
    WSL distro to use for the Linux leg. Default: Ubuntu-24.04.

.PARAMETER SkipWindows
    Skip the Windows leg (diagnostics only).

.PARAMETER SkipWsl
    Skip the WSL leg (diagnostics only).

.NOTES
    Prerequisite: a .NET 8 SDK (>= 8.0.419) installed in WSL — run
    godot/tools/wsl-dotnet-setup.sh once. See cross-platform-determinism-runbook.md.

    Exit code: 0 only if every executed leg passed; non-zero otherwise.
#>
[CmdletBinding()]
param(
    [string]$WslDistro = 'Ubuntu-24.04',
    [switch]$SkipWindows,
    [switch]$SkipWsl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Repo root = two levels up from this script (godot/tools/ -> repo root).
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj   = 'godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj'

# Hard refuse if the operator's environment would flip the harness into re-baseline mode
# on EITHER leg — recording would overwrite the committed baseline and prove nothing.
if ($env:CHIMERA_GOLDEN_RECORD) {
    throw "CHIMERA_GOLDEN_RECORD is set ('$($env:CHIMERA_GOLDEN_RECORD)'). This gate is VERIFY-ONLY. Unset it and re-run."
}

function ConvertTo-WslPath {
    <#  Translate a Windows path to its WSL /mnt path. Prefer `wsl wslpath`; fall back to a
        drive-letter string map so the script still works if wslpath is unavailable. #>
    param([Parameter(Mandatory)][string]$WinPath)
    try {
        $translated = & wsl -d $WslDistro -- wslpath -a ($WinPath -replace '\\', '/') 2>$null
        if ($LASTEXITCODE -eq 0 -and $translated) { return $translated.Trim() }
    } catch { }
    if ($WinPath -match '^([A-Za-z]):[\\/](.*)$') {
        $drive = $Matches[1].ToLower()
        $rest  = $Matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }
    throw "Cannot translate Windows path '$WinPath' to a WSL path."
}

$windowsPassed = $null   # $null = not run; $true/$false = result
$wslPassed     = $null
$wslDivergence = $null

# ── Windows leg ──────────────────────────────────────────────────────────────
if (-not $SkipWindows) {
    Write-Host ''
    Write-Host '== Windows leg: Tier-1 golden-checksum suite (verify mode) ==' -ForegroundColor Cyan
    Push-Location $repoRoot
    try {
        & dotnet restore $csproj --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Windows 'dotnet restore --locked-mode' failed (exit $LASTEXITCODE)." }
        & dotnet test $csproj -c Release --no-restore --logger 'trx;LogFileName=tier1-windows.trx'
        $windowsPassed = ($LASTEXITCODE -eq 0)
    } finally {
        Pop-Location
    }
    Write-Host ("Windows leg: " + ($(if ($windowsPassed) { 'PASS' } else { 'FAIL' }))) `
        -ForegroundColor ($(if ($windowsPassed) { 'Green' } else { 'Red' }))
}

# ── WSL / Linux leg ──────────────────────────────────────────────────────────
if (-not $SkipWsl) {
    Write-Host ''
    Write-Host "== WSL leg ($WslDistro): Tier-1 golden-checksum suite on Linux (verify mode) ==" -ForegroundColor Cyan
    $wslRepo   = ConvertTo-WslPath $repoRoot
    $wslWorker = ConvertTo-WslPath (Join-Path $PSScriptRoot 'cross-platform-determinism-check.wsl.sh')

    # Stream the worker output live AND capture it so a RED leg can surface the first
    # diverging tick in the final verdict. $LASTEXITCODE reflects the wsl/worker exit.
    $wslLines = & wsl -d $WslDistro -- bash $wslWorker $wslRepo 2>&1 | Tee-Object -Variable _tee
    $wslPassed = ($LASTEXITCODE -eq 0)
    if (-not $wslPassed) {
        $wslDivergence = ($wslLines | Select-String -Pattern 'Checksum drift|DESYNC|first divergence|expected 0x' |
            Select-Object -First 3 | ForEach-Object { $_.Line.Trim() }) -join "`n  "
    }
    Write-Host ("WSL leg: " + ($(if ($wslPassed) { 'PASS' } else { 'FAIL' }))) `
        -ForegroundColor ($(if ($wslPassed) { 'Green' } else { 'Red' }))
}

# ── Verdict ──────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '──────────────────────────────────────────────────────────────'
$ran    = @($windowsPassed, $wslPassed) | Where-Object { $_ -ne $null }
$allOk  = ($ran.Count -gt 0) -and (-not ($ran -contains $false))

if ($allOk -and (-not $SkipWindows) -and (-not $SkipWsl)) {
    Write-Host '✅ Windows<->Linux byte-identical: the 4 committed goldens produce IDENTICAL per-tick' -ForegroundColor Green
    Write-Host '   SimChecksum sequences on both OSes (verify mode; no golden re-recorded).' -ForegroundColor Green
    Write-Host '   Fixed-point determinism holds cross-platform (AR-37 / M1 cross-platform gate GREEN).' -ForegroundColor Green
}
elseif ($allOk) {
    Write-Host '✅ All executed legs PASSED (a leg was skipped — not the full cross-platform proof).' -ForegroundColor Green
}
else {
    Write-Host '❌ CROSS-PLATFORM DESYNC — a leg FAILED. This is a REAL determinism bug; do NOT re-record a golden.' -ForegroundColor Red
    if ($wslDivergence) {
        Write-Host '   First divergence on the Linux leg:' -ForegroundColor Red
        Write-Host "  $wslDivergence" -ForegroundColor Red
    }
    Write-Host '   See cross-platform-determinism-runbook.md §5 ("If it''s RED") for the suspect order.' -ForegroundColor Red
}
Write-Host ('   legs: ' +
    "Windows=$(if ($SkipWindows) { 'skipped' } elseif ($windowsPassed) { 'PASS' } else { 'FAIL' }), " +
    "WSL=$(if ($SkipWsl) { 'skipped' } elseif ($wslPassed) { 'PASS' } else { 'FAIL' })")
Write-Host '──────────────────────────────────────────────────────────────'

if ($allOk) { exit 0 } else { exit 1 }

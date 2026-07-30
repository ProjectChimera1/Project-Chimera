<#
.SYNOPSIS
  bmad-loop verify gate: a story that changes Godot-coupled presentation code cannot reach
  `done` without an attached in-engine verification artifact in its spec.

.DESCRIPTION
  Wired into .bmad-loop/policy.toml as a [verify] command, so the orchestrator runs it after a
  clean review. This is the load-bearing half of the gate: the dev/review agents are *instructed*
  to run the in-engine check (see _bmad/custom/bmad-dev-auto.toml), but instructions alone were
  optional for three epics running and produced three epics of unobserved UI. Story 11-1 shipped
  `done` after three review passes with a defect that deleted the AI opponent's entire starting
  army on any cross-faction launch — invisible in the code, obvious the moment the game ran.

  Exit codes are chosen to drive bmad-loop's own routing:
    0   pass, or the change does not touch a Godot-coupled surface (nothing to gate)
    1   gate missing/incomplete -> ordinary verify failure -> loop dispatches a repair session
        and hands it this script's output as the instruction
    127 the godot-mcp bridge is unreachable -> ENV_FAULT_RCS -> loop PAUSES for an operator
        instead of burning story attempts on something no agent can fix

.NOTES
  Scoping comes from the active run's own state.json (`baseline_commit` + `spec_file`), so the
  diff range is exact rather than guessed. Verify commands get no story substitution from
  bmad-loop, which is why the state file is read directly.

  `spec_file` is frequently EMPTY at the moment this runs (see Resolve-SpecFile), so the spec is
  resolved by the spec-<story_key>.md convention as a fallback. Without that fallback the gate
  fails every Godot-coupled story on its first dev attempt and burns a no-op repair session.
#>

[CmdletBinding()]
param(
    # Bridge port the godot-mcp editor addon listens on.
    [int]$BridgePort = 6550,
    # Skip the bridge reachability probe (used by the self-test).
    [switch]$NoBridgeCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Where story specs live when the run's state.json does not say (it usually does not).
$DefaultSpecFolder = '_bmad-output/implementation-artifacts'
# Set by Get-ActiveRunTask from the run's state.spec_folder, when non-empty.
$script:ActiveSpecFolder = ''

# Presentation surfaces whose behavior cannot be judged from a diff or a headless test. Kept
# deliberately narrow: pure-simulation stories (src/Core/Skirmish, src/Combat, ...) are fully
# covered by Tier-1 and must NOT be forced to open an editor.
$GodotCoupledPatterns = @(
    '^godot/src/UI/',
    '^godot/src/CreationSuite/',
    '^godot/src/Core/Bootstrap/',
    '^godot/src/Core/MainScene\.cs$',
    '^godot/scenes/',
    '\.tscn$',
    '\.tres$'
)

function Write-Section($msg) { Write-Output "[in-engine-gate] $msg" }

function Get-ActiveRunTask {
    <# Newest non-terminal run's active task: its baseline_commit and spec_file. #>
    $runsDir = Join-Path $RepoRoot '.bmad-loop/runs'
    if (-not (Test-Path $runsDir)) { return $null }

    $runDirs = Get-ChildItem $runsDir -Directory -ErrorAction SilentlyContinue |
               Sort-Object Name -Descending
    foreach ($d in $runDirs) {
        $stateFile = Join-Path $d.FullName 'state.json'
        if (-not (Test-Path $stateFile)) { continue }
        try { $state = Get-Content $stateFile -Raw | ConvertFrom-Json } catch { continue }

        if ($state.finished -or $state.stopped) { continue }
        if ($state.PSObject.Properties.Name.Contains('crashed') -and $state.crashed) { continue }
        if (-not $state.PSObject.Properties.Name.Contains('tasks')) { continue }

        # max_parallel is clamped to 1, so at most one task is genuinely in flight. Prefer a
        # non-terminal task; otherwise fall back to the last one recorded.
        $chosen = $null
        foreach ($p in $state.tasks.PSObject.Properties) {
            $t = $p.Value
            if ($t.phase -notin @('done', 'deferred', 'escalated')) { $chosen = $t; break }
            $chosen = $t
        }
        if ($chosen) {
            if ($state.PSObject.Properties.Name.Contains('spec_folder') -and $state.spec_folder) {
                $script:ActiveSpecFolder = $state.spec_folder
            }
            return $chosen
        }
    }
    return $null
}

function Resolve-SpecFile($task) {
    <#
      bmad-loop leaves task.spec_file EMPTY for a sprint-status-sourced run's first dev attempt:
      the story is dispatched by key (`/bmad-dev-auto <story-key>`) and the path only reaches
      state.json when the dev session's result_json is merged back - which happens AFTER this
      verify command runs. Taking that empty value at face value failed the gate on stories that
      had a perfectly good artifact: Story 11-5's dev-1 wrote a complete `result: PASS` block with
      a real runtime digest and was still sent to a repair session that committed nothing. Runs
      for 11-3 and 11-4 show the same empty field, so this is the normal case, not an edge one.

      Fall back to the convention every spec in the folder follows: spec-<story_key>.md.
    #>
    if ($task.PSObject.Properties.Name.Contains('spec_file')) {
        $declared = $task.spec_file
        if ($declared -and (Test-Path $declared)) { return (Resolve-Path $declared).Path }
    }

    if (-not $task.PSObject.Properties.Name.Contains('story_key')) { return $null }
    $key = $task.story_key
    if (-not $key) { return $null }

    $folders = @()
    if ($script:ActiveSpecFolder) { $folders += $script:ActiveSpecFolder }
    $folders += $DefaultSpecFolder
    foreach ($f in $folders) {
        $dir = if ([System.IO.Path]::IsPathRooted($f)) { $f } else { Join-Path $RepoRoot $f }
        $candidate = Join-Path $dir "spec-$key.md"
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }
    return $null
}

function Get-ChangedPaths($baseline) {
    $paths = New-Object System.Collections.Generic.List[string]
    if ($baseline) {
        $committed = & git -C $RepoRoot diff --name-only "$baseline" HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $committed) { $committed | ForEach-Object { $paths.Add($_) } }
    }
    $working = & git -C $RepoRoot status --porcelain 2>$null
    if ($working) {
        foreach ($line in $working) {
            if ($line.Length -gt 3) { $paths.Add($line.Substring(3).Trim().Trim('"')) }
        }
    }
    # @() keeps a single result an array - StrictMode has no .Count on an unwrapped scalar.
    return @($paths | Where-Object { $_ } | Select-Object -Unique)
}

function Test-BridgeReachable($port) {
    try {
        $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        return [bool]$c
    } catch { return $false }
}

# ── Locate the story under verification ──────────────────────────────────────────────────
$task = Get-ActiveRunTask
if (-not $task) {
    Write-Section 'no active bmad-loop run task found - nothing to gate (exit 0).'
    exit 0
}

$baseline = $null
if ($task.PSObject.Properties.Name.Contains('baseline_commit')) { $baseline = $task.baseline_commit }
$declaredSpec = ''
if ($task.PSObject.Properties.Name.Contains('spec_file') -and $task.spec_file) { $declaredSpec = $task.spec_file }
$specFile = Resolve-SpecFile $task
if ($specFile -and -not $declaredSpec) {
    Write-Section "state.json carried no spec_file; resolved by story_key -> $(Split-Path $specFile -Leaf)"
}

$changed = @(Get-ChangedPaths $baseline)
if ($changed.Count -eq 0) {
    Write-Section 'no changes detected against the story baseline - nothing to gate (exit 0).'
    exit 0
}

$coupled = @($changed | Where-Object {
    $p = $_ -replace '\\', '/'
    $hit = $false
    foreach ($pat in $GodotCoupledPatterns) { if ($p -match $pat) { $hit = $true; break } }
    $hit
})

if ($coupled.Count -eq 0) {
    Write-Section 'no Godot-coupled presentation changes in this story - gate not applicable (exit 0).'
    exit 0
}

Write-Section "Godot-coupled changes detected in $($coupled.Count) file(s):"
$coupled | Select-Object -First 12 | ForEach-Object { Write-Output "    $_" }

# ── Require the artifact ─────────────────────────────────────────────────────────────────
$required = @'
### In-Engine Gate - <YYYY-MM-DD>
- surface: <which screen or flow was driven>
- launched: <how the game was reached, e.g. godot_editor_edit run then Play/Launch via emitted `pressed` signals>
- digest: <verbatim runtime-state / HUD text captured at the asserted moment>
- asserted: <expected vs observed, quantified - e.g. "alpha_map_01 authors 3 units for slot 0 and 2 for slot 1; observed P1: 3 units P2: 2 units at Tick 0">
- result: PASS
'@

function Fail-Gate($reason) {
    Write-Output ''
    Write-Section "FAIL: $reason"
    Write-Output ''
    Write-Output 'This story changes Godot-coupled presentation code, so it cannot reach `done`'
    Write-Output 'without evidence that the change was observed RUNNING in the engine.'
    Write-Output ''
    Write-Output 'Drive the real thing over the godot-mcp bridge (the /godot-verify skill is the'
    Write-Output 'shortest path), then append a completed block to the story spec:'
    Write-Output ''
    if ($specFile) {
        Write-Output "  $specFile"
    } else {
        # Never print an empty path here - a bare "" reads as a tooling bug and sent the
        # Story 11-5 repair session hunting for a file that was sitting on disk all along.
        Write-Output "  $(Join-Path (Join-Path $RepoRoot $DefaultSpecFolder) "spec-$($task.story_key).md")"
    }
    Write-Output ''
    Write-Output $required
    Write-Output ''
    Write-Output 'Rules for the block:'
    Write-Output '  - `digest` must be text actually captured from the running game (runtime-state'
    Write-Output '    digest or on-screen HUD), not a description of it.'
    Write-Output '  - `asserted` must compare NUMBERS against the authoring source (scenario JSON,'
    Write-Output '    faction JSON, spec AC). "It looked right" is not an assertion - the Story 11-1'
    Write-Output '    defect that motivated this gate looked completely fine in a screenshot.'
    Write-Output '  - `result: PASS` only when every assertion held. If it did not, fix the code'
    Write-Output '    first - do not record a FAIL to get past this gate.'
    exit 1
}

if (-not $specFile -or -not (Test-Path $specFile)) {
    if (-not $NoBridgeCheck -and -not (Test-BridgeReachable $BridgePort)) {
        Write-Section "spec file not found AND godot-mcp bridge is not listening on $BridgePort."
        exit 127
    }
    $where = if ($declaredSpec) { "declared path '$declaredSpec'" }
             else { "convention path for story_key '$($task.story_key)'" }
    Fail-Gate "story spec not found at the $where - cannot confirm the in-engine artifact."
}

$spec = Get-Content $specFile -Raw

$hasHeading  = $spec -match '(?m)^###\s+In-Engine Gate'
$digest      = [regex]::Match($spec, '(?m)^\s*-\s*digest:\s*(.+)$')
$asserted    = [regex]::Match($spec, '(?m)^\s*-\s*asserted:\s*(.+)$')
$hasPass     = $spec -match '(?m)^\s*-\s*result:\s*PASS\s*$'

# A placeholder left un-filled is not evidence.
$digestOk   = $digest.Success   -and $digest.Groups[1].Value.Trim().Length   -ge 20 -and $digest.Groups[1].Value   -notmatch '^\s*<'
$assertedOk = $asserted.Success -and $asserted.Groups[1].Value.Trim().Length -ge 20 -and $asserted.Groups[1].Value -notmatch '^\s*<'

if ($hasHeading -and $digestOk -and $assertedOk -and $hasPass) {
    Write-Section "PASS - in-engine artifact present in $(Split-Path $specFile -Leaf)."
    exit 0
}

# Missing artifact + no editor running => the operator, not the story, is the blocker.
if (-not $NoBridgeCheck -and -not (Test-BridgeReachable $BridgePort)) {
    Write-Section "godot-mcp bridge is NOT listening on 127.0.0.1:$BridgePort."
    Write-Output 'The in-engine gate cannot be satisfied by any agent until the Godot editor is'
    Write-Output 'open on this project with the godot_mcp addon enabled, and no idle client is'
    Write-Output 'holding the single-client bridge. Pausing for an operator fix rather than'
    Write-Output 'burning story attempts.'
    exit 127
}

$missing = @()
if (-not $hasHeading) { $missing += 'the `### In-Engine Gate` heading' }
if (-not $digestOk)   { $missing += 'a filled-in `- digest:` line (>=20 chars, not a placeholder)' }
if (-not $assertedOk) { $missing += 'a filled-in `- asserted:` line (>=20 chars, not a placeholder)' }
if (-not $hasPass)    { $missing += 'a `- result: PASS` line' }
Fail-Gate ("missing " + ($missing -join '; ') + '.')

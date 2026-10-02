# Trial preflight (S4). Windows PowerShell 5.1 compatible.
#   DESKTOP OK 1920x1080 @96            primary screen size and AppliedDPI as the plans require
#   -Quiet                              also requires an idle machine (10 s sample); exit 0 only when quiet
# Exit: 0 ok, 1 desktop wrong / DPI unreadable / LogonUI present, 2 machine not quiet.
# -Quiet flags a listed build/run process above the per-process limit, and any listed process born during the window.
param([switch]$Quiet, [int]$SampleSeconds = 10, [double]$ProcLimitPct = 5.0, [double]$TotalLimitPct = 10.0,
      [switch]$TestFailSystemTimes)  # test hook: behave as if GetSystemTimes failed
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class ChimeraPf {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int i);
  public static int LogPixelsX(){ IntPtr dc = GetDC(IntPtr.Zero); if (dc == IntPtr.Zero) return 0; int v = GetDeviceCaps(dc, 88); ReleaseDC(IntPtr.Zero, dc); return v; }
  [StructLayout(LayoutKind.Sequential)] public struct FT { public uint lo; public uint hi; }
  [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FT idle, out FT kernel, out FT user);
  static ulong U(FT f){ return ((ulong)f.hi<<32)|f.lo; }
  public static bool Times(out ulong idle, out ulong total){ FT i,k,u; bool ok = GetSystemTimes(out i,out k,out u); idle=U(i); total=U(k)+U(u); return ok; }
}
"@
[void][ChimeraPf]::SetProcessDPIAware()
$w = [ChimeraPf]::GetSystemMetrics(0); $h = [ChimeraPf]::GetSystemMetrics(1)
$dpi = $null
try { $dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI).AppliedDPI } catch { }
if ($dpi -eq $null) { try { $dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop' -Name LogPixels).LogPixels } catch { } }
if ($dpi -eq $null) { $lp = [ChimeraPf]::LogPixelsX(); if ($lp -gt 0) { $dpi = $lp } }
if ($dpi -eq $null) { $dpi = '?' }   # never assume 96: an unread DPI fails the desktop check
$logon = @(Get-Process -Name LogonUI -ErrorAction SilentlyContinue).Count -gt 0
$parsec = @(Get-Process -Name parsecd -ErrorAction SilentlyContinue).Count -gt 0
$parsecState = if ($parsec) { 'running' } else { 'absent' }

$fail = 0
if ($w -eq 1920 -and $h -eq 1080 -and "$dpi" -eq '96' -and -not $logon) {
  Write-Output ("DESKTOP OK {0}x{1} @{2}" -f $w, $h, $dpi)
} else {
  Write-Output ("DESKTOP NOT 1920x1080@100% (is {0}x{1} @{2}, LogonUI {3})" -f $w, $h, $dpi, $logon)
  $fail = 1
}
Write-Output ("PARSEC {0}" -f $parsecState)

if ($Quiet) {
  $pat = '^(dotnet|ilc|cl|link|MSBuild|testhost.*|Godot.*|ffmpeg|ShaderCompileWorker|blender)$'
  function Snap { $d = @{}; foreach ($p in Get-Process) { try { $d[$p.Id] = @($p.ProcessName, $p.TotalProcessorTime.TotalSeconds) } catch { } }; $d }
  $a = Snap
  $i0 = [uint64]0; $t0 = [uint64]0; $ok0 = [ChimeraPf]::Times([ref]$i0, [ref]$t0)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Start-Sleep -Seconds $SampleSeconds
  $b = Snap; $el = $sw.Elapsed.TotalSeconds
  $i1 = [uint64]0; $t1 = [uint64]0; $ok1 = [ChimeraPf]::Times([ref]$i1, [ref]$t1)
  if ($TestFailSystemTimes) { $ok1 = $false }
  if (-not ($ok0 -and $ok1) -or $t1 -le $t0 -or $i1 -lt $i0) {
    # fail closed: an unreadable system-time counter must never read as an idle machine
    Write-Output "TOTALCPU ?"
    Write-Output "QUIET NOT total=? (GetSystemTimes failed)"
    if ($fail -eq 0) { $fail = 2 }
    exit $fail
  }
  $total = 100.0 * (1.0 - ([double]($i1 - $i0) / [double]($t1 - $t0)))
  $busy = @()
  foreach ($id in $b.Keys) {
    $n = $b[$id][0]
    if ($n -notmatch $pat) { continue }
    if ($a.ContainsKey($id) -and $a[$id][0] -eq $n) {
      $pct = 100.0 * ($b[$id][1] - $a[$id][1]) / $el
      if ($pct -gt $ProcLimitPct) { $busy += ("{0}({1})={2:N1}%" -f $n, $id, $pct) }
    } else {
      # born inside the window: all of its CPU time was spent inside it, and a build tool starting now is activity
      $pct = 100.0 * $b[$id][1] / $el
      $busy += ("{0}({1})=new,{2:N1}%" -f $n, $id, $pct)
    }
  }
  $mp = '?'
  foreach ($id in $b.Keys) { if ($b[$id][0] -eq 'MsMpEng' -and $a.ContainsKey($id)) { $mp = "{0:N1}%" -f (100.0 * ($b[$id][1] - $a[$id][1]) / $el) } }
  Write-Output ("MsMpEng {0}" -f $mp)
  Write-Output ("TOTALCPU {0:N1}%" -f $total)
  if ($busy.Count -gt 0 -or $total -ge $TotalLimitPct) {
    Write-Output ("QUIET NOT busy=[{0}] total={1:N1}%" -f ($busy -join ' '), $total)
    if ($fail -eq 0) { $fail = 2 }
  } else { Write-Output "QUIET OK" }
}
exit $fail

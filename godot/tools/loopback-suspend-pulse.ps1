# Force genuinely long frames on one loopback client by suspending its process in
# short pulses — verifies the DW-924 [Catchup] phase suffix end-to-end.
param([int]$Pulses = 12, [int]$SuspendMs = 150, [int]$GapMs = 350)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class NtSusp {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
"@
$clients = @()
foreach ($p in (Get-Process Godot* -ErrorAction SilentlyContinue)) {
    $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine
    if ($cmd -match '--autojoin' -and $cmd -notmatch 'console') { $clients += $p }
}
if ($clients.Count -eq 0) { # fall back: any autojoin process
    foreach ($p in (Get-Process Godot* -ErrorAction SilentlyContinue)) {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine
        if ($cmd -match '--autojoin') { $clients += $p }
    }
}
if ($clients.Count -eq 0) { Write-Host 'NO CLIENT FOUND'; exit 1 }
$t = ($clients | Sort-Object StartTime)[-1]
Write-Host "pulsing pid=$($t.Id): $Pulses x ${SuspendMs}ms suspend / ${GapMs}ms gap"
$h = [NtSusp]::OpenProcess(0x1F0FFF, $false, $t.Id)
for ($i = 0; $i -lt $Pulses; $i++) {
    [NtSusp]::NtSuspendProcess($h) | Out-Null
    Start-Sleep -Milliseconds $SuspendMs
    [NtSusp]::NtResumeProcess($h) | Out-Null
    Start-Sleep -Milliseconds $GapMs
}
[NtSusp]::CloseHandle($h) | Out-Null
Write-Host 'done — process left running'

import os
import shutil
import subprocess
import sys
import time

import pytest

KIT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PF = os.path.join(KIT, "preflight.ps1")
pytestmark = pytest.mark.skipif(sys.platform != "win32" or not shutil.which("powershell.exe"), reason="needs Windows PowerShell")


def cmd(*args):
    return ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", PF, *args]


def run(*args):
    return subprocess.run(cmd(*args), capture_output=True, text=True, timeout=120)


def busy_ilc(tmp_path):
    """A busy python copy named like a build tool (ilc.exe)."""
    exe = tmp_path / "ilc.exe"
    shutil.copyfile(sys.executable, exe)
    p = subprocess.Popen([str(exe), "-c", "while True: pass"], cwd=os.path.dirname(sys.executable))
    time.sleep(1)
    if p.poll() is not None:
        pytest.skip("could not start renamed python")
    return p


def test_desktop_line_and_exit():
    r = run()
    assert r.stdout.startswith("DESKTOP ")
    assert (r.returncode == 0) == ("DESKTOP OK 1920x1080 @96" in r.stdout)


def test_quiet_detects_busy_named_process(tmp_path):
    p = busy_ilc(tmp_path)
    try:
        r = run("-Quiet", "-SampleSeconds", "3", "-TotalLimitPct", "100")
    finally:
        p.kill()
    # rc 1 when the desktop check also fails (it takes precedence); the CPU finding is checked on its own
    assert "QUIET NOT" in r.stdout and "ilc(" in r.stdout and r.returncode in (1, 2), r.stdout


def test_quiet_detects_process_born_mid_window(tmp_path):
    """The born-in-window branch itself: ilc.exe starts after the window opens, so it must be reported as `=new,`."""
    pf = subprocess.Popen(cmd("-Quiet", "-SampleSeconds", "12", "-TotalLimitPct", "100"),
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    head = []
    for line in pf.stdout:  # DESKTOP and PARSEC print just before the first process snapshot
        head.append(line)
        if line.startswith("PARSEC"):
            break
    time.sleep(3)  # let the first snapshot finish; the 12 s window is now open
    p = busy_ilc(tmp_path)
    try:
        out, _ = pf.communicate(timeout=120)
    finally:
        p.kill()
    out = "".join(head) + out
    assert "QUIET NOT" in out and "ilc(" in out and "=new," in out and pf.returncode in (1, 2), out


def test_quiet_fails_closed_when_system_times_fail():
    r = run("-Quiet", "-SampleSeconds", "1", "-TotalLimitPct", "100", "-ProcLimitPct", "100000", "-TestFailSystemTimes")
    assert "QUIET NOT total=?" in r.stdout and "QUIET OK" not in r.stdout and r.returncode in (1, 2), r.stdout

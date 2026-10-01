#!/usr/bin/env python3
"""logscan.py - one Unreal log rule set for all three trial checks (EXECUTION.md 3 C4).

Calibrated on 9 UnrealEditor.exe -game logs (all game_*/warm_* in P/LookTest/logs scan clean). Packaged logs
are UNVERIFIED until C11's first packaged run. The rule set is stricter than C4, which fails only `Failed to load '/`
and game mode: any allowance added for packaged logs must cite an evidence line, as the 4 profiler DLLs below do.
A clean run logs exactly four `LogWindows: Failed to load '<profiler>.dll'` lines (aqProf, VtuneApi,
VtuneApi32e, WinPixGpuCapturer); those are allowed. Every other line containing `Failed to load`
fails, as do the rules below (C's parse set, B T9's linker/streaming set, crashes, shot failures).
`--profile editor` (editor and commandlet logs) also allows, by exact text, Wintab32.dll and two
known engine ensures seen on clean editor/commandlet runs of P (see EDITOR_ALLOW).
Usage: logscan.py LOG [LOG...] [--profile game|editor] [--extra-fail REGEX]... [--ignore REGEX]...
Exit 0 clean, 1 findings, 2 usage/io.
"""
import argparse
import re
import sys

ALLOWED_LOAD = {"game": ("aqProf", "VtuneApi", "VtuneApi32e", "WinPixGpuCapturer")}
ALLOWED_LOAD["editor"] = ALLOWED_LOAD["game"] + ("Wintab32",)

# (rule name, compiled regex). A line can match several rules.
RULES = [
    ("failed-to-load-asset", re.compile(r"Failed to load '/")),
    ("failed-to-load-gamemode", re.compile(r"Failed to load game mode")),
    ("chimera-error", re.compile(r"LogChimera\w*: Error")),
    ("realtimemesh-error", re.compile(r"(LogRealtimeMesh[A-Za-z]*|RealtimeMesh): (Error|Warning)")),
    ("ensure", re.compile(r"Ensure condition failed")),
    ("fatal", re.compile(r"\bFatal\b|Unhandled Exception|Assertion failed")),  # C's rule: any `Fatal` (plan-c 3.8)
    ("critical-error", re.compile(r"Critical error")),
    ("linker-streaming", re.compile(r"LogLinker: (Error|Warning)|LogStreaming: (Error|Warning)")),
    ("failed-to-find", re.compile(r"LogUObjectGlobals: Warning: Failed to find")),
    ("update-texture-regions", re.compile(r"UpdateTextureRegions called for")),
    ("shot-failure", re.compile(r"SHOT (TIMEOUT|MISSING|NO CALLBACK)")),
]
FAILED_LOAD_ANY = re.compile(r"Failed to load")
FAILED_LOAD_DLL = re.compile(r"LogWindows: Failed to load '([^'/]*)'")

# editor profile: ensures seen on clean P editor/commandlet runs, allowed only by exact condition and file
EDITOR_ALLOW = [
    # python commandlet shutdown (MegascansPlugin atexit), P/LookTest/logs/cmd_probe.log:1620
    re.compile(r"Ensure condition failed: IsInGameThread\(\) \|\| IsInAsyncLoadingThread\(\)\s+\[File:[^\]]*TickableEditorObject\.h\]"),
    # editor viewport client, P/LookTest/logs/editor_3.stdout.log:32437 and :32483
    re.compile(r"Ensure condition failed: !bCheckMissingOverride \|\| bRemoved\s+\[File:[^\]]*EditorViewportClient\.cpp\]"),
]


def _allowed_load(line, profile):
    m = FAILED_LOAD_DLL.search(line)
    if not m:
        return False
    base = m.group(1).replace("\\", "/").rsplit("/", 1)[-1]
    if not base.lower().endswith(".dll"):
        return False
    return base[:-4] in ALLOWED_LOAD[profile]


def scan_lines(lines, extra_fail=(), ignore=(), profile="game"):
    """Return a list of (lineno, rule, text) findings."""
    extra = [re.compile(p) for p in extra_fail]
    skip = [re.compile(p) for p in ignore]
    out = []
    for n, line in enumerate(lines, 1):
        line = line.rstrip("\r\n")
        if any(s.search(line) for s in skip):
            continue
        if profile == "editor" and any(a.search(line) for a in EDITOR_ALLOW):
            continue
        hit = set()
        for name, rx in RULES:
            if rx.search(line):
                hit.add(name)
        if FAILED_LOAD_ANY.search(line) and not hit & {"failed-to-load-asset", "failed-to-load-gamemode"} \
                and not _allowed_load(line, profile):
            hit.add("failed-to-load-other")
        for i, rx in enumerate(extra):
            if rx.search(line):
                hit.add("extra-%d" % i)
        for h in sorted(hit):
            out.append((n, h, line.strip()[:240]))
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("logs", nargs="+")
    ap.add_argument("--profile", choices=("game", "editor"), default="game")
    ap.add_argument("--extra-fail", action="append", default=[])
    ap.add_argument("--ignore", action="append", default=[])
    a = ap.parse_args(argv)
    bad = 0
    for path in a.logs:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                lines = f.readlines()
        except OSError as e:
            print("LOGSCAN ERROR %s: %s" % (path, e))
            return 2
        found = scan_lines(lines, a.extra_fail, a.ignore, a.profile)
        for n, rule, text in found:
            print("%s:%d: %s: %s" % (path, n, rule, text))
        bad += len(found)
        if not found:
            print("LOGSCAN OK %s (%d lines)" % (path, len(lines)))
    if bad:
        print("LOGSCAN FAIL %d finding(s)" % bad)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

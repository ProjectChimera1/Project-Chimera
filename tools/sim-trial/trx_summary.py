#!/usr/bin/env python3
"""Summarise .trx test results for the trial's T1 gate.

Usage: trx_summary.py [file.trx | dir ...] [--failed]
  default argument: D:/Projects/Chimera-Unreal/TrialOut/a/tests
  A directory means ONLY the newest *.trx in it (by mtime), so OUT/tests can hold every task's trx and the
  bare command still reports the run that just finished. Several explicitly named .trx files are summed.

Prints, per summarised file:   file=<name> run=<ResultSummary outcome> counters_total=<n>
then one line:                 total= passed= failed= skipped=
Outcome mapping (UnitTestResult rows): Passed -> passed; NotExecuted -> skipped; EVERY other outcome
(Failed, Error, Timeout, Aborted, Inconclusive, ...) -> failed. With --failed, each failing test is listed.

Exit code: 0 = consistent run with failed=0; 1 = failed > 0; 2 = MISMATCH (run outcome not Completed/Failed,
outcome contradicts the rows, Counters disagree with the rows, or no trx found). A MISMATCH line says why.
"""
import glob, os, sys
import xml.etree.ElementTree as ET

DEFAULT = "D:/Projects/Chimera-Unreal/TrialOut/a/tests"
FAIL_COUNTERS = ("failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
                 "notRunnable", "disconnected")


def trx_files(args):
    out = []
    for a in args or [DEFAULT]:
        if os.path.isdir(a):
            found = glob.glob(os.path.join(a, "*.trx"))
            if found:
                out.append(max(found, key=os.path.getmtime))
        else:
            out.append(a)
    return out


def local(tag):
    return tag.rsplit("}", 1)[-1]


def summarise(path):
    """Return (rows dict, failing names, mismatch reasons, header line) for one trx file."""
    rows = {"total": 0, "passed": 0, "failed": 0, "skipped": 0}
    names, why = [], []
    name = os.path.basename(path)
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        return rows, names, [f"{name}: unreadable trx ({e})"], f"file={name} run=? counters_total=?"
    run_outcome, counters = None, None
    for el in root.iter():
        t = local(el.tag)
        if t == "UnitTestResult":
            rows["total"] += 1
            o = el.get("outcome")
            if o == "Passed":
                rows["passed"] += 1
            elif o == "NotExecuted":
                rows["skipped"] += 1
            else:
                rows["failed"] += 1
                names.append(f"{el.get('testName')} [{o}]")
        elif t == "ResultSummary":
            run_outcome = el.get("outcome")
        elif t == "Counters":
            counters = {k: int(v) for k, v in el.attrib.items() if v.lstrip("-").isdigit()}
    if run_outcome is None or counters is None:
        why.append(f"{name}: no ResultSummary/Counters (truncated or crashed run)")
        header = f"file={name} run={run_outcome} counters_total=?"
        return rows, names, why, header
    header = f"file={name} run={run_outcome} counters_total={counters.get('total', '?')}"
    if run_outcome not in ("Completed", "Failed"):
        why.append(f"{name}: run outcome is {run_outcome}, not Completed")
    if run_outcome == "Failed" and rows["failed"] == 0:
        why.append(f"{name}: run outcome Failed but no failing UnitTestResult row")
    if run_outcome == "Completed" and rows["failed"] > 0:
        why.append(f"{name}: run outcome Completed but {rows['failed']} failing rows")
    if counters.get("total") != rows["total"]:
        why.append(f"{name}: Counters total={counters.get('total')} but {rows['total']} UnitTestResult rows")
    if counters.get("passed") != rows["passed"]:
        why.append(f"{name}: Counters passed={counters.get('passed')} but {rows['passed']} Passed rows")
    cfail = sum(counters.get(k, 0) for k in FAIL_COUNTERS)
    if cfail != rows["failed"]:
        why.append(f"{name}: Counters failing={cfail} but {rows['failed']} failing rows")
    if counters.get("total", 0) - counters.get("executed", 0) != rows["skipped"]:
        why.append(f"{name}: Counters total-executed={counters.get('total', 0) - counters.get('executed', 0)}"
                   f" but {rows['skipped']} NotExecuted rows")
    return rows, names, why, header


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    show_failed = "--failed" in sys.argv
    files = trx_files(args)
    total = {"total": 0, "passed": 0, "failed": 0, "skipped": 0}
    names, why = [], []
    if not files:
        why.append("no .trx file found in " + ", ".join(args or [DEFAULT]))
    for f in files:
        rows, n, w, header = summarise(f)
        print(header)
        for k in total:
            total[k] += rows[k]
        names += n
        why += w
    print("total={total} passed={passed} failed={failed} skipped={skipped}".format(**total))
    if show_failed:
        for n in names:
            print("FAILED " + n)
    for w in why:
        print("MISMATCH " + w)
    sys.exit(2 if why else (1 if total["failed"] else 0))


if __name__ == "__main__":
    main()

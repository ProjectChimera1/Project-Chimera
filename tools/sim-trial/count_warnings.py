#!/usr/bin/env python3
"""Count UNIQUE compiler/analyzer warnings by rule id from MSBuild output (stdin or file).

A warning is unique by (file, line, col, rule id, message); MSBuild prints each twice
(inline and in the summary) and per target framework. Prints 'RULE=count' pairs,
descending, on one line, then 'unique_total=N'.
"""
import re, sys
from collections import Counter

PAT = re.compile(r"^\s*(?P<loc>.+?)\s*:\s*warning\s+(?P<id>[A-Za-z]+\d+)\s*:\s*(?P<msg>.*?)(?:\s*\[[^\]]*\])?\s*$")

def main():
    text = open(sys.argv[1], encoding="utf-8", errors="replace").read() if len(sys.argv) > 1 else sys.stdin.read()
    seen = set()
    c = Counter()
    for line in text.splitlines():
        m = PAT.match(line)
        if not m:
            continue
        key = (m["loc"].lower().replace("\\", "/"), m["id"], m["msg"])
        if key in seen:
            continue
        seen.add(key)
        c[m["id"]] += 1
    print(" ".join(f"{k}={v}" for k, v in c.most_common()))
    print(f"unique_total={sum(c.values())}")

if __name__ == "__main__":
    main()

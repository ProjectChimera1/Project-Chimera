#!/usr/bin/env python3
"""List the exported symbol names of a PE DLL (derived from proto-a/pe_exports_review.py).

Usage: check_exports.py <dll> [--expect NAME ...] [--allowlist FILE]
  The DLL is the first argument that is not an option or an option's value; options may come in any order.
Prints the sorted export names.
  --expect NAME ...  (names run to the next --option or *.dll argument) exit 1 if any expected name is missing.
  --allowlist FILE   one export name per line ('#' comments and blank lines ignored). Compares the DLL's exports,
                     ignoring the runtime's DotNetRuntimeDebugHeader, with the list exactly and prints
                     'missing=N extra=N allowlist=ok|fail' plus MISSING/EXTRA lines; exit 1 on fail.
"""
import struct, sys

def exports(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    optsz = struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    ddir = opt + (112 if magic == 0x20B else 96)
    exp_rva, _ = struct.unpack_from("<II", d, ddir)
    if exp_rva == 0:
        return []
    secs = []
    s0 = opt + optsz
    for i in range(nsec):
        o = s0 + 40 * i
        vsz, va, rsz, rptr = struct.unpack_from("<IIII", d, o + 8)
        secs.append((va, max(vsz, rsz), rptr))
    def r2o(rva):
        for va, sz, p in secs:
            if va <= rva < va + sz:
                return rva - va + p
        raise ValueError(rva)
    e = r2o(exp_rva)
    nnames = struct.unpack_from("<I", d, e + 24)[0]
    anames = struct.unpack_from("<I", d, e + 32)[0]
    out = []
    for i in range(nnames):
        nr = struct.unpack_from("<I", d, r2o(anames) + 4 * i)[0]
        o = r2o(nr)
        out.append(d[o:d.index(b"\0", o)].decode())
    return sorted(out)

IGNORED = {"DotNetRuntimeDebugHeader"}


def parse_args(a):
    dll, expect, allow, i = None, [], None, 0
    while i < len(a):
        if a[i] == "--expect":
            i += 1
            while i < len(a) and not a[i].startswith("--") and not a[i].lower().endswith(".dll"):
                expect.append(a[i])
                i += 1
            continue
        if a[i] == "--allowlist":
            allow = a[i + 1]
            i += 2
            continue
        if a[i].startswith("--"):
            sys.exit("unknown option " + a[i])
        if dll is None:
            dll = a[i]
        else:
            sys.exit("unexpected argument " + a[i])
        i += 1
    if dll is None:
        sys.exit(__doc__)
    return dll, expect, allow


if __name__ == "__main__":
    dll, exp, allow = parse_args(sys.argv[1:])
    names = exports(dll)
    print(", ".join(names))
    rc = 0
    missing = [x for x in exp if x not in names]
    if missing:
        print("MISSING " + ", ".join(missing))
        rc = 1
    if allow:
        want = set()
        for line in open(allow, encoding="utf-8"):
            line = line.split("#", 1)[0].strip()
            if line:
                want.add(line)
        have = set(names) - IGNORED
        miss, extra = sorted(want - have), sorted(have - want)
        ok = not miss and not extra
        print(f"missing={len(miss)} extra={len(extra)} allowlist={'ok' if ok else 'fail'}")
        if miss:
            print("MISSING " + ", ".join(miss))
        if extra:
            print("EXTRA " + ", ".join(extra))
        if not ok:
            rc = 1
    sys.exit(rc)

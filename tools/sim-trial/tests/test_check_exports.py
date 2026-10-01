"""Self-tests of check_exports.py's header and allow-list parsing (Unreal trial A5): comments are stripped, only function
declarations count, and the DLL-vs-header-vs-allow-list comparison fails on a missing or extra name. The DLL itself is
read through a minimal synthetic PE with an export table. Run: python -m pytest tools/sim-trial/tests -q"""
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, ".."))
import check_exports as ce  # noqa: E402

HEADER = """
/* int32_t chimera_in_comment(void); */
// int32_t chimera_line_comment(void);
#define CHIMERA_X 1
typedef struct ChimeraUnit { int32_t id; } ChimeraUnit;
int32_t chimera_abi_version(void);
int32_t chimera_read_units(int32_t session, ChimeraUnit* out, int32_t cap, int32_t* count);
int32_t chimera_step(int32_t session);
"""


def make_dll(path, names):
    """A tiny PE32+ with one section holding an export directory (names only)."""
    names = sorted(names)
    rva = 0x1000
    name_blobs = [n.encode() + b"\0" for n in names]
    n = len(names)
    off_funcs = 40
    off_names = off_funcs + 4 * n
    off_ords = off_names + 4 * n
    off_strs = off_ords + 2 * n
    strs, pos = [], off_strs
    for b in name_blobs:
        strs.append(pos)
        pos += len(b)
    sec = bytearray(pos)
    struct.pack_into("<I", sec, 20, n)           # NumberOfFunctions
    struct.pack_into("<I", sec, 24, n)           # NumberOfNames
    struct.pack_into("<I", sec, 28, rva + off_funcs)
    struct.pack_into("<I", sec, 32, rva + off_names)
    struct.pack_into("<I", sec, 36, rva + off_ords)
    for i in range(n):
        struct.pack_into("<I", sec, off_names + 4 * i, rva + strs[i])
        struct.pack_into("<H", sec, off_ords + 2 * i, i)
    sec[off_strs:pos] = b"".join(name_blobs)
    pe = 0x80
    opt_size = 240
    hdr = bytearray(0x200)
    hdr[0:2] = b"MZ"
    struct.pack_into("<I", hdr, 0x3C, pe)
    hdr[pe:pe + 4] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", hdr, pe + 4, 0x8664, 1, 0, 0, 0, opt_size, 0x2022)
    opt = pe + 24
    struct.pack_into("<H", hdr, opt, 0x20B)
    struct.pack_into("<II", hdr, opt + 112, rva, len(sec))   # data directory 0 = export table
    s0 = opt + opt_size
    hdr[s0:s0 + 8] = b".edata\0\0"
    struct.pack_into("<IIII", hdr, s0 + 8, len(sec), rva, len(sec), 0x200)
    with open(path, "wb") as f:
        f.write(bytes(hdr) + bytes(sec))


def test_header_functions_ignores_comments_and_types(tmp_path):
    p = tmp_path / "_hdr.h"
    p.write_text(HEADER)
    assert ce.header_functions(str(p)) == ["chimera_abi_version", "chimera_read_units", "chimera_step"]


def run(tmp_path, dll_names, header_text, allow_text):
    dll, hdr, allow = tmp_path / "t.dll", tmp_path / "t.h", tmp_path / "allow.txt"
    make_dll(str(dll), dll_names)
    hdr.write_text(header_text)
    allow.write_text(allow_text)
    r = subprocess.run([sys.executable, os.path.join(HERE, "..", "check_exports.py"), str(dll), "--allowlist", str(allow),
                        "--header", str(hdr)], capture_output=True, text=True)
    return r.returncode, r.stdout


def test_all_three_agree(tmp_path):
    names = ["chimera_abi_version", "chimera_read_units", "chimera_step", "DotNetRuntimeDebugHeader"]
    rc, out = run(tmp_path, names, HEADER, "# list\nchimera_abi_version\nchimera_read_units\nchimera_step\n")
    assert rc == 0, out
    assert "missing=0 extra=0 allowlist=ok" in out and "header=ok" in out


def test_extra_export_fails(tmp_path):
    names = ["chimera_abi_version", "chimera_read_units", "chimera_step", "chimera_sneaky_write"]
    rc, out = run(tmp_path, names, HEADER, "chimera_abi_version\nchimera_read_units\nchimera_step\n")
    assert rc == 1 and "EXTRA chimera_sneaky_write" in out and "header=fail" in out


def test_header_missing_a_function_fails(tmp_path):
    names = ["chimera_abi_version", "chimera_read_units", "chimera_step"]
    rc, out = run(tmp_path, names, HEADER + "int32_t chimera_not_exported(void);\n",
                  "chimera_abi_version\nchimera_read_units\nchimera_step\n")
    assert rc == 1 and "HEADER_DECLARES_BUT_DLL_LACKS chimera_not_exported" in out


def test_allowlist_differs_from_header_fails(tmp_path):
    names = ["chimera_abi_version", "chimera_read_units", "chimera_step"]
    rc, out = run(tmp_path, names, HEADER, "chimera_abi_version\nchimera_read_units\n")
    assert rc == 1 and "allowlist=fail" in out

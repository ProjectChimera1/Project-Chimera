"""hlsl_include.py - the one text include of the terrain trial's Custom-node HLSL (plan C scatter 3.7, task S6). Pure Python, no `unreal`:
make_ground_material.py (inside the editor), scatter_material_spec.py (editor and pytest) and the tests read the same expansion.

A line that is exactly `//#INCLUDE <file>` (no indentation, nothing after the name) is replaced by the text of <file>, looked up next to this
module (T/Scripts). The included file's leading lines that start with `//!` are its own documentation and are dropped, so the bytes that reach
the material are exactly the shared block. Includes do not nest (an `//#INCLUDE` inside an included file is an error), every included file
must end with a newline, and a missing file is an error: a material is never built from a half-expanded string.
Line endings are normalised to LF on read (CRLF and lone CR become LF), as the pre-S6 builders' universal-newline reads did: git runs with
core.autocrlf=true on this machine and no eol rule covers .hlsl, so a fresh checkout may write these files with CRLF.

Why a text include: a Custom node's Code is one string (MaterialExpressionCustom.h); the ground (M_ChimeraGround) and the grass blades
(M_ScatterBlade) must compute the meadow patch field with the same statements and the same scalars, so the grass colour follows the ground.
The ground's expanded string must stay byte-identical to the string it had before the block moved into Scripts/ChimeraPatch.hlsl
(plan C scatter 4 S6; test_hlsl_include.py pins its sha256).
"""
import hashlib
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
DIRECTIVE = re.compile(r"^//#INCLUDE[ \t]+([A-Za-z0-9_.]+)$")


def to_lf(text):
    """`text` with CRLF and lone CR line endings replaced by LF."""
    return text.replace("\r\n", "\n").replace("\r", "\n")


def read_lf(path):
    """The UTF-8 text of `path` with every line ending normalised to LF (the universal-newline reading the pre-S6 builders used)."""
    with open(path, encoding="utf-8", newline="") as f:
        return to_lf(f.read())


def included_text(name, base_dir=HERE):
    """The body of one include file: its text without the leading `//!` documentation lines."""
    path = os.path.join(base_dir, name)
    if not os.path.isfile(path):
        raise ValueError("hlsl include not found: %s" % path)
    text = read_lf(path)
    if not text.endswith("\n"):
        raise ValueError("%s: must end with a newline" % path)
    lines = text.split("\n")
    i = 0
    while i < len(lines) and lines[i].startswith("//!"):
        i += 1
    body = "\n".join(lines[i:])
    for ln in body.split("\n"):
        if DIRECTIVE.match(ln):
            raise ValueError("%s: nested //#INCLUDE is not allowed" % path)
    return body


def expand(text, base_dir=HERE):
    """`text` with every `//#INCLUDE <file>` line replaced by that file's body (included_text). Returns (expanded, [names]).
    Line endings in `text` are normalised to LF first, so a directive is matched whatever the checkout's line endings."""
    out, names = [], []
    lines = to_lf(text).split("\n")
    for ln in lines:
        m = DIRECTIVE.match(ln)
        if m:
            body = included_text(m.group(1), base_dir)
            names.append(m.group(1))
            # The body ends with "\n", which stands for this directive line's own newline (join adds it back).
            out.append(body[:-1])
        else:
            out.append(ln)
    return "\n".join(out), names


def expand_file(path, base_dir=HERE):
    """(expanded text, include names, sha256 of the expanded UTF-8 bytes) of an HLSL file."""
    code, names = expand(read_lf(path), base_dir)
    return code, names, hashlib.sha256(code.encode("utf-8")).hexdigest()

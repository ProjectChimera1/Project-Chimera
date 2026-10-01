#!/usr/bin/env python3
"""secret_scan.py - fail on secret assignments: a SecurityToken, *Key, *Token, *Secret or *Password name
assigned a value (EXECUTION.md 2.1 S4). Names match case-insensitively, so .env forms such as
ANTHROPIC_API_KEY or secret_key are caught as well as UE's SecurityToken.

Two matches, either fails the file:
  line start  a name at the start of a line (optionally after export/set/const/let/var/ENV, `$` or `$env:`,
              a UE ini +-.! prefix, a YAML `- `, or quotes for JSON), then `=` or `:` (never `==` or `=>`);
  in-line     anywhere in a line: a secret-like name, an optional type annotation (`name: string`), `=` or `:`,
              then a quoted literal of 8+ characters, optionally wrapped as TEXT("..."), L"...", @"..." or u8"..."
              (C#, C++, PowerShell, env-prefixed commands, dict literals).
Strong names (api, secret, password, access, private, auth, credential, encryption, signing, bearer, SecurityToken)
fail on any real value. Other *key/*token names (`asset_key`, `cacheKey`) fail on `=` at line start, and otherwise
only when the value looks like a secret (a known prefix such as sk-, ghp_, AKIA, eyJ, or 16+ characters with
2+ digits, 2+ letters and 10+ distinct characters). Ignored values: empty, placeholders (<...>, ${...}, %X%, $X),
escape sequences, code expressions, type names, literals such as NAME_None/nullptr/true/false/null, and for weak
*Key names a PascalCase word (UE input keys like SpaceBar). A line carrying `secret-scan: allow` is skipped and counted
(SECRET_SCAN ALLOWED n). Values are never printed.
Files are decoded as UTF-8, or UTF-16 when they carry a BOM or look like BOM-less UTF-16 (PowerShell 5.1 `>` output).
A file that still looks binary is skipped; when its extension is a text one, `SECRET_SCAN SKIP <file> (binary)` says so.
Usage: secret_scan.py PATH [PATH...]   exit 0 clean, 1 found, 2 io error.
Directories are walked, skipping .git, Binaries, Intermediate, Saved, DerivedDataCache, node_modules and binary files.
"""
import os
import re
import sys

ASSIGN = re.compile(
    r"""^\s*(?:-\s+)?(?:(?:export|set|const|let|var|ENV|env)\s+)?(?:\$env:|\$)?[+\-.!]?["']?(?P<name>[A-Za-z_][\w.\-]*)["']?\s*"""
    r"""(?P<op>:(?!:)|=(?![=>]))\s*(?P<v>.*?)\s*$""")
INLINE = re.compile(
    r"""(?<![\w$])["']?(?P<name>[A-Za-z_][\w\-]*)["']?\s*(?::\s*[A-Za-z_][\w.<>\[\]?]*\s*)?"""
    r"""(?<![=!<>+\-*/%&|^~])(?P<op>=(?![=>~])|:(?!:))\s*(?:TEXT\(\s*|u8|[LuU@])?(?P<q>["'])(?P<lit>[^"'\\\s]{8,})(?P=q)""")
SECRET_NAME = re.compile(r"(?i)(securitytoken|token|secret|password|passwd|key|privateexponent)s?$")
STRONG_NAME = re.compile(r"(?i)securitytoken|api|secret|passw|access|private|auth|credential|encryption|signing|bearer")
KEY_ONLY = re.compile(r"(?i)keys?$")
LITERALS = {"name_none", "nullptr", "null", "none", "true", "false", "nil", "undefined", "{}", "[]", "\"\"", "''"}
TYPE_NAMES = {"int", "str", "string", "float", "double", "bool", "boolean", "char", "byte", "long", "short", "object",
              "fstring", "fname", "ftext", "any", "number", "dict", "list", "array", "bytes", "uint", "int32", "int64",
              "uint8", "uint32", "uint64", "secretstr", "securestring", "keycode", "key", "variant", "void"}
PLACEHOLDER = re.compile(r"^(<[^>]*>|\$\{[^}]*\}|%\w+%|\$\w+|\\.*|\.\.\.|x{3,}|\*{3,})$", re.I)
CODE = re.compile(r"[();{}\[\]\s]|->|=>|::|^[A-Za-z_]\w{0,23}(\.[A-Za-z_]\w{0,23})+,?$")
PASCAL = re.compile(r"^[A-Z][a-z]+([A-Z][a-z0-9]*)*$")
KNOWN_PREFIX = re.compile(r"^(sk-|sk_live_|rk_live_|ghp_|gho_|ghu_|ghs_|github_pat_|glpat-|AKIA|ASIA|eyJ|xox[abpr]-|AIza|hf_|npm_)")
ALLOW = "secret-scan: allow"

SKIP_DIRS = {".git", "Binaries", "Intermediate", "Saved", "DerivedDataCache", "node_modules", "__pycache__", ".vs",
             ".pytest_cache"}
BIN_EXT = {".png", ".jpg", ".jpeg", ".gif", ".mp4", ".dll", ".exe", ".pdb", ".uasset", ".umap", ".glb", ".ttf", ".otf",
           ".zip", ".pak", ".lib", ".obj", ".bin", ".pyc", ".pdf", ".webp"}
TEXT_EXT = {".ini", ".env", ".txt", ".json", ".ps1", ".psm1", ".sh", ".bat", ".cmd", ".py", ".cs", ".cpp", ".h", ".hpp",
            ".c", ".js", ".ts", ".yml", ".yaml", ".md", ".toml", ".cfg", ".config", ".xml", ".csproj", ".props", ".gd",
            ".uproject", ".uplugin", ".log", ".csv", ".html", ".css"}


def secret_like(v):
    """Does a value look like a credential rather than an identifier or label?"""
    if KNOWN_PREFIX.match(v):
        return True
    if "://" in v:
        return False  # a URL such as http://localhost:11434 is an endpoint, not a credential
    digits = sum(ch.isdigit() for ch in v)
    letters = sum(ch.isalpha() for ch in v)
    return len(v) >= 16 and digits >= 2 and letters >= 2 and len(set(v)) >= 10


def _value(v):
    """Strip a trailing comment, separator and quotes from an assignment value."""
    v = v.strip()
    if v and v[0] in "\"'":
        q = v[0]
        end = v.find(q, 1)
        if end < 0:
            return v[1:]
        rest = v[end + 1:].strip()
        if rest and not re.match(r"^[,;]?\s*(#|//|$)", rest):
            return None  # string expression such as "a" + b, or `X="v" cmd`: the in-line match judges it
        return v[1:end]
    v = re.split(r"\s+[#;]|\s+//", v, maxsplit=1)[0].strip()
    if v.endswith(","):
        return None  # unquoted initializer or enum member (`Token = token,`, `BadToken = 0,`): code
    return v


def _kind(name):
    return "SecurityToken" if "securitytoken" in name.lower() else name


def classify_start(line):
    m = ASSIGN.match(line)
    if not m:
        return None
    name = m.group("name").rsplit(".", 1)[-1]
    if not SECRET_NAME.search(name):
        return None
    raw = m.group("v")
    if raw.startswith("=") or raw.startswith("("):  # `a ==`, `Key=(...)` tuples (the in-line match reads inside)
        return None
    v = _value(raw)
    if v is None or not v or v.lower() in LITERALS or PLACEHOLDER.match(v):
        return None
    if "securitytoken" in name.lower():
        return _kind(name)  # S4: SecurityToken= with any value fails
    quoted = bool(raw and raw[0] in "\"'")
    if not quoted and CODE.search(v):
        return None
    if not quoted and v.rstrip("?").lower() in TYPE_NAMES:
        return None  # type annotation: `var alt_key: int`
    strong = bool(STRONG_NAME.search(name))
    if not strong and KEY_ONLY.search(name) and PASCAL.match(v):
        return None
    if not strong and m.group("op") == ":" and not secret_like(v):
        return None  # data key such as "asset_key": "sanguine_furnace" or "token": "await"
    return _kind(name)


def classify_inline(line):
    for m in INLINE.finditer(line):
        name = m.group("name")
        if not SECRET_NAME.search(name):
            continue
        v = m.group("lit")
        if v.lower() in LITERALS or PLACEHOLDER.match(v):
            continue
        if STRONG_NAME.search(name) or secret_like(v):
            return _kind(name)
    return None


# Well-known credential shapes, matched anywhere in a line (bare literals, call arguments, env subscripts, curl
# headers, CLI flags), so a key needs no secret-named assignment beside it to be caught. Word-bounded and length-gated
# so ordinary words ("task-", "risk-") never match.
TOKEN_ANYWHERE = re.compile(
    r"(?<![A-Za-z0-9_-])("
    r"sk-ant-[A-Za-z0-9_-]{20,}"
    r"|sk-(?:proj-)?[A-Za-z0-9_-]{32,}"
    r"|[sr]k_live_[A-Za-z0-9]{16,}"
    r"|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}"
    r"|glpat-[A-Za-z0-9_-]{20,}"
    r"|A[KS]IA[0-9A-Z]{16}"
    r"|xox[abpr]-[A-Za-z0-9-]{10,}"
    r"|AIza[0-9A-Za-z_-]{35}"
    r"|hf_[A-Za-z0-9]{30,}"
    r"|npm_[A-Za-z0-9]{36}"
    r"|eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"
    r"|-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----"
    r")")


def classify_token(line):
    m = TOKEN_ANYWHERE.search(line)
    return ("known-token:" + m.group(1)[:7]) if m else None


def classify(line):
    """Return 'SecurityToken', the matched name, or 'known-token:<prefix>' when the line carries a secret, else None."""
    if ALLOW in line:
        return None
    return classify_start(line) or classify_inline(line) or classify_token(line)


def scan_text(text):
    """Return [(lineno, name)] for lines assigning a secret-like value."""
    return [(n, k) for n, k, _ in scan_text_full(text)[0]]


def scan_text_full(text):
    """Return ([(lineno, name, None)], allowed_count)."""
    hits, allowed = [], 0
    for n, line in enumerate(text.splitlines(), 1):
        if ALLOW in line:
            if classify_start(line) or classify_inline(line) or classify_token(line):
                allowed += 1
            continue
        k = classify(line)
        if k:
            hits.append((n, k, None))
    return hits, allowed


def decode(raw):
    """Decode file bytes to text, or return None when the file looks binary."""
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw[3:].decode("utf-8", errors="replace")
    if raw.startswith(b"\xff\xfe") or raw.startswith(b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    head = raw[:4096]
    if b"\0" not in head:
        return raw.decode("utf-8", errors="replace")
    even, odd = head[0::2], head[1::2]
    if len(head) >= 4:
        if odd.count(0) >= 0.7 * len(odd) and even.count(0) <= 0.1 * len(even):
            return raw.decode("utf-16-le", errors="replace")
        if even.count(0) >= 0.7 * len(even) and odd.count(0) <= 0.1 * len(odd):
            return raw.decode("utf-16-be", errors="replace")
    return None


def iter_files(paths):
    for p in paths:
        if os.path.isdir(p):
            for root, dirs, files in os.walk(p):
                dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
                for f in files:
                    yield os.path.join(root, f)
        else:
            yield p


def main(argv=None):
    paths = (argv if argv is not None else sys.argv[1:])
    if not paths:
        print("usage: secret_scan.py PATH [PATH...]", file=sys.stderr)
        return 2
    bad = allowed = 0
    for f in iter_files(paths):
        ext = os.path.splitext(f)[1].lower()
        if ext in BIN_EXT:
            continue
        try:
            with open(f, "rb") as fh:
                raw = fh.read()
        except OSError as e:
            print("SECRET_SCAN ERROR %s: %s" % (f, e))
            return 2
        text = decode(raw)
        if text is None:
            if ext in TEXT_EXT or os.path.basename(f).lower().startswith(".env"):
                print("SECRET_SCAN SKIP %s (binary)" % f)
            continue
        hits, al = scan_text_full(text)
        allowed += al
        for n, kind, _ in hits:
            print("%s:%d: %s=<value masked>" % (f, n, kind))
            bad += 1
    if allowed:
        print("SECRET_SCAN ALLOWED %d line(s) (secret-scan: allow)" % allowed)
    if bad:
        print("SECRET_SCAN FAIL %d line(s)" % bad)
        return 1
    print("SECRET_SCAN OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())

import json
import os
import shutil
import subprocess
import sys

import pytest
from PIL import Image

KIT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, KIT)
import evidence  # noqa: E402
import logscan  # noqa: E402
import secret_scan  # noqa: E402

CLEAN = [
    "LogWindows: Failed to load 'aqProf.dll' (GetLastError=126)\n",
    "LogWindows: Failed to load 'VtuneApi.dll' (GetLastError=126)\n",
    "LogWindows: Failed to load 'VtuneApi32e.dll' (GetLastError=126)\n",
    "LogWindows: Failed to load 'WinPixGpuCapturer.dll' (GetLastError=126)\n",
    "LogChimeraHud: Display: shot written 12345\n",
]


def rules(lines, **kw):
    return {r for _, r, _ in logscan.scan_lines(lines, **kw)}


def test_logscan_clean_run_passes():
    assert logscan.scan_lines(CLEAN) == []


@pytest.mark.parametrize("line,rule", [
    ("LogWindows: Failed to load '/Game/Maps/X.X'", "failed-to-load-asset"),
    ("LogLoad: Failed to load game mode", "failed-to-load-gamemode"),
    ("LogWindows: Failed to load 'other.dll'", "failed-to-load-other"),
    ("LogChimeraHud: Error: SHOT TIMEOUT", "chimera-error"),
    ("RealtimeMesh: Warning: oops", "realtimemesh-error"),
    ("LogRealtimeMeshCore: Error: bad", "realtimemesh-error"),
    ("Ensure condition failed: x", "ensure"),
    ("Fatal error: crash", "fatal"),
    ("LogD3D12RHI: Fatal GPU crash detected", "fatal"),  # C's rule: any `Fatal` (plan-c 3.8)
    ("LogCore: Fatal", "fatal"),
    ("UpdateTextureRegions called for 3", "update-texture-regions"),
    # verifier probe lines: unquoted Failed to load, crash banner, B T9's linker/streaming set
    ("LogPakFile: Error: Failed to load pak file", "failed-to-load-other"),
    ("LogAudio: Warning: Failed to load audio mixer platform module", "failed-to-load-other"),
    ("LogWindows: Failed to load 'Wintab32.dll' (GetLastError=126)", "failed-to-load-other"),
    ("LogWindows: Failed to load 'aqProf.exe'", "failed-to-load-other"),
    ("LogWindows: Error: === Critical error: ===", "critical-error"),
    ("LogStreaming: Error: Couldn't find file for package /Game/Maps/Arena", "linker-streaming"),
    ("LogLinker: Warning: bad import", "linker-streaming"),
    ("LogUObjectGlobals: Warning: Failed to find object 'X'", "failed-to-find"),
])
def test_logscan_fails(line, rule):
    assert rule in rules([line])


def test_logscan_editor_profile():
    ensure = ("LogOutputDevice: Error: Ensure condition failed: IsInGameThread() || IsInAsyncLoadingThread()  "
              "[File:D:\\build\\Engine\\Source\\Editor\\UnrealEd\\Public\\TickableEditorObject.h] [Line: 50]")
    vp = ("EnsureFailed: Error: Ensure condition failed: !bCheckMissingOverride || bRemoved  "
          "[File:D:\\build\\Engine\\Source\\Editor\\UnrealEd\\Private\\EditorViewportClient.cpp] [Line: 748]")
    wintab = "LogWindows: Failed to load 'Wintab32.dll' (GetLastError=126)"
    assert rules([ensure, vp, wintab]) == {"ensure", "failed-to-load-other"}
    assert rules([ensure, vp, wintab] + CLEAN, profile="editor") == set()
    assert rules(["Ensure condition failed: other  [File:X.cpp]"], profile="editor") == {"ensure"}
    assert rules(["LogWindows: Failed to load 'other.dll'"], profile="editor") == {"failed-to-load-other"}


LOOKTEST = "D:/Projects/Chimera-Unreal/ProjectChimera/LookTest/logs"


@pytest.mark.skipif(not os.path.isdir(LOOKTEST), reason="look-test logs not on this machine")
def test_logscan_looktest_game_logs_clean():
    logs = sorted(f for f in os.listdir(LOOKTEST) if f.endswith(".log") and f.startswith(("game_", "warm_")))
    assert len(logs) == 9
    for f in logs:
        with open(os.path.join(LOOKTEST, f), encoding="utf-8", errors="replace") as fh:
            lines = fh.readlines()
        assert logscan.scan_lines(lines) == [], f
        assert sum(1 for x in lines if "Failed to load '" in x and ".dll'" in x) == 4, f


def test_logscan_ignore_and_extra():
    assert rules(["Fatal error: x"], ignore=["Fatal"]) == set()
    assert rules(["hello MARK"], extra_fail=["MARK"]) == {"extra-0"}


def test_logscan_cli(tmp_path):
    ok = tmp_path / "ok.log"
    ok.write_text("".join(CLEAN))
    bad = tmp_path / "bad.log"
    bad.write_text("".join(CLEAN) + "Ensure condition failed: z\n")

    def run(p, *a):
        return subprocess.run([sys.executable, os.path.join(KIT, "logscan.py"), str(p), *a], capture_output=True, text=True)
    assert run(ok).returncode == 0
    r = run(bad)
    assert r.returncode == 1 and "ensure" in r.stdout
    assert run(bad, "--profile", "editor").returncode == 1


# Secret fixtures are assembled at runtime so this file never carries a secret-shaped line itself.
ST = "Security" + "Token"
K = "Ke" + "y"
SK = "sk-" + "ant-api03-abcdef123456"


def run_scan(*paths):
    return subprocess.run([sys.executable, os.path.join(KIT, "secret_scan.py"), *map(str, paths)],
                          capture_output=True, text=True)


def test_secret_scan_planted(tmp_path):
    f = tmp_path / "DefaultEngine.ini"
    f.write_text("[Android]\n" + ST + "=abc123SECRET\n")
    r = run_scan(f)
    assert r.returncode == 1
    assert "abc123SECRET" not in r.stdout  # value masked
    assert "SECRET_SCAN FAIL" in r.stdout


@pytest.mark.parametrize("line", [
    ST + "=0123456789ABCDEF0123456789ABCDEF",  # UE form, as in P/Config/DefaultEngine.ini:92
    "ANTHROPIC_API" + "_KEY=sk-ant-xxxx",
    "OPENAI_API" + "_KEY=sk-yyyy",
    "export GITHUB_TO" + "KEN=ghp_abc123",
    "api" + "_key=zzz",
    "secret" + "_key = qqq",
    "ApiK" + "ey=xyz",
    "  \"api" + "Key\": \"abc123\",",
    "client_sec" + "ret: s3cr3tvalue",
    "pass" + "word=hunter2",
    "const api" + "Key = \"abc123def\";",
    ST + "=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc",
    # verifier probes: typed declarations, PowerShell, env-prefixed commands, dict literals, YAML lists
    "private const string Api" + K + " = \"" + SK + "\";",
    "static readonly string api" + K + " = \"" + SK + "\";",
    "FString Api" + K + " = TEXT(\"" + SK + "\");",
    "$env:ANTHROPIC_API_" + K.upper() + " = \"" + SK + "\"",
    "$api" + K + " = '" + SK + "'",
    "ANTHROPIC_API_" + K.upper() + "=\"" + SK + "\" python run.py",
    "headers = {\"x-api-" + K.lower() + "\": \"" + SK + "\"}",
    "  - Api" + K + ": " + SK,
    "Signing" + "PrivateExponent=AbCdEf0123456789==",
    "+SecondaryEncryption" + K + "s=(Guid=\"1\",Name=\"x\"," + K + "=\"AbCd0123456789+/=\")",
    "\"github_to" + "ken\": \"ghp_" + "abcdefABCDEF0123456789\"",
    ST + "=Abc Def",
])
def test_secret_scan_flags(line):
    assert secret_scan.scan_text(line + "\n") != [], line


@pytest.mark.parametrize("line", [
    ST + "=",
    "ApiK" + "ey=",
    ST + "=<value>",
    "ApiK" + "ey=${API_KEY}",
    "ApiK" + "ey=\\n[Section]",
    "const FName& Key = Pair.Key;",
    "void Pilot(FName ViewportConfigKey=NAME_None);",
    "    ViewportConfigK" + "ey=NAME_None",
    "if (cacheKey == other)",
    "if (viewportKey === \"dc-model\") {",
    "+ActionMappings=(ActionName=\"Jump\",Key=SpaceBar)",
    "-ConsoleK" + "eys=Tilde",
    "K" + "ey=SpaceBar",
    "    this.onK" + "ey = ev => {",
    "        ke" + "y: wrapper ? void 0 : key,",
    "            const ke" + "y = \"SCRIPT|\" + (child.getAttribute(\"src\") || \"\");",
    "        ke" + "ys = [k for k in keys if k in only]",
    "            AiAvailability.NoK" + "ey =>",
    "                To" + "ken           = token,",
    "        BadTo" + "ken = 0,",
    "bUseK" + "ey=True",
    "ApiK" + "ey=abc123  ; secret-scan: allow",
    # verifier false positives: JSON data keys, type annotations, URLs
    "    \"asset_k" + "ey\": \"sanguine_furnace\",",
    "\t\"to" + "ken\": \"await\",",
    "\tvar alt_k" + "ey: int = 0",
    "    def f(self, cache_k" + "ey: str) -> None:",
    "  { id: 'ollama', k" + "ey: 'http://localhost:11434', ok: '38 ms' }",
    "if (api" + K + " != \"" + SK + "\") {",
    "const string Cache" + K + " = \"unit_definitions_v2\";",
])
def test_secret_scan_ignores(line):
    assert secret_scan.scan_text(line + "\n") == [], line


# A realistic-length key in forms with no secret-named assignment beside it (S4 verifier finding).
LONG_SK = "sk-" + "ant-api03-" + "Ab3dE5gH7jK9mN1pQ3sT5vX7zA9cE1gI3kM5oQ7sU9wY1aC3eG5"


@pytest.mark.parametrize("line", [
    "client = Anthropic(\"" + LONG_SK + "\")",
    "headers.Add(\"x-api-key\", \"" + LONG_SK + "\");",
    "Request->SetHeader(TEXT(\"x-api-key\"), TEXT(\"" + LONG_SK + "\"));",
    "os.environ[\"ANTHROPIC_API_KEY\"] = \"" + LONG_SK + "\"",
    "curl -H \"x-api-key: " + LONG_SK + "\" https://api.anthropic.com/v1/messages",
    "python run.py --api-key " + LONG_SK,
    "\"" + LONG_SK + "\",",
    "AKIA" + "ABCDEFGHIJKLMNOP is the access id",
    "-----BEGIN " + "RSA PRIVATE KEY-----",
])
def test_secret_scan_flags_known_tokens_anywhere(line):
    assert secret_scan.scan_text(line + "\n") != [], line


@pytest.mark.parametrize("line", [
    "task-ant-api03-not-a-key-because-of-the-word-boundary-before-it",
    "see docs for the sk- prefix format",
    "risk-free change; ask-ant-colony",
])
def test_secret_scan_known_token_rule_is_word_bounded(line):
    assert secret_scan.scan_text(line + "\n") == [], line


def test_secret_scan_clean_dir_and_kit_itself(tmp_path):
    d = tmp_path / "proj"
    d.mkdir()
    (d / "a.ini").write_text("[x]\nRenderer=Vulkan\n")
    r = run_scan(d)
    assert r.returncode == 0 and "SECRET_SCAN OK" in r.stdout
    r = run_scan(KIT)  # the main session scans tools/unreal-* before every R commit: the kit must pass its own gate
    assert r.returncode == 0, r.stdout


@pytest.mark.parametrize("enc,bom", [("utf-16-le", b"\xff\xfe"), ("utf-16-be", b"\xfe\xff"), ("utf-16-le", b"")])
def test_secret_scan_reads_utf16(tmp_path, enc, bom):
    """PowerShell 5.1 `>` and Out-File write UTF-16LE with a BOM; a secret in such a file must still fail."""
    f = tmp_path / "DefaultGame.ini"
    f.write_bytes(bom + ("[/Script/X]\r\n" + ST + "=0123456789ABCDEF0123456789ABCDEF\r\n").encode(enc))
    r = run_scan(tmp_path)
    assert r.returncode == 1 and "DefaultGame.ini:2: SecurityToken" in r.stdout, r.stdout
    e = tmp_path / "ps51.env"
    e.write_bytes(b"\xff\xfe" + ("Api" + K + "=" + SK + "\r\n").encode("utf-16-le"))
    r = run_scan(e)
    assert r.returncode == 1 and "ps51.env:1: Api" in r.stdout, r.stdout


def test_secret_scan_reports_skips_and_allowed(tmp_path):
    (tmp_path / "blob.ini").write_bytes(b"\x00\x01\x02\x03\x00\x00\x10\x00\x00\x00garbage")
    (tmp_path / "blob.uexp").write_bytes(b"\x00\x01\x02\x03\x00\x00\x10\x00")
    (tmp_path / "ok.ini").write_text("Api" + K + "=abc123  ; secret-scan: allow\n")
    r = run_scan(tmp_path)
    assert r.returncode == 0, r.stdout
    assert "SECRET_SCAN SKIP" in r.stdout and "blob.ini (binary)" in r.stdout and "blob.uexp" not in r.stdout
    assert "SECRET_SCAN ALLOWED 1 line(s)" in r.stdout


def mkimg(path, w=2000, h=1000, noise=False):
    os.makedirs(os.path.dirname(str(path)), exist_ok=True)
    im = Image.effect_noise((w, h), 80).convert("RGB") if noise else Image.new("RGB", (w, h), (30, 90, 160))
    im.save(path)
    return str(path)


def test_add_check_roundtrip(tmp_path):
    ev = str(tmp_path / "ev")
    j = mkimg(tmp_path / "src.jpg", 800, 400)
    r = evidence.add(ev, "a", j, "a-A8-parity.jpg", task="A8")
    assert r["committed"] and r["w_h"] == [800, 400] and len(r["sha256"]) == 64
    assert evidence.check_ev(ev) == []
    with open(os.path.join(ev, "a", "a-A8-parity.jpg"), "ab") as f:
        f.write(b"x")
    assert any("sha256 mismatch" in p for p in evidence.check_ev(ev))


def test_add_refuses_b_images_and_bad_names(tmp_path):
    ev = str(tmp_path / "ev")
    p = mkimg(tmp_path / "b.png", 100, 100)
    with pytest.raises(evidence.EvError):
        evidence.add(ev, "b", p, "b-T4b-scorecard.png")
    with pytest.raises(evidence.EvError):
        evidence.add(ev, "a", p, "BadName.png")
    r = evidence.add(ev, "b", p, "b-T4b-scorecard.png", register=True)
    assert r["committed"] is False and os.path.isabs(r["path"])
    assert evidence.check_ev(ev) == []
    os.remove(p)
    assert any("missing" in x for x in evidence.check_ev(ev))


def test_add_refuses_oversize_png(tmp_path):
    p = mkimg(tmp_path / "big.png", 1200, 900, noise=True)
    assert os.path.getsize(p) > 300 * 1024
    with pytest.raises(evidence.EvError):
        evidence.add(str(tmp_path / "ev"), "c", p, "c-C7-composite.png")


def test_phone_limits(tmp_path):
    p = mkimg(tmp_path / "big.png", 3840, 2160, noise=True)
    out = evidence.phone(p)
    assert out.endswith("-phone.jpg")
    with Image.open(out) as im:
        assert im.width <= 1600
    assert os.path.getsize(out) <= 1024 * 1024


def test_sheet_and_scorecard(tmp_path):
    imgs = [mkimg(tmp_path / ("i%d.png" % i), 640, 360) for i in range(4)]
    out = evidence.sheet(imgs, str(tmp_path / "s.jpg"), ["one"], cols=2, title="T")
    with Image.open(out) as im:
        assert im.width > 900 and im.height > 400
    spec = {"checks": [
        {"name": "(a) native sim", "verdict": "PASS", "numbers": ["1440/1440", "p95 1.2 ms"], "file": "a-A8-parity.png"},
        {"name": "(b) HUD", "verdict": "PARTIAL", "numbers": ["PASS 30/38"], "file": "scorecard_A.png"}]}
    sp = tmp_path / "spec.json"
    sp.write_text(json.dumps(spec))
    r = subprocess.run([sys.executable, os.path.join(KIT, "evidence.py"), "scorecard", str(sp), "--out", str(tmp_path / "sc.jpg")],
                       capture_output=True, text=True)
    assert r.returncode == 0 and os.path.getsize(tmp_path / "sc.jpg") > 1000


def test_scorecard_overall_needs_all_three():
    def v(*cs):
        checks = [{"name": "(%s) x" % c, "verdict": vd} for c, vd in cs]
        return evidence.overall_verdict(checks, [c["verdict"] for c in checks])
    assert v(("a", "PASS"), ("b", "PASS"), ("c", "PASS")) == "PASS"
    assert v(("a", "PASS"), ("b", "PASS")) == "INCOMPLETE"
    assert v(("a", "PASS"), ("a", "PASS"), ("c", "PASS")) == "INCOMPLETE"
    assert v(("a", "PASS"), ("b", "PARTIAL"), ("c", "PASS")) == "PARTIAL"
    assert v(("a", "PASS"), ("b", "FAIL")) == "FAIL"


def test_cli_check_exit_codes(tmp_path):
    ev = str(tmp_path / "ev")

    def run(*a):
        return subprocess.run([sys.executable, os.path.join(KIT, "evidence.py"), "--ev", ev, *a], capture_output=True, text=True)
    j = mkimg(tmp_path / "s.jpg", 300, 200)
    assert run("add", "--check", "a", j, "--name", "a-A1-shot.jpg").returncode == 0
    assert run("check").returncode == 0
    os.remove(os.path.join(ev, "a", "a-A1-shot.jpg"))
    assert run("check").returncode == 1


def test_manifest_sent(tmp_path):
    ev = str(tmp_path / "ev")
    evidence.add(ev, "a", mkimg(tmp_path / "s.jpg", 300, 200), "a-A1-shot.jpg")
    evidence.mark_sent(ev, "a", "a-A1-shot.jpg")
    assert evidence.load_manifest(ev, "a")[0]["sent_to_alec"]


def git(cwd, *a):
    subprocess.run(["git", "-C", str(cwd), *a], check=True, capture_output=True, text=True)


def init_repo(path):
    git(path.parent, "init", "-q", str(path))
    git(path, "config", "user.email", "t@t")
    git(path, "config", "user.name", "t")


@pytest.mark.skipif(not shutil.which("git"), reason="needs git")
@pytest.mark.parametrize("staged", [True, False])
def test_check_git_policy_in_repo(tmp_path, staged):
    """EV four levels below the repo top, like R/docs/unreal-move/trial-checks/evidence."""
    repo = tmp_path / "repo"
    repo.mkdir()
    init_repo(repo)
    ev = repo / "docs" / "x" / "y" / "evidence"
    assert evidence.add(str(ev), "a", mkimg(tmp_path / "s.jpg", 300, 200), "a-A8-parity.jpg", commit="x")["committed"]
    git(repo, "add", "-A")
    git(repo, "commit", "-qm", "base")
    assert evidence.check_ev(str(ev)) == []
    big = mkimg(ev / "a" / "a-A8-bigframe.png", 1200, 900, noise=True)
    assert os.path.getsize(big) > 300 * 1024
    mkimg(ev / "b" / "b-T4b-x.png", 50, 50)
    (ev / "stray.txt").write_text("x")
    if staged:
        git(repo, "add", "-A")
    pr = evidence.check_ev(str(ev))
    assert any("a/a-A8-bigframe.png" in p and "policy" in p for p in pr), pr
    assert any("(b) image b/b-T4b-x.png" in p for p in pr), pr
    assert any("not in a/manifest.json" in p for p in pr), pr
    assert any("EV root: stray.txt" in p for p in pr), pr
    r = subprocess.run([sys.executable, os.path.join(KIT, "evidence.py"), "--ev", str(ev), "check"], capture_output=True, text=True)
    assert r.returncode == 1 and "EVIDENCE CHECK FAIL" in r.stdout


@pytest.mark.skipif(not shutil.which("git"), reason="needs git")
def test_check_respects_gitignore(tmp_path):
    """A (b) image the repo ignores (as R/.gitignore ignores evidence/b/**/*.png) is fine once registered."""
    repo = tmp_path / "repo"
    repo.mkdir()
    init_repo(repo)
    (repo / ".gitignore").write_text("evidence/b/**/*.png\n")
    ev = repo / "evidence"
    p = mkimg(ev / "b" / "b-T4b-shot.png", 50, 50)
    evidence.add(str(ev), "b", p, "b-T4b-shot.png", register=True)
    assert evidence.check_ev(str(ev)) == []
    git(repo, "add", "-f", "evidence/b/b-T4b-shot.png")  # force-tracked despite the ignore rule: caught
    assert any("(b) image" in x for x in evidence.check_ev(str(ev)))


@pytest.mark.skipif(not shutil.which("git"), reason="needs git")
def test_check_flags_unlisted_ignored_files(tmp_path):
    """A (b) image written straight into EV/b, ignored by git and in no manifest, fails (as b-T0-lcd-vs-gray-4x.png did)."""
    repo = tmp_path / "repo"
    repo.mkdir()
    init_repo(repo)
    (repo / ".gitignore").write_text("evidence/b/**/*.png\nevidence/a/*.log\n")
    ev = repo / "evidence"
    mkimg(ev / "b" / "b-T0-lcd-vs-gray-4x.png", 50, 50)
    (ev / "a").mkdir(parents=True)
    (ev / "a" / "a-A3-run.log").write_text("x")
    notes = []
    pr = evidence.check_ev(str(ev), notes)
    assert any("b/b-T0-lcd-vs-gray-4x.png" in p and "no b/manifest.json row" in p for p in pr), pr
    assert any("a/a-A3-run.log" in n for n in notes), notes
    evidence.add(str(ev), "b", str(ev / "b" / "b-T0-lcd-vs-gray-4x.png"), register=True)
    evidence.add(str(ev), "a", str(ev / "a" / "a-A3-run.log"), register=True)
    notes = []
    assert evidence.check_ev(str(ev), notes) == [] and notes == []


def test_add_stores_ev_relative_path_and_check_is_portable(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    j = mkimg(tmp_path / "s.jpg", 300, 200)
    r = subprocess.run([sys.executable, os.path.join(KIT, "evidence.py"), "--ev", "./ev_cli", "add", "--check", "a", j,
                        "--name", "a-A11-contact-sheet.jpg"], capture_output=True, text=True)
    assert r.returncode == 0, r.stderr
    row = evidence.load_manifest(str(tmp_path / "ev_cli"), "a")[0]
    assert row["path"] == "a/a-A11-contact-sheet.jpg"
    # a copy of EV elsewhere (another clone) still checks clean, from any working directory
    shutil.copytree(tmp_path / "ev_cli", tmp_path / "clone" / "ev")
    monkeypatch.chdir(KIT)
    assert evidence.check_ev(str(tmp_path / "clone" / "ev")) == []

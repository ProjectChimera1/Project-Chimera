# -*- coding: utf-8 -*-
"""Turn a TRIPO_RUNBOOK.md prompt block into a Flux-ready prompt.

Tripo's image models follow instructions and obey negations. Flux does not: a diffusion
model conditions on the nouns it is given, so "no streets, no roads, no courtyards" is a
reliable way to summon streets, roads and courtyards. That is the same mechanism the
runbook already records for `covenant_transmuter` (naming a part you want absent is
stronger than the sentence excluding it) - it just bites harder here.

So this strips, rather than translates:
  - the CHECK block entirely  (Flux cannot re-read its own output)
  - every negated clause      ("no X", "nothing but Y", "NOT a Z", "rather than W")
  - FRAMING's absence list, replaced by a short positive statement of the same intent

Usage:
  python tripo_block_to_flux.py bolt_sanctum
  python tripo_block_to_flux.py bolt_sanctum --into <workflow.json>
"""
import argparse, io, json, re, sys

RUNBOOK = r"D:\tripo-input\TRIPO_RUNBOOK.md"

# What FRAMING's wall of negations is actually asking for, said positively.
POSITIVE_FRAMING = (
    "A single isolated building standing alone and complete against a plain flat mid-grey "
    "backdrop, the whole structure inside the frame and filling it, seen from an elevated "
    "three-quarter angle as an RTS camera would see it."
)

NEG = re.compile(
    r"(?:^|(?<=[,;:.\-]) *)(?:and +)?(?:"
    r"no +[a-z]|nothing +|never +|not +a +|rather than +|without +|minimal +"
    r")[^,;.]*[,;.]?",
    re.I,
)

def extract(name, text):
    m = re.search(r"^### `%s`\s*$" % re.escape(name), text, re.M)
    if not m:
        have = re.findall(r"^### `([^`]+)`", text, re.M)
        sys.exit("no block '%s'. available:\n  %s" % (name, "\n  ".join(have)))
    blk = re.search(r"```\s*\n(.*?)\n```", text[m.end():], re.S)
    if not blk:
        sys.exit("block '%s' has no fenced prompt" % name)
    return blk.group(1)

def field(block, key):
    m = re.search(r"^%s[^:]*:\s*(.*?)(?=\n[A-Z]{4,}[^:]*:|\Z)" % key, block, re.S | re.M)
    return re.sub(r"\s+", " ", m.group(1)).strip() if m else ""

def denegate(s):
    out = NEG.sub(" ", s)
    out = re.sub(r"\s*[,;]\s*(?=[,;.])", "", out)
    out = re.sub(r"\s{2,}", " ", out)
    out = re.sub(r"\s+([,;.])", r"\1", out)
    out = re.sub(r"(?:^|(?<=\. ))\s*[,;]\s*", "", out)
    return out.strip(" ,;")

# STYLE is shared boilerplate written for characters. On a building these clauses are
# dead weight at best and misleading at worst - Flux conditions on every noun it is given,
# and "facial features" on a building is a noun it will try to honour.
CHARACTER_ONLY = [
    r"stylised facial features rather than a portrait likeness[,.]?",
    r"no photographic skin or fabric rendering[,.]?",
    r"\s*against the cloth",
    r"woven wool, creased leather, stitched seams, ",
]

# Two-view sheet. This DELIBERATELY breaks the runbook's "SUBJECT comes first" rule:
# with Flux the layout instruction has to lead or it draws one building and ignores the
# panels entirely. Subject-first is a rule about instruction-following models weighting
# early content - here the composition IS the thing that must survive.
# Two framings, because they fail in opposite directions and neither wins on buildings.
#
# ORTHO  - reliably produces a GENUINELY different back, but flat head-on elevations, so
#          the roof is invisible. Correct input format for a multi-view 3D endpoint, and
#          correct for characters, whose back really is a different picture.
# ANGLED - reliably produces the RTS three-quarter with the roof as the largest surface,
#          but Flux will NOT rotate a building 180 degrees: both panels come back as the
#          same front view slightly turned. Good plate, useless as a second view.
#
# Tested 2026-09-21 on bolt_sanctum, one batch each.
TWO_VIEW_ORTHO = (
    "A two-panel turnaround reference sheet showing ONE single subject twice, at identical "
    "scale and in identical style, in two equal side-by-side panels on one plain flat mid-grey "
    "backdrop. LEFT PANEL: the front, seen straight on. RIGHT PANEL: the exact same subject seen "
    "straight on from directly behind - the rear elevation, showing everything that is on its "
    "back. Both panels show the complete subject at the same size. THE SUBJECT: "
)
TWO_VIEW_ANGLED = (
    "Two equal side-by-side panels on one plain flat mid-grey backdrop, showing ONE single "
    "building twice at identical scale, identical style and identical lighting. LEFT PANEL: the "
    "building seen from the front from a high vantage point looking DOWN on it, so the whole "
    "front slope of its roof is clearly visible along with its front wall. RIGHT PANEL: the exact "
    "same building turned around, seen from behind from the same high vantage point looking DOWN "
    "on it, so the rear slope of its roof and its back wall are clearly visible. In both panels "
    "the viewpoint is well above the building and angled down onto it, the raised three-quarter "
    "view of an isometric strategy game, with the roof the largest visible surface. THE BUILDING: "
)

def build(name, twoview=False, angled=False):
    text = io.open(RUNBOOK, encoding="utf-8").read()
    b = extract(name, text)
    subject = denegate(field(b, "SUBJECT").split(":", 1)[-1].strip())
    style   = denegate(field(b, "STYLE"))
    palette = denegate(field(b, "PALETTE"))
    if "this is the building" in b:
        for pat in CHARACTER_ONLY:
            style = re.sub(pat, "", style, flags=re.I)
            palette = re.sub(r"cloth,?\s*", "", palette, flags=re.I)
        style = re.sub(r"\s{2,}", " ", style).replace(" ,", ",").strip(" ,")
        palette = re.sub(r"\s{2,}", " ", palette).strip(" ,")
    if twoview:
        head = TWO_VIEW_ANGLED if angled else TWO_VIEW_ORTHO
        parts = [head + subject, style, palette]
    else:
        parts = [subject, POSITIVE_FRAMING, style, palette]
    parts = [p for p in parts if p]
    prompt = " ".join(parts)
    prompt = re.sub(r"\s{2,}", " ", prompt).strip()
    if not prompt.endswith("."):
        prompt += "."
    return prompt

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("asset")
    ap.add_argument("--twoview", action="store_true",
                    help="front+back turnaround sheet for multi-view 3D input")
    ap.add_argument("--angled", action="store_true",
                    help="RTS three-quarter framing (roof visible, but back view unreliable)")
    ap.add_argument("--into", help="workflow .json to write the prompt into (node titled CHIMERA_PROMPT)")
    a = ap.parse_args()
    p = build(a.asset, twoview=a.twoview, angled=a.angled)
    words = len(p.split())
    print(p)
    print("\n--- %d words, ~%d T5 tokens (Flux caps at 512) ---" % (words, round(words * 1.35)))
    if words * 1.35 > 512:
        print("!! OVER THE CAP - the tail will be silently dropped. Shorten SUBJECT first.")
    if a.into:
        w = json.load(io.open(a.into, encoding="utf-8"))
        n = next(x for x in w["nodes"] if x.get("title") == "CHIMERA_PROMPT")
        n["widgets_values"] = [p]
        io.open(a.into, "w", encoding="utf-8").write(json.dumps(w, indent=2))
        print("\nwrote prompt into", a.into)

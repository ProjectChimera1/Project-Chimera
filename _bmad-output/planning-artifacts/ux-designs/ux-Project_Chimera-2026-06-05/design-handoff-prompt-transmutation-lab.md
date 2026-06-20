# Design Handoff Prompt — Project Chimera UI · "THE TRANSMUTATION LAB"

**Producer:** Google Stitch (https://stitch.withgoogle.com) — emits screen mockups (+ exportable HTML/CSS).
**Identity source:** `docs/world-theme-bible.md` · **Reference wall:** `docs/ui-reference-wall.html`
**Supersedes the mood of:** `design-handoff-prompt-retheme-alchemy.md` (same idea, now grounded in the bible).

**How to use:**
1. Paste **Block 0 (Style System)** first — it establishes the look. Let Stitch generate the component kit.
2. Then paste each **Screen Block** in its own generation (or its own Stitch project), reusing Block 0's theme.
3. Save everything Stitch returns into this folder. Give feedback, then we run a **Claude Design** pass to
   refine the winning screens into high-fidelity HTML.

> **Set expectations (important):** Stitch is an AI UI generator with its own defaults — it will *drift
> toward generic* unless pushed. The anti-reference list and the material language below are there to push
> it. Expect 2–3 iterations re-emphasizing "engraved brass on charcoal, alchemical sigils — NOT neon, NOT
> glassmorphism." Stitch gives you **layout + identity gist**; the real engraved texture and motion arrive
> later in the Godot execution. The goal of this pass is a fast read on whether the direction sings.

---

## Block 0 — Style System  *(paste first)*

> You are designing the UI for **Project Chimera**, a PC-desktop **real-time-strategy (RTS) creation
> platform** — both a polished RTS game and a Warcraft III World Editor–class tool for building custom
> games without code. It ships as a premium title on Steam.
>
> **The identity — "The Transmutation Lab."** This is the instrument panel of a master transmuter's
> **bio-alchemical workshop**. In this tool you do not *build* units — you **transmute** raw matter and
> living parts into chimerical war-beasts. The UI must feel like an alchemist's ledger and laboratory
> console: learned, precise, weighty, a little uncanny. Warm engraved metal framing cold, living light.
>
> **Tone:** solemn & scholarly, with a thread of the uncanny. Confident and builder-friendly, never cute,
> never grimdark, never gamer-neon. Think *Fullmetal Alchemist* transmutation circles meets a 17th-century
> engraved emblem plate — rebuilt as a clean, legible dark UI.
>
> **Platform & canvas:** Desktop only (Windows primary). 16:9, designed at 1920×1080, scales cleanly to
> 1440p/4K with a UI-scale setting. Input is **keyboard + mouse** — design hover states, right-click
> context, drag, and hotkey-glyph hints. No touch, controller, or mobile.
>
> **Visual identity (hold these firm):**
> - **Dark primary.** Charcoal/slate panel surfaces. Substrate `#14161A`, panel `#20242B`, raised `#2A2F38`.
>   Parchment-warm text `#ECE6D8`, muted `#9A958A`.
> - **Brass is the structural accent** `#C9A86A` (bright edge `#E3C887`, deep `#8A6E3C`): render it as
>   **thin engraved line borders, fine inlaid rules, and filigree at panel corners** — read as etched metal
>   and inked parchment, NOT glossy glass, NOT neon glow. Brass marks structure and interactive/selected.
> - **Verdigris-teal** `#4FB39A` for **active / in-progress / valid** states. **Bioluminescent green**
>   `#7FE3A1` as a rare "this is alive" highlight only.
> - **Crimson** `#C8503C` is **DANGER-ONLY**, always paired with an icon — never a general accent.
> - **Faction/team colors (saturated blue vs red) are reserved** for in-world unit ownership (player tags,
>   minimap dots). UI chrome must be clearly distinct and must never read as a team color.
> - **Motif — alchemical sigil geometry:** concentric rings, fine radial arrays, runic/glyph accents,
>   transmutation circles — used **sparingly and structurally** (frames, dividers, loaders, progress), as
>   suggestion, never decoration overload.
> - **Iconography:** engraved **alchemical-emblem** style — geometric line emblems that look etched into
>   metal (apothecary/alchemy feel) while staying instantly readable as their function.
> - **Typography:** a refined **engraved / antique serif** or "old scientific-instrument label" face for
>   titles and section heads (e.g. *Cinzel* or *Spectral*); a clean geometric **sans** for UI/body
>   (e.g. *Inter*); a **monospace** for all numeric readouts — resources, timers, stats (e.g. *JetBrains
>   Mono*). The result reads like an alchemist's ledger, precise and learned.
> - **Material story:** warm engraved brass against cold bioluminescent glow on charcoal. A subtle
>   aged/patina texture is welcome only where it won't hurt legibility.
>
> **Microcopy tone:** measured, declarative, a touch arcane. Examples: complete → *"Array stable. The
> specimen holds."*; empty → *"The crucible is empty. Begin the Work."*; error → *"The formula will not
> bind."*
>
> **Thematic vocabulary** (use where it doesn't hurt clarity; keep raw stat names plain): Generate/Create →
> **Transmute**; a unit → a **specimen / chimera**; production building → **Athanor**; the "complete/valid"
> badge → a **sealed array** stamp; the Edit↔Play toggle → **Dormant ↔ Animate**.
>
> **Hard constraints:**
> - **Clarity over a busy, zoomed-out 3D battlefield — legibility ALWAYS wins.** The alchemy is *flavor on
>   a clean structure*, never clutter that fights the numbers.
> - Must rebuild as **flat Godot Control-node panels** — engraved borders, flat fills, line-emblem icons,
>   sigil accents. No full-screen blur, no heavy live effects.
> - **WCAG AA** contrast on dark; support UI scaling; **never encode meaning by color alone** (pair with
>   icon/label); keep any team colors colorblind-safe.
>
> **Anti-references — do NOT produce:**
> - Generic dark-fantasy / Dark-Souls-clone ornate chrome.
> - Neon sci-fi / hologram-blue "FUI" HUDs.
> - Glassmorphism, soft gradients, default rounded-pill cards, indigo/violet accents — the safe AI default.
>
> **Deliver a reusable component kit**, dark + engraved-brass styling, one warm accent: engraved
> **panel/card**; **button** (primary / secondary / ghost + disabled); **icon button**; **slider with
> numeric input**; **dropdown**; **tab bar**; **tooltip**; **resource/stat readout chip** (mono numerals);
> **progress bar** (reads as a filling alchemical bar or ring); **list row**; **modal/dialog**;
> **toast/alert banner**; the **sealed-array validation stamp**; and a **transmutation-circle frame** element.

---

## Screen Block 1 — In-game RTS HUD  *(most-seen surface — do this first)*

> Design the **in-match HUD** as a non-diegetic flat overlay on a 3D battlefield (angled top-down camera,
> 500–2000 units possible). Dark, engraved-brass chrome; reserve saturated blue/red for team identity only.
>
> - **Top bar:** two resource counters — **Ore** and **Crystal**, each an engraved-emblem icon + amount in
>   **mono** numerals; a **supply** readout (e.g. 12/20); a game clock. Unobtrusive, precise, ledger-like.
> - **Bottom-left — Selection & Command card:** selected-unit portrait(s) in an engraved frame + a
>   **command grid** (Move, Attack-Move, Stop, Hold, Patrol, abilities), each with a **hotkey glyph** in the
>   corner. When an **Athanor** (production building) is selected, show its trainable specimens with cost,
>   build-time, a **progress bar** (verdigris fill), and a `[need: …]` prerequisite tag on locked items.
> - **Bottom-right — Minimap:** square, fog-of-war (unexplored / explored / visible), team-color unit dots,
>   building markers, camera-viewport rectangle — **framed by a fine concentric-ring / transmutation-circle
>   border**.
> - **Alerts:** transient toast/banner area ("Under attack!"; a centered amber "Waiting for peer…" stall
>   banner).
> - Keep the **center clear**. Show selected-count and control-group tabs (1–9). Provide hover + selected
>   states. Brass = structure/selection; verdigris = in-progress; crimson (icon-paired) = danger only.
>
> **Secondary (same style, later):** victory/defeat score card (kills / built / ore mined / duration);
> pause menu.

---

## Screen Block 2 — The Transmutation Bench (Unit Card Editor)  *(headline differentiator + signature moment)*

> Design the **in-app unit-creation editor** — a WC3-World-Editor-class tool living in the same app. It must
> feel powerful but approachable, with an instant **Dormant ↔ Animate** (edit ↔ playtest) toggle.
>
> **Editor shell:** top toolbar with the prominent **Dormant / Animate** toggle, tool groups (Terrain,
> Entities, Resources, Laws/Triggers, Win Conditions, **Transmute**), undo/redo, save/publish. A dockable
> tool palette; the 3D world fills the center. Dark, engraved, faceted panels.
>
> **Hero panel — the consolidated bench (one entity, one view — explicitly NOT scattered across tabs):**
> - **Left:** a **live rotating 3D model preview set inside a glass specimen-vat frame** (cold inner glow),
>   with buttons to change model/icon.
> - **Right:** grouped sections — **Combat** (HP, Attack, Range, Armor, Speed as sliders + numeric input,
>   each min/max), **Economy** (costs, build time), **Abilities** (chips/list, add from a library), and a
>   **Hero** toggle revealing leveling/XP/ultimate fields. A **base-form picker** ("Start from Footman /
>   Archer / Worker"), a **compare-to-specimen** side-by-side stat view, and the **sealed-array validation
>   stamp** (brass + verdigris completed transmutation-circle) when the specimen is valid. Tooltip on every
>   field.
> - **Progressive disclosure:** a **Simple ↔ Advanced** toggle — Simple = presets/dropdowns/sliders;
>   Advanced = every field + a **raw JSON** escape hatch. Show both states.
>
> **The signature surface — the Transmute control:** the AI-generate / create action, framed by a
> **transmutation-circle motif** — concentric brass rings around the action, with room for a bioluminescent
> "charge" state. (Static mock is fine; the animation is built later in-engine.) Show its **resting** and
> **ready** states.
>
> Dark theme, engraved-brass chrome, verdigris for the active tool and the selected field.

---

## Screen Blocks 3–4 — reuse the originals, re-skinned  *(optional, after 1–2 land)*

> The **Shell & Menus** and **Content Browser** layouts from `design-handoff-prompt.md` are still valid —
> re-run them under Block 0 so they inherit the Transmutation Lab look. For the title screen, the "Chimera"
> wordmark sits over a subtle transmutation-circle emblem on an arcane-workshop backdrop (not a generic
> vista).

---

## Iteration tips for Stitch

- If output drifts generic, re-paste this line: *"Re-skin: thin engraved brass line-borders and alchemical
  sigil accents on charcoal panels; antique-serif titles, mono numerals; NO neon, NO glassmorphism, NO
  rounded pills."*
- Steer the palette using the exact hex above in Stitch's theme controls.
- Push the **minimap ring**, the **sealed-array stamp**, and the **transmutation-circle Transmute frame** —
  those three motifs carry most of the identity.
- Keep numerals monospace everywhere; it's a cheap, strong "instrument" signal.

## After Stitch returns

Save outputs here, mark the screens that work, and note what drifted. We then run the **Claude Design** pass
to take the winning screens to high fidelity, and reconcile the result into the `DESIGN.md` token tables.

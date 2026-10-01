# Plan B: Match HUD board 3.1a in native C++ Slate, matched to the mockup by screenshot

Trial check (b), 2026-10-01, revision 2 (after the API and proof critiques; see the Review log at the end). The plan of record for implementer agents.
Inputs: research `r4-hud-board-3.1-spec.md` (r4), `r5-slate-pixel-faithful.md` (r5), the Unreal Spec (Non-negotiables, How it is built) and `HANDOFF.md`.
Short names: **R** = `D:/Projects/Project_Chimera`, **U** = `D:/Projects/Chimera-Unreal` (a local git repo, no remote), **H** = `U/ChimeraHud` (new), **P** = `U/ProjectChimera`,
**E** = `D:/Epic Games/UE_5.8/Engine/Source/Runtime`, **T** = `R/tools/unreal-hud` (new, canonical tooling), **LOCK** = `bash D:/Projects/Chimera-Unreal/ue_lock.sh`.

## 0. What "done" means, in one paragraph

Board **3.1a "HUD · early game"** (the only board coded 3.1, r4 §1) is rebuilt in pure C++ Slate in a new sibling project ChimeraHud. The HUD
reads one plain state struct filled with the 3.1a fixture data and writes nothing. An in-process capture takes the final frame,
UI included, at exactly 1920x1080, and saves it as a PNG. A Python comparator scores it against a **grayscale re-render of the mockup** in two pairs:
(A) the HUD over flat `#14161A`, and (B) the HUD over the mockup's own world image, which Slate draws as a 1:1 backdrop so that only the UI differs.
Scoring covers 38 named regions. Fixed physical gates catch layout, size, colour and shape errors, and gates calibrated against
controlled perturbations (Chromium variants plus a model of Slate's own text rasteriser) allow for text and vector anti-aliasing. The check passes when both pairs
pass every gate, two consecutive captures are identical, an Opus reviewer has looked at the result, and Alec has the scorecard PNGs on his phone.

## 1. Facts the plan rests on (re-verified today unless marked)

| # | Fact | Evidence |
|---|---|---|
| F1 | UI-inclusive capture: `FScreenshotRequest::RequestScreenshot(Filename, bShowUI, bAddFilenameSuffix, bHdr=false, FIntRect, bRestrictToGameViewport)`. `HighResShot` excludes UI | E/Engine/Public/UnrealClient.h:219; E/Engine/Private/GameViewportClient.cpp:2380-2413; r5 §4 |
| F2 | `OnScreenshotRequestProcessed` is broadcast **after** the `if (bScreenshotSuccessful)` block, so it also fires when nothing was written | GameViewportClient.cpp:2539, :2583 |
| F3 | A path containing `/` is used verbatim; otherwise it goes to GameScreenshotSaveDirectory | E/Engine/Private/UnrealClient.cpp:304-306 |
| F4 | `AddViewportWidgetContent` lands inside `SGameLayerManager`'s `SDPIScaler`. `ScaleToFit` = `min(W/DesignX, H/DesignY)` x `ApplicationScale` (config properties) | E/Engine/Private/Slate/SGameLayerManager.cpp:113-115, 467-497; E/Engine/Private/UserInterfaceSettings.cpp:109, 155-156; UserInterfaceSettings.h:167-168, 200 |
| F5 | This machine: display scale 100% (AppliedDPI 96), primary screen 1920x1080 (one measurement; Parsec can change it, so every run re-checks) | measured 2026-10-01; tools/unreal-looktest/run_fps.ps1:75-76 (parsecd) |
| F6 | Fonts: `FSlateFontInfo(TSharedPtr<const FCompositeFont>, Size, TypefaceName)`; `FTypeface::AppendFont(name, file, hinting, policy)`; RenderDPI 96, so **Size = CSS px x 0.75** | E/SlateCore/Public/Fonts/SlateFontInfo.h:15, 227, 245-275; CompositeFont.h:369; r5 §1.2 |
| F7 | **Kerning trap**: `Auto` shaping = KerningOnly for LTR; HarfBuzz gets `kern` only if `FT_HAS_KERNING(face) || LetterSpacing != 0`; the three fonts have GPOS kerning only. Shaping is an **`STextBlock` argument** (no field in `FSlateFontInfo`); the cvar `Slate.DefaultTextShapingMethod` (2 = FullShaping) sets the default | FontCache.h:51-62; SlateTextShaper.cpp:713-736; STextBlock.h:142; SlateFontInfo.h (no shaping member); FontCache.cpp:57-60, 107-110 |
| F8 | **Glyph advances are rounded to whole px per glyph** (`int16`). JetBrains Mono advance is 600/1000 em (hmtx, checked today), so 11 px rate glyphs are 6.6 px in Chromium and 7 in Slate: chip widths 100/84/84 instead of 98.81/83.2/83.2, chip x **8/112/200/288 vs Chromium 8/111/198/285**; the 46-glyph stat line is 322 instead of 331.2 px | SlateTextShaper.cpp:476, :797; r4 table at r4:134-137 |
| F9 | `FSlateFontMeasure::Measure(FStringView, FSlateFontInfo, float FontScale)` exists; at FontScale 16 advances are quantised to 1/16 px | E/SlateCore/Public/Fonts/FontMeasure.h:29; r5 §5.1 |
| F10 | `FSlateColorBrush(FColor)` reinterprets bytes as linear. Always `FLinearColor::FromSRGBColor` | SlateColorBrush.h:30; r5 §2.1 |
| F11 | Rounded box shader: the **fill** edge uses smoothstep spread 0.5 (exactly 1/0 at pixel centres ±0.5, crisp on integer edges); only the **outline** band is soft (spread 1, 84%/16%). Default outline is width 0, transparent | Engine/Shaders/Private/SlateShaderCommon.ush:100-118; SlateBrush.h:134-138 |
| F12 | `SWidget` RenderOpacity becomes `FWidgetStyle::BlendOpacity`, which multiplies **each draw element's** alpha (not a flattened group like CSS `opacity`) | E/SlateCore/Private/Widgets/SWidget.cpp:1489-1490 |
| F13 | `MakeGradient` with **`Orient_Vertical` varies colour along X**; stop positions are **local pixels** (`StartPt.X += Position.X`); interpolation is in linear light | ElementBatcher.cpp:1737-1797; DrawElementTypes.h:180; r5 §2.3 |
| F14 | Runtime SVG `FSlateVectorImageBrush` + nanosvg; PNG via `FSlateDynamicImageBrush`; procedural `CreateWithImageData(name, size, BGRA8 sRGB)` | SlateImageBrush.h:85-93; SlateSVGRasterizer.cpp:31; SlateDynamicImageBrush.h:69, 90 |
| F15 | URL `?game=` overrides the map's game mode | E/Engine/Private/GameInstance.cpp:1515-1537 |
| F16 | `RequestExitWithStatus(false, N)` posts WM_QUIT but the process returns GuardedMain's init ErrorLevel (0). Only `Force=true` calls `TerminateProcess(..., N)` (after `GLog->Flush()`) | E/Core/Private/Windows/WindowsPlatformMisc.cpp:1474-1521; E/Launch/Private/Launch.cpp:146-204; LaunchWindows.cpp:115-138 |
| F17 | `GShaderCompilingManager` can be null (created conditionally, nulled at exit); `IsCompiling()`; `FAssetCompilingManager::Get().GetNumRemainingAssets()` | ShaderCompiler.cpp:634; LaunchEngineLoop.cpp:3251, 3259, 6964; ShaderCompiler.h:1167; E/Engine/Public/AssetCompilingManager.h:44, 64 |
| F18 | **`-game -fullscreen -ResX=1920 -ResY=1080` gives a 1920x1080 viewport in mode `WindowedFullscreen` on this PC** (9/9 look-test logs), so the size follows the desktop. Git Bash needs `MSYS_NO_PATHCONV=1`; `-ABSLOG` must end in `.log` | P/LookTest/logs/game_*.log, warm_*.log; SceneViewport.cpp:1968-1977; run_fps.ps1:119-130 |
| F19 | Every clean run logs 4 `LogWindows: Failed to load '<profiler>.dll'` lines (aqProf, VtuneApi, VtuneApi32e, WinPixGpuCapturer) | P/LookTest/logs/game_A_noLumen_r1.log |
| F20 | Back buffer default is 10-bit (`r.DefaultBackBufferPixelFormat=4`); readback requantises 1010102 to 8-bit, which round-trips 8-bit values exactly | E/Renderer/Private/SceneTextures.cpp:71-80; E/RHI/Public/RHISurfaceDataConversion.h:126-144 |
| F21 | With `BuildSettingsVersion.V7` the legacy public/parent include paths are off: the module root is not an include path unless added. The engine's own TP_ThirdPerson template adds its folders to `PublicIncludePaths` | ModuleRules.cs:1473-1487; UEBuildModuleCPP.cs:438-475; UhtSession.cs:330-356; Templates/TP_ThirdPerson/Source/TP_ThirdPerson/TP_ThirdPerson.Build.cs |
| F22 | Editor defaults: monitor content directories ON, auto-create assets ON, for `/Game/`. Loose TTF/SVG/PNG under `Content/` become .uasset files if the editor ever opens the project | Engine/Source/Editor/UnrealEd/Private/Settings/SettingsClasses.cpp:253-264 |
| F23 | **U is a git repo** since 2026-10-01 00:56 (30b6744, 9f13ed6), no remote; `.gitignore` ignores `**/Content/` and build output but not a `HudRef/` folder. P's committed `DefaultEngine.ini` carries an AndroidFileServer `SecurityToken` | `git -C U log`, `U/.gitignore`, P/Config/DefaultEngine.ini:92 |
| F24 | **Third-party pixels already sit untracked in R**: `research/hud-ref/` holds `world-backdrop-bf-village.jpg` and 4 board PNGs. `git status --porcelain` hides them behind one `?? docs/unreal-move/trial-checks/` line; `-uall` lists all 5 | git status today; docs/ui-redesign/README.md:5-7, :32 |
| F25 | The rope bands cover every panel top border: no column in x 0-255 (rows 854-875), 256-1619 (894-915) or 1621-1919 (854-876) is rope-free in the reference | measured today on board-3.1a-hud-only-on-14161A (e.g. x=20: rows 861/862/867/868 carry rope; x=900: rows 901/902/907/908) |
| F26 | `Match.dc.html` loads the fonts from Google with `display=swap` | docs/ui-redesign/Match.dc.html:12-13 |
| F27 | Python: numpy 2.4.4, Pillow 12.2 (FreeType 2.14.3, no raqm), scipy 1.17.1, fontTools 4.63; no scikit-image, OpenCV, freetype-py or uharfbuzz. playwright-cli 0.1.21 has `run-code`, `route`, `--config` | checked today |
| F28 | `ue_lock.sh`: mkdir lock, owner `pid=$$`, waits up to 360 min then exit 75; it does **not** export any held-marker, so a nested call would wait on itself | U/ue_lock.sh:13-56 |
| F29 | **The existing reference PNGs have LCD (subpixel) text**: on the unit name 961 of 1341 ink pixels sit > 12/255 off the bg-to-fg line; the toast is clean. Slate is grayscale, so the reference is re-rendered with `--disable-lcd-text --font-render-hinting=none` | measured 2026-10-01 on the r4 PNGs; r5 §5 row 1 |
| F30 | At t = 3.0 s the toast is at full opacity (visible window 5%-70% of 11 s) and the reveals are finished | r4:56-58; JSON `keyframes.hudFade` |

UNVERIFIED (each has a check below): nanosvg anti-aliasing parity with Skia (T4a route test); that fractional Slate layout positions reach the element batcher unrounded and snap like Chromium's
(T4a measures chip x); that `FSlateFontMeasure` honours the shaping cvar (T4a kern check); the first-run shader compile time of a new project (T3 warm-up); whether Chromium's
`--font-render-hinting` changes anything on Windows; Windows HDR state (G6 and T3's blend test).

## 2. Design

### 2.1 Project ChimeraHud (sibling, same Blank C++ shape; r3 §6.3)
- `H/ChimeraHud.uproject`: module `ChimeraHud` (Runtime, Default), same `EngineAssociation` as P. No plugins (T9, optional, adds `PCGBiomeSample`).
- `Source/ChimeraHud.Target.cs`, `ChimeraHudEditor.Target.cs`: copies of P's (V7, `Unreal5_8`) with names changed. `Source/ChimeraHud/ChimeraHud.{h,cpp}` with
  `IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, ChimeraHud, "ChimeraHud")` (as P/Source/ProjectChimera/ProjectChimera.cpp).
  `ChimeraHud.Build.cs`: Public `Core, CoreUObject, Engine, InputCore`; Private `Slate, SlateCore`; **`PublicIncludePaths.Add(ModuleDirectory);`** (F21) so `#include "Ui/ChimeraUi.h"` and UHT's gen.cpp includes resolve from subfolders.
- `Config/DefaultEngine.ini`: P's renderer block verbatim (shared DDC keys), plus `[/Script/Engine.UserInterfaceSettings] UIScaleRule=ScaleToFit, DesignScreenSize=(X=1920,Y=1080),
  ApplicationScale=1.0, bAllowHighDPIInGameMode=False`; `[/Script/EngineSettings.GameMapsSettings] GameDefaultMap=/Engine/Maps/Entry, GlobalDefaultGameMode=/Script/ChimeraHud.ChimeraHudGameMode`;
  `[ConsoleVariables] Slate.DefaultTextShapingMethod=2` (F7). `DefaultGame.ini` gets a new ProjectID GUID.
- Source layout (one file pair per panel): `Game/ChimeraHudGameMode` (engine default pawn; HUDClass `AChimeraHudActor`), `Game/ChimeraHudActor` (AHUD: adds/removes the root widget),
  `Game/ChimeraHudShot` (capture automation, run log), `Ui/ChimeraUi.{h,cpp}` (tokens, `Hex()`, `Text(style, text)`, brush factory, `Load*` file loaders), `Ui/ChimeraBakes.{h,cpp}` (CPU bakes),
  `Ui/ChimeraHudState.h` (`FChimeraHudState` + `MakeBoard31aState()` from `Match.dc.html:610, 698`), `Ui/SChimeraMatchHud` (root), `Ui/Panels/{SChimeraTopStrip, SChimeraToastStack,
  SChimeraMinimapPanel, SChimeraGroupTabs, SChimeraSelectionPanel, SChimeraCommandCard, SChimeraWorldOverlays, SChimeraOrnamentFrame}`, `Ui/Widgets/{SChimeraText, SChimeraKeycap, SChimeraIcon, SChimeraBakedImage}`.
- Runtime files live in **`H/HudData/`** (not `Content/`, F22), resolved from `FPaths::ProjectDir()`: `Fonts/{Inter,Cinzel,JetBrainsMono}/*.ttf` (from `hud-ref/fonts`, OFL),
  `Icons/*.svg`, `Ornaments/*.svg` (and `--png` variants) generated by `T/make_svgs.py` with a sha256 manifest `HudData/manifest.json`, and `Placeholders/{minimap_photo.png, portrait_figure.png}`.
- Build: `T/hud_build.sh` = `LOCK "D:/Epic Games/UE_5.8/Engine/Build/BatchFiles/Build.bat" ChimeraHudEditor Win64 Development -Project="$H/ChimeraHud.uproject" -WaitMutex` (about 95 s, r3 §6.1).
  `-game` runs use `UnrealEditor.exe` on the Editor target, as the look test did.
- Version control: H is versioned **in U** (F23). T3 adds `**/HudRef/` and `**/HudData/Placeholders/` to `U/.gitignore`. Checkpoint commits in U name explicit paths
  (`ChimeraHud/{ChimeraHud.uproject,Config,Source,HudData/Fonts,HudData/Icons,HudData/Ornaments,HudData/manifest.json}`, `.gitignore`), never `git add -A`.
  U has no remote, so `T/sync_src.sh` mirrors `H/{*.uproject, Config, Source}` into `T/ChimeraHud-src/` as the off-machine copy; it redacts `SecurityToken=`/`*Key=` values, and `--check` fails if one appears unredacted.

### 2.2 DPI: 1 Slate unit = 1 px at 1920x1080, proven not assumed
ScaleToFit gives 1.0 at 1920x1080 (F4) and the platform scale is 1.0 (F5, `bAllowHighDPIInGameMode=False`). The root widget logs once from `OnPaint`:
`LogChimeraHud: geometry scale=<AllottedGeometry.Scale> local=<W>x<H> viewport=<W>x<H>`; the shot script requires `scale=1.000 local=1920x1080`.
`-HudUiScale=<f>` sets `GetMutableDefault<UUserInterfaceSettings>()->ApplicationScale` before the widget is added (README's 80-150% hook; T8 takes one ungated shot at 0.8).

### 2.3 Text recipe (r5 §1, F6-F9)
One `FStandaloneCompositeFont` per family, created once, typefaces `"400"`..`"700"` from the static TTFs. Every text element is built by **`ChimeraUi::Text(style, text)`**, the only
place text widgets are constructed (grep gate in T10): Size = px x 0.75, `LetterSpacing = 1` (switches GPOS kerning on, adds 0 px), colour, and shaping FullShaping (argument
and cvar, F7). The 13 styles of r4 §6 are an `EChimeraText` enum; hinting is one global choice measured in T4a (start: None).
**Default text widget: `SChimeraText`** (an `SLeafWidget`, r5 §5.1 extended). It measures prefix widths with `FSlateFontMeasure::Measure(prefix, font, 16)/16` (cached per string),
places each glyph with one `MakeText` at `round(x_i)` (error ≤ 0.5 px, non-cumulative), and **returns the exact fractional advance sum as its desired width**, so flex rows land
on Chromium's LayoutUnit positions (chips at 110.81/198.02/285.22, snapped to 111/198/285) instead of drifting +3 px (F8). `-HudText=block` switches to plain `STextBlock` for the
T4a route test only. Line boxes: `SBox(HeightOverride=line-height, VAlign_Center)` plus **one per-style** Y nudge (≤ 2 px, measured reason in a comment); per-element nudges are forbidden.
Strings are authored as the mockup renders them (stat line single spaces around dots, r4 §5.6; Cinzel needs no transform).

### 2.4 Brush vocabulary (r5 §2, F10-F14)
- Colours only through `ChimeraUi::Hex(0xRRGGBB, a)` (FromSRGBColor). Grep gate: no `FSlateColorBrush(FColor` in Source.
- Zero-radius boxes and 1 px hairlines: nested `SBorder`s over `FSlateColorBrush(FLinearColor)` (crisp, pixel-snapped). Per-side borders use padding.
- Keycaps (radius 2-3, 1 px sides, 2 px bottom): an outer **fill-only** `FSlateRoundedBoxBrush` in `#5C5446` and an inner fill-only rounded box inset (1,1,1,2) (F11: fill edges are crisp).
  Rounded boxes are built only by `ChimeraUi::KeycapBrush()` with the fill + radius form; the outline constructor `(Fill, Radius, OutlineColor, Width)` is never used (T10 grep + review).
- **Locked buttons (CSS `opacity .55` on the whole button):** no `SetRenderOpacity` (F12). Every colour inside is pre-composited over the panel `#1C1F25` (`c' = .55c + .45·#1C1F25`,
  sRGB bytes) and drawn opaque: fill `#181A1F`, border `#2D3138` (r4 §5.7), keycap border and fill, key letter, icon tint, lock tint, cost text. Because blending happens in the
  gamma-encoded back buffer (T3 blend test), opaque pre-composited colours with normal glyph/icon AA reproduce CSS group opacity exactly.
- Laser head: `MakeGradient(..., Stops, EOrientation::Orient_Vertical)` (colour varies along X, F13), `FSlateGradientStop.Position = FVector2f(px_along_head, 0)` in the head's local pixels,
  16 stops pre-sampled in sRGB with alpha pre-multiplied as CSS does.
- **CPU bakes** (`ChimeraBakes::*` returning `CreateWithImageData` brushes, sRGB maths, 4x4 supersampling), each built in the task of its first consumer:
  box shadow outer and inset (CSS blur b → Gaussian σ = b/2; minimap plate `0 3px 6px rgba(0,0,0,.4)`, map inset `0 2px 6px rgba(0,0,0,.6)`) and radial gradient (CSS `circle at x% y%`,
  farthest-corner; minimap fog) and the ring set (selection ring 38x16, 3 px + 2 px outer + 1 px inset, r4 §5.2) and the 6 px ringed minimap nodes, all in T5; the vat glow (radial) and the dashed border
  (Chromium dash arithmetic, empty slot V, r5 §2.6) in T6. The 1 px bevels on the plate are plain lines.

### 2.5 Icons and ornaments
`T/make_svgs.py` writes one SVG per icon (18) from `board-3.1-elements.json` `iconsUsed[].markup` (`currentColor` → white, tinted at draw time), the rope tile (16x8), 4 sigil centre marks,
an index JSON and `HudData/manifest.json` (sha256 of every generated file). Default route: runtime `FSlateVectorImageBrush` (resolution-independent). Fallback (`-HudIcons=png`, chosen by
numbers in T4a): `make_svgs.py --png` rasterises the same SVGs with Chromium at the exact on-screen sizes (12/14/16/24, sigils 14); built from our sources, never cropped from a reference.
Rope strips: `SChimeraOrnamentFrame` paints whole 16x8 tiles with `MakeBox`, one clip rect per half panel; the left half phased from x=0, the right half from its right edge (mirrored, r4 §8.1).
Visible sigils: the 5 positions per panel of r4 §8.2 (later DOM nodes win); none on the selection panel.

### 2.6 Widget tree, region by region (numbers from r4 §4-5; z order r4 §5.8)
Root `SChimeraMatchHud`: `SOverlay` of [backdrop layer] and an `SConstraintCanvas` (bottom to top: world overlays, toast, minimap panel, tab row, selection panel, command card, top strip).
Panels anchor to screen edges; the root is `SelfHitTestInvisible`; panels do not clip (ornaments overhang); the toast and command buttons clip to bounds.
- **Top strip** (ids 8-68): 1920x40 `#1C1F25`, bottom hairline `#3A3F48`. HBox (padding 0 8, gap 4): 4 chips (28 tall at **y=6**), spacer, alert-log button, Menu (`grid`, "Menu", F10 keycap
  28x18 `#20242B`). Clock group (`2:14` Mono600 18 + speed pill "Normal · ×1.0" Inter500 12, 1 px border) centred on x=960. Chip = border `#3A3F48` > fill `#14161A` padding 0 10 > HBox gap 7:
  icon 16 gold, value Mono600 15 `#ECE6D8`, rate Mono500 11 `#9A958A`. The supply chip keeps an empty rate slot (its 7 px gap).
- **Toast** (69-80): (12,814) 400x38, fill `#14161A`@.94, border `#3A3F48`, padding 9/12, gap 10: `info` 16, text Inter500 13 (flex), keycap "Space" (`#14161A`), 2 px gold timer bar (242.92 px at t=3 s).
  At t=3 its opacity is 1 (F30), so the gated frame has no group fade; the fade itself (motion, ungated) may use the toast's `ColorAndOpacity`.
- **World overlays** (4-7): ring bake centred on (1010,560); health bar 30x7 at (995,522) (`#0B0C0E` frame, `#4FB39A` 28x5, outer `rgba(236,230,216,.25)` 1 px). Screen space for this check only;
  hidden with `-HudWorldOverlays=0`.
- **Minimap panel** (81-218): 256x216 `#1C1F25`, top `#8A6E3C`, right `#3A3F48`. Three 28x28 buttons at (8,16) gap 4 (eye, flag, grid). Plate 200x200 at (44,8): `#262A31`, `#8A6E3C` border,
  two 1 px bevels, shadow bake. Inner line 192x192 `#4A4234`. Four node bakes. Map surface 184x184 at (52,16), **in CSS paint order**: 1 px `#0B0C0E` border; `minimap_photo.png` (photo crop
  only, the stand-in for the future live render); the inset-shadow bake; the dim box `rgba(20,22,26,.35)`; the fog radial bake; 11 unit dots (216 dropped as a duplicate); the camera rect 54x34 `#ECE6D8`.
- **Group tab** (219-230): (268,874) 30 tall, no bottom border, `#2A2F38` fill, `#E3C887` sides and top. "3" Mono600 11 `#E3C887`, `owner` 14, "8" Mono600 12.
- **Selection panel** (231-282): (256,904) 1364x176 `#1C1F25`, top `#8A6E3C`, padding 14/18, gap 20. Vat 124x147 at (274,919): `#8A6E3C` border, radial bake, `portrait_figure.png` at (312,975).
  Info column 340 wide at x=418: name Cinzel600 20, role Inter400 13 `#9A958A`, "220 / 220" Mono500 12, HP bar 340x7 (`#3A3F48` frame, `#0B0C0E` track, 338x5 `#4FB39A`), stat line Mono500 12 (ellipsis policy).
- **Command card** (283-483): (1620,864) 300x216 `#1C1F25`, top `#8A6E3C`, left `#3A3F48`, padding 12. Grid 4x64 by 60-px rows, gap 6. Slot: keycap 16x16 at (3,3) (`#0B0C0E`, radius 2),
  icon 24, cost Mono600 9, lock 12 at top 4 / right 4. States (r4 §5.7): normal Q W A S D F X C; locked E R (§2.4); active Z (`#173129`/`#4FB39A`); empty V (dashed bake).
- **Ornament frames** on minimap panel, selection panel, command card: rope strips 8 px centred on the top border, sigils (not on the selection panel), top laser head at its t=3 s position.

### 2.7 Backdrops, placeholders, the runtime file allow-list, and third-party pixels
`-HudBackdrop=` takes `#14161A` (pair A), `<abs path>/world_layer.png` (pair B, drawn 1:1 under the HUD), or `none` (live 3D world, T9). The world, the minimap photo and the portrait figure are
placeholders for future 3D renders (README.md:31-33); for them only, Chromium-rendered images are allowed. Every other pixel is drawn by Slate. All image loads go through `ChimeraUi::Load*`,
which logs `LogChimeraHud: loaded <path> <sha256>`; the shot script fails if a path is outside {`HudData/Fonts/*.ttf`; `HudData/Icons|Ornaments/*` whose sha256 matches `manifest.json`
(SVG or PNG route); the two placeholders; the backdrop arg}. **Third-party (Manor Lords) pixels never enter a repo**: they live only under `H/HudRef/` and `H/HudData/Placeholders/`
(both gitignored in U, T3); T0 moves the existing `research/hud-ref/*.png|*.jpg` to `H/HudRef/r4/` and adds a `docs/unreal-move/trial-checks/**/*.{png,jpg,gif}` ignore rule to `R/.gitignore`.
Repo commits name explicit paths only.

### 2.8 HUD clock and freeze
One HUD clock (seconds since construct) drives the toast fade and timer and the laser heads; rope reveal and sigil-in finish by 1.3 s and are drawn final. `-HudFreezeTime=3.0` pins the
clock at 3.0 s, the reference's capture state (r4 §2). Laser heads follow `chiRunX` (background-position −40% → 140% in the first 20% of a 19 s cycle, cubic-bezier from the JSON), delays
0 / 1.5 / 3 s per panel. Boundary rule (CSS active phase starts at local time 0): an animation with delay d is active with progress 0 when t ≥ d. The command card's top head sits exactly on
this boundary at t=3; T0 records whether it is visible in the reference and the code follows the same rule.

### 2.9 Capture automation (`ChimeraHudShot`) and `T/hud_shot.sh`
Switches: `-HudShot=<abs.png>`, `-HudShotFrames=N` (default 90), `-HudCompileWait=S` (default 1800), `-HudShotTimeout=S` (default 120), `-HudWarmup`.
On the core ticker: phase 1 waits until `(GShaderCompilingManager == nullptr || !GShaderCompilingManager->IsCompiling())` and `FAssetCompilingManager::Get().GetNumRemainingAssets() == 0`
(F17), logging `compile idle after <s> s`; past `HudCompileWait` wall-clock seconds it fails. Phase 2 counts N frames, logs the geometry line (§2.2) and calls
`RequestScreenshot(path, true, false, false, FIntRect(), true)`; past `HudShotTimeout` seconds from compile-idle it fails. On `OnScreenshotRequestProcessed` it checks the file exists, is
non-empty and newer than the request (F2): success logs `LogChimeraHud: Display: shot written <bytes>` and calls `RequestExitWithStatus(false, 0)`. A post-request ticker fails after 30 s.
Every failure logs `LogChimeraHud: Error: SHOT TIMEOUT|SHOT MISSING|SHOT NO CALLBACK` and calls `RequestExitWithStatus(true, 3|4|5)` (F16: only the forced path returns the code).
`-HudWarmup` skips the shot and logs `WARMUP DONE compile_s=<n>` after phase 1 + N frames.
`hud_shot.sh --backdrop X --tag T [--map M] [--extra "..."] [--warmup]`: first runs `T/preflight.ps1` **outside the lock** (primary screen 1920x1080 at AppliedDPI 96, else
`DESKTOP NOT 1920x1080@100%`, F5/F18), then under LOCK:
```
MSYS_NO_PATHCONV=1 "D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor.exe" "$H/ChimeraHud.uproject" "${MAP:-/Engine/Maps/Entry}?game=/Script/ChimeraHud.ChimeraHudGameMode" \
  -game -fullscreen -ResX=1920 -ResY=1080 -nosound -unattended -nosplash -ABSLOG=$H/HudRef/logs/$TAG.log -LogCmds="LogViewport Verbose" \
  -ExecCmds="DisableAllScreenMessages,r.ScreenPercentage 100,t.MaxFPS 60" -HudBackdrop=$X -HudFreezeTime=3.0 -HudShot=$H/HudRef/out/$TAG.png
```
It kills the process after `HudCompileWait + HudShotTimeout + 120` s (warm-up: 30 min). Checks: exit 0 **and** the `shot written` line (or `WARMUP DONE`); the PNG is newer than the start and
1920x1080; the last `Scene viewport resized to 1920x1080, mode (Fullscreen|WindowedFullscreen)` (mode recorded, F18); `geometry scale=1.000 local=1920x1080`; the allow-list; zero
`LogChimeraHud: Error`. It prints `SHOT OK <png> mode=<m> compile_s=<n> ...` or `SHOT FAIL <reason> exit=<code>`.
**Lock nesting**: the main session makes `ue_lock.sh` re-entrant (export `UE_LOCK_HOLDER=$$` to the child; a nested call whose `UE_LOCK_HOLDER` equals the live owner pid runs the command
directly). Every script then always calls LOCK; T3 proves it with a nested self-test. No private bypass variable.

## 3. Reference images (`T/render_reference.sh`, frozen after T0)

A scratch copy of `R/docs/ui-redesign` plus `assets/bf-village.jpg` from the export zip is served by `python -m http.server` from `H/HudRef/site`. playwright-cli runs headless with a pinned
`--config` (launch args `--disable-lcd-text --font-render-hinting=none`). **Fonts are pinned**: `T/fetch_webfonts.py` saves the Google CSS and the exact woff2 files once to `H/HudRef/webfonts/`
(sha256 in `T/webfonts.lock.json`); `T/ref_capture.js` routes `fonts.googleapis.com`/`fonts.gstatic.com` to those copies (`route`), awaits `document.fonts.ready` and asserts
`document.fonts.check()` for every family/weight/size used (Cinzel 600 20px; Inter 400 13px, 500 12-13px; JetBrains Mono 500 11-12px, 600 9-18px), failing otherwise. The routine selects "3.1a",
pauses all animations at 3000 ms and takes element screenshots of the 1920x1080 board. Outputs in `H/HudRef/ref/`, with `ref_meta.json` (Chromium version, user agent, WebGL renderer string,
launch args, font hashes, sha256 of every output; every control render must carry identical meta):
`ref_hudonly.png` (world nodes 1-3 hidden, flat `#14161A`; **pair A**), `ref_backdrop.png` (**pair B**), `world_layer.png` (only nodes 1-3), `hud_alpha.png` (black/white differencing, r4 §15),
`minimap_photo.png` (182x182 inner content of node 203 with its children hidden, box-shadow none), `portrait_figure.png` (48x78 RGBA of node 266), `text_off_{A,B}.png` (all text `color:transparent`,
the base for control P5), `controls/{A,B}/` (§4.4), and `lcd_vs_gray_text_4x.png` (old LCD vs new grayscale on the unit name, stat line and a chip, 4x; for Alec, §10).

## 4. Comparison (`T/hud_compare.py`; regions in `T/regions.json`)

### 4.1 Alignment (no silent re-alignment)
Both PNGs must be 1920x1080. Global capture alignment is proven by T3's 1:1 pipeline check and, in every pair-B run, by G6 on world.open. Anchor edges, all outside the rope bands (F25), then
localise faults: top-strip hairline row 39 at x=500, first chip left column 8 at y=20, Menu right column 1911 at y=20 (T4+); toast left column 12 at y=830, minimap right column 255 at y=1000,
plate top row 873 at x=100 (T5+); vat border top row 919 at x=336, command-card left column 1620 at y=1000, Q button top row 877 at x=1660 (T6+). Each is found by a ±10 px search for the
reference's exact edge profile (`make_regions --check` asserts the profile is unique in the window); only anchors of panels in the run's `--only` set are used. A mismatch prints
`ALIGNMENT FAIL <anchor> dx dy` and stops (a capture or DPI fault if G6/pipeline also fails, otherwise a mis-anchored panel).

### 4.2 Regions and pixel classes (`T/make_regions.py --ref <png>`, from `board-3.1-elements.json` and the given reference)
38 named regions, each pixel in exactly one, ornament bands taking precedence: `top.strip, top.chip.{gold,iron,aether,supply}, top.clock, top.alertlog, top.menu, toast, world.ring, world.open`
(the complement of the HUD alpha mask dilated 3 px: the control), `mm.panel, mm.buttons, mm.plate, mm.map, tab.3, sel.panel, sel.portrait, sel.{name,role,hp,stats}, card.panel,
card.{Q,W,E,R,A,S,D,F,Z,X,C,V}, orn.{minimap,selection,card}`. Each is **fixed** (screen-anchored) or **flow** (position depends on text width: chips 2-4, clock, alert, menu, tab, toast keycap).
Pixel classes from the JSON: **text** (glyph rects +1 px), **vector** (icon boxes +1, ornament bands, keycap corners, ring, dashes, nodes), **placeholder** (map photo, figure box), **flat** (rest).
Also generated: 36 text runs (string, style, ink window), every solid border side (a pixel line in the given reference within ±1 px of the predicted Chromium row, half-pixels up), and the
explicit probe list `probes[]` (id, x, y, rgb, class, pair). The final `regions.json` is generated from T0's `ref_hudonly.png` as T2's first step, then hashed.

### 4.3 Gates. Hard gates are physics (fixed); soft gates absorb anti-aliasing (calibrated)
| gate | what | pass rule | why this number |
|---|---|---|---|
| G1 colour probes | **36 hard probes** = r4 §13's 37 single-pixel probes minus the anti-aliased ring left edge, with the vat-bottom probe (336,1040) moved off the figure placeholder to a vat-glow pixel; plus 1 auto probe per box element's uncovered fill. The ring edge and the 4 rope rows are vector-class checks | each channel ±2/255 | The Slate colour round trip is exact (F10, F20); 2 levels cover 8-bit packing. One surface-token step is ≥ 4 levels |
| G2 border lines | every solid border side, at the region's G3 shift | ≥ 95% of pixels within ±8 | A 1 px edge offset drops a line to ~0%; 5% covers corners next to AA children |
| G3 region shift | argmin of flat+vector MAD over shifts in [-3,3]² (ties within 0.05 → 0); **G1 auto probes, G2, G4, G5 of a flow region are scored at this shift, which is reported** | fixed: (0,0); flow: ≤ 1 px per axis | SChimeraText places glyphs ≤ 0.5 px from exact and reports exact widths (§2.3), so a flow box lands on Chromium's snapped pixel or its neighbour. Without it chips drift +3 px (F8) |
| G4 class error | per region: flat MAD ≤ 1.0 and bad-px (> 8) ≤ 1%; placeholder MAD ≤ 2; vector and text MAD and SSIM | flat/placeholder fixed; vector/text calibrated | Flat pixels should be exact; AA is rasteriser-dependent |
| G5 text runs | ink box from the local background (median of the window rim), threshold 24 | left, top, bottom ±1 px; right ±max(1 px, 0.5% of width); ink mass calibrated | SChimeraText's 1/16 px advance quantisation drifts ≤ n/32 px; JetBrains Mono 12 px: 115/16 = 7.1875 vs 7.2 px, 0.58 px over the 46-glyph stat line |
| G6 pipeline control | world.open (and pair B's backdrop pixels) | MAD ≤ 0.5, max ≤ 2 | The backdrop is a 1:1 blit; anything else is a colour, DPI, HDR or overlay fault |
| G7 summary | MAD and SSIM (luma, Gaussian σ 1.5, K1 .01, K2 .03) **over the HUD mask dilated 3 px**; world.open reported separately | calibrated | 78% of the frame is identical by construction and would inflate a whole-frame score |

### 4.4 Calibration (`T/calibrate.py`, per pair, then frozen)
Controls are rendered for **both pairs** (13 each) with one thing changed, by CSS injection keyed on `title` attributes and text content; each control's intended regions are listed in `T/ref_controls.json`.
**Positives (must PASS every gate):** P1 static TTFs served locally instead of the Google variable fonts; P2 `font-kerning:none`; P3 text-bearing and icon elements offset by
`position:relative; left:.4px; top:.3px` (a CSS `transform` would be a no-op on inline text); P4 = P1+P2+P3; **P5 Slate model**: `text_off_{A,B}.png` with every text run re-drawn by FreeType
(freetype-py, a new pinned dev dependency, load flags = the chosen Slate hinting, start None), glyph origins at `round(exact x)` from hmtx + GPOS kerning (fontTools), raw grayscale coverage,
straight-alpha blend in sRGB bytes, each run placed at the integer origin that best fits the reference ink box. **Negatives (must FAIL, only in their listed regions):** N1 command card
translateY 1 px (card.* and orn.card); N2 chip fill `#1C1F25`; N3 unit name weight 700; N4 toast text 14 px; N5 eye-button border removed; N6 Stop icon swapped for `grid`; N7 stat line in Inter;
N8 role text `#ECE6D8`. Every control must differ from its base: changed pixels > 0 inside its intended regions and, for negatives, 0 outside them; the table prints the counts.
Soft threshold rule: `thr = 1.5 x worst positive`, required below `best negative / 1.2`; if none exists the metric is **report-only** and named. A positive failing a hard gate means a mask is
wrong: fix the mask, never the gate. **A negative that passes every gate stops calibration**: escalate to the main session with its per-metric values; the remedy is a new hard metric for that
defect class (e.g. per-run mean ink colour), never dropping the control. If every text soft metric of a region is report-only, escalate before T4a starts.
Outputs: `T/thresholds.json`, `T/results/calibration.json` (sha256 of thresholds, regions, refs, controls, freetype-py version), committed by the main session with a `HUD: calibration` message; the
commit hash is appended to `T/results/calibration_log.csv`. `hud_compare.py` refuses to score on any hash mismatch unless run with `--recalibrated <note>` (main session only; every use is
written to `convergence.csv`). **One planned recalibration**: if T4a picks a hinting other than None, the main session regenerates P5 at that hinting and recalibrates once.

### 4.5 Outputs per run (`H/HudRef/out/<tag>/`)
`report.json` (every region, class, gate, value, threshold, flow shift), `report.md` (failing items first), `diff_heat.png` (max-channel |diff|: 0 black, 8 blue, 32 yellow, ≥ 64 red; region boxes
green/red with labels), `side_by_side.png` (ref | Unreal, plus a 1920-wide half-scale phone copy), `crops/<region>.png` (ref | Unreal | diff at 4x, 5 worst regions), `flicker_worst.gif`
(alternating 0.6 s, 3x), `scorecard.png` (heatmap + region table). Last stdout line per pair: `PAIR <A|B>: RESULT PASS|FAIL <k>/<n> regions failing; HUD MAD x; HUD SSIM y; WORST <region> badness z (<gate>)`.
Badness = max over gates of value/threshold (≤ 1 passes).

## 5. Iterate-until-converged loop (`T/hud_iterate.sh <tag> [--only REGEX] [--gates LIST] [--skip-build]`)
1. One LOCK hold covers build + shot A + shot B (about 4-5 min); the inner scripts' LOCK calls are re-entrant (§2.9). Comparisons run outside the lock.
2. Compare both pairs, write `out/<tag>-<n>/`, append to `T/results/convergence.csv` (iteration, time, failing count per pair, worst region, badness, HUD MAD, SSIM, source tree hash, recalibration note).
3. If both pairs PASS, take one more shot and require MAD 0 against the previous one (`DETERMINISM OK`). Done.
4. Otherwise read the worst region's crop and failing gates and fix **one root cause**, in the shared style or vocabulary first (font-style nudge, token, bake parameter). Runtime switches
   (`-HudHinting`, `-HudKern`, `-HudIcons`, `-HudText`) use `--skip-build`.
5. Escalate to the main session after 15 iterations, or after 3 consecutive iterations where worst badness falls < 5% and the failing count does not drop, with the region, numbers, hypotheses tried and crop.
6. Forbidden: editing `thresholds.json`, `regions.json` or references; per-element text nudges; images outside the allow-list.

## 6. Tasks (in order; T0, T1 and T3's build can run in parallel; anything that builds or runs ChimeraHud is serial)
Commit messages for this check start with `HUD:`; T0 records the base commit of R in `T/results/base_commit.txt`.

**T0 · Grayscale references and controls.** Tier sonnet (medium). Lock: no. Deps: none.
First: move `R/docs/unreal-move/trial-checks/research/hud-ref/*.{png,jpg}` to `H/HudRef/r4/` (r4's PNG names now live there), add the ignore rule (§2.7) to `R/.gitignore`, write `base_commit.txt`.
Files: `T/render_reference.sh`, `T/fetch_webfonts.py`, `T/webfonts.lock.json`, `T/ref_capture.js`, `T/ref_controls.json`, `T/ref_check.py`; outputs `H/HudRef/ref/**`, `H/HudData/Placeholders/*.png`.
Accept: `bash T/render_reference.sh && python T/ref_check.py` prints for each pair `1920x1080 lcd_fringed 0`, `fonts loaded <n>/<n>, woff2 sha256 match`, `repeat MAD 0.000`, `r4 §13 non-text values 41/41`,
`controls 13/13 rendered, each differs from base`, then `placeholders 2/2 (182x182, 48x78 RGBA)`, `world_layer vs ref_backdrop over world.open: max <= 1`, `card laser head at 3000 ms: visible|hidden (<n> px)`,
`REF CHECK OK`. And `! git -C R status --porcelain -uall | grep -qE '\.(png|jpg|gif)$'` succeeds, `git -C R ls-files docs/unreal-move | grep -E '\.(png|jpg|gif)$'` prints nothing.
Then the main session sends `lcd_vs_gray_text_4x.png` to Alec (§10).

**T1 · Regions + comparator + self-tests + text drift model.** Tier sonnet (high: long spec). Lock: no. Deps: none (develops against `H/HudRef/r4/` until T0's refs exist).
Files: `T/make_regions.py`, `T/hud_compare.py`, `T/ssim.py`, `T/text_drift.py`, `T/tests/test_hud_compare.py`.
Accept: `python T/make_regions.py --ref <png> --check` prints 38 / 36 / >100 (regions, runs, border lines), `probes 36 hard + 5 vector`, `anchors unique 9/9`, `every pixel in exactly one region: OK`.
`python T/text_drift.py` writes `T/results/text_drift.csv` (per run: exact width, whole-px width, worst glyph-origin error for both routes) and prints the chip x for both routes (`block 8/112/200/288`, `frac 8/111/198/285`).
`python -m pytest T/tests -q` all passed, covering: identity → PASS, MAD 0, SSIM 1.0000; a 1 px shift of card.S fails G2 and G3 in card.S only; a 1 px shift of top.chip.iron passes every gate and reports
shift (1,0); a 2 px shift of it fails G3 only; recolouring a chip fill fails G1; erasing one border fails G2; whole-image 1 px offset and 1.01 rescale give `ALIGNMENT FAIL`; SSIM equals the closed
form on constant-offset images; badness ordering.

**T2 · Calibration.** Tier sonnet (medium). Lock: no. Deps: T0, T1, **and Alec's answer on §10 (or his "proceed on the recommendation")**.
Files: `T/calibrate.py`, `T/make_p5.py`, `T/thresholds.json`, `T/results/calibration.json`, `T/results/calibration_log.csv`. First regenerate `regions.json` from `ref_hudonly.png`.
Accept: `python T/calibrate.py` prints the per-pair control table (changed-pixel counts, per-metric values) and, per pair, `CALIBRATION OK positives 5/5 pass, negatives 8/8 fail only in intended regions;
report-only: [..]; thresholds sha256 <hex>`. Checkpoint: the main session commits T/ by explicit paths (`HUD: calibration frozen`).

**T3 · ChimeraHud project + capture pipeline proof.** Tier sonnet (medium). Lock: yes (build; short runs; one warm-up). Deps: T0 (world_layer.png); the re-entrant `ue_lock.sh` from the main session.
Files: H project files (§2.1), `Game/*`, root `SChimeraMatchHud` with the backdrop layer, geometry log and `-HudTestPattern=blend`, `T/hud_build.sh`, `T/hud_shot.sh`, `T/preflight.ps1`, `T/sync_src.sh`, `U/.gitignore`.
Accept, in order: (1) `timeout 10 bash $U/ue_lock.sh bash $U/ue_lock.sh true` exits 0 (else stop and ask the main session). (2) `powershell -File T/preflight.ps1` prints `DESKTOP OK 1920x1080 @96`.
(3) `bash T/hud_build.sh` shows `Result: Succeeded` and `H/Binaries/Win64/UnrealEditor-ChimeraHud.dll` exists. (4) `bash T/hud_shot.sh --warmup --tag t3warm` prints `SHOT OK ... WARMUP DONE compile_s=<n>`.
(5) `bash T/hud_shot.sh --backdrop '#14161A' --tag t3flat` prints `SHOT OK ... mode=WindowedFullscreen|Fullscreen scale=1.000 local=1920x1080`. (6) `--backdrop $H/HudRef/ref/world_layer.png --tag t3world` then
`python T/hud_compare.py --pipeline ...` prints `PIPELINE OK MAD 0.000 max 0` (the rule for T3 is max 0, F20; G6's looser limit applies to pairs A/B only); a second t3world has MAD 0 against the first.
(7) `--extra -HudTestPattern=blend --tag t3blend` (50%-alpha `#ECE6D8` and 25%-alpha `#0B0C0E` boxes over `#14161A` and over world_layer) then `hud_compare.py --blend` prints `BLEND OK gamma-space max err <= 1`
(if not, stop: the alpha strategy of §2.4 changes). (8) `--extra "-HudShotFrames=100000 -HudShotTimeout=5" --tag t3timeout` prints `SHOT FAIL SHOT TIMEOUT exit=3`. (9) `git -C $U check-ignore -q ChimeraHud/HudRef/a.png
ChimeraHud/HudData/Placeholders/a.png` exits 0 and `! git -C $U status --porcelain -uall | grep -qE '\.(png|jpg|gif)$'`. (10) `bash T/sync_src.sh && bash T/sync_src.sh --check` prints `MIRROR OK, 0 secrets`.

**T4a · UI vocabulary, fonts, SChimeraText, text and icon routes.** Tier **opus (medium)** (visual judgement). Lock: yes. Deps: T2, T3.
Files: `Ui/ChimeraUi.*`, `Ui/ChimeraHudState.h`, `Widgets/{SChimeraText, SChimeraKeycap, SChimeraIcon}`, `Panels/SChimeraTopStrip` (first cut), `T/make_svgs.py`, `H/HudData/{Fonts,Icons,Ornaments,manifest.json}`.
Switches: `-HudHinting=None|Default|AutoLight`, `-HudKern=on|off`, `-HudIcons=svg|png`, `-HudText=frac|block`.
Accept: `T/results/t4_routes.csv` has **8 rows** (text frac x hinting {None, Default, AutoLight} x kern {on, off}; text block at the best hinting with kern on; png icons at the best text route),
each with top.* text-failing counts, G5 edge errors and MADs, and the chosen defaults compiled in and named in a `chosen` column. With the chosen route, the comparator reports the 4 chip left borders
at x = 8/111/198/285. The log line `LogChimeraHud: kern check "Covenant Acolyte" Cinzel600 20px kern=<w> nokern=<w0>` shows w0 − w within 0.3 px of the fontTools kerning sum (≈ 1.9 px, r4 §10).

**T4b · Top-strip convergence.** Tier opus (medium). Lock: yes. Deps: T4a.
Accept: `bash T/hud_iterate.sh t4 --only '^top\.'` gives `PAIR A: RESULT PASS 0/8`. Checkpoint commit (U and R, explicit paths).

**T5 · Toast, world overlays, minimap panel, group tab.** Tier sonnet (medium). Lock: yes. Deps: T4b.
Files add: `Ui/ChimeraBakes.*` (shadow outer/inset, radial, ring set, ringed node), the minimap layer order of §2.6.
Accept: `bash T/hud_iterate.sh t5 --only '^(toast|world\.|mm\.|tab\.)' --gates G1,G2,G3,G5,G6` gives `RESULT PASS` on A and B (G4 reported; bakes are tuned in T7).

**T6 · Selection panel and command card.** Tier sonnet (medium). Lock: yes. Deps: T5.
Files add: vat-glow radial and dashed-border bakes; locked-button pre-compositing (§2.4).
Accept: `bash T/hud_iterate.sh t6 --only '^(sel\.|card\.)' --gates G1,G2,G3,G5` gives `RESULT PASS` on A and B, including all 12 slot states. Checkpoint commit.

**T7 · The hard visual tail: ornaments, shadows, radial, ring, dashes.** Tier **opus (medium)** (geometry, visual). Lock: yes. Deps: T6.
Files: `Panels/SChimeraOrnamentFrame`, bake parameters, laser keyframe maths. Accept: `bash T/hud_iterate.sh t7 --only '^(orn\.|mm\.plate|mm\.map|sel\.portrait|card\.V|world\.ring)'` gives `RESULT PASS` (all gates) on A and B.

**T8 · Full-board convergence, UI-scale shot, HUD cost.** Tier **opus (medium)**. Lock: yes, per iteration. Deps: T7.
Accept: `bash T/hud_iterate.sh final` gives `PAIR A: RESULT PASS 0/38`, `PAIR B: RESULT PASS 0/38`, `DETERMINISM OK`, with `convergence.csv` showing the path. In the last hold:
`bash T/hud_shot.sh --backdrop '#14161A' --extra -HudUiScale=0.8 --tag uiscale80` prints `SHOT OK ... scale=0.800` (ungated; the reviewer checks panels stay edge-anchored with no overlap or clipping);
`LOCK powershell -File T/hud_cost.ps1` (Entry map, run_fps pattern, `-csvCaptureFrames=1500 -ExitAfterCsvProfiling`, `-HudOff` vs on, 3 reps each after a warm-up) prints
`HUD cost: frame ms off/on min a/b median c/d; game-thread and render-thread ms off/on ...` and writes `T/results/hud_cost.json`. No gate; a median delta above 0.5 ms is flagged. Checkpoint commit.

**T9 · (optional) HUD over the live look-test world.** Tier sonnet (medium). Lock: yes (one warm-up hold of up to 30 min, then short). Deps: T8. Not a dependency of T10;
the main session runs it only when checks (a) and (c) are not waiting on the lock, or defers it to the merge.
Steps: copy `P/Content/LookTest` to `H/Content/LookTest` (231 MB, same `/Game` paths); add `PCGBiomeSample` to the .uproject; **rebuild in the same hold** (`bash T/hud_build.sh` → `Result: Succeeded`;
the plugin-list change relinks, r3:361-362); `hud_shot.sh --warmup --map /Game/LookTest/Maps/LT_A_noLumen --backdrop none`; then the shot.
Accept: `bash T/hud_shot.sh --map /Game/LookTest/Maps/LT_A_noLumen --backdrop none --extra -HudWorldOverlays=0 --tag bonus_live` prints `SHOT OK`, and
`grep -cE "LogLinker: (Error|Warning)|LogStreaming: (Error|Warning)|LogUObjectGlobals: Warning: Failed to find|Failed to load '/(Game|PCGBiomeSample|Engine)/" <log>` prints 0
(the 4 `LogWindows: Failed to load '*.dll'` profiler probes are normal, F19).

**T10 · Proof pack, audits, review.** Tier sonnet (medium) for the scripts; the visual verdict and source review by an **Opus reviewer (xhigh)**. Lock: no. Deps: T8.
Files: `T/make_proof.py`, `T/results/{report_A.json, report_B.json, convergence.csv, calibration.json, hud_cost.json, review.md}`.
Accept: `python T/make_proof.py` writes `H/HudRef/proof/{scorecard_A.png, scorecard_B.png, side_by_side_B_phone.png, diff_heat_A.png, worst5_crops.png, flicker_worst.gif, uiscale80.png}` (+ `bonus_live.png`
if T9 ran) and prints sizes and headline numbers. Audits, all silent on success:
`bash T/sync_src.sh --check` → `MIRROR OK, 0 secrets`; `git -C R log --format= --name-only --grep '^HUD:' $(cat T/results/base_commit.txt)..HEAD -- godot` empty;
`git -C R diff --quiet <last calibration_log commit> -- T/thresholds.json T/regions.json T/results/calibration.json` (and the hash printed);
`grep -rnE 'UUserWidget|WidgetBlueprint|FSlateColorBrush\(FColor|OutlineSettings' $H/Source`, `grep -n SetRenderOpacity $H/Source/ChimeraHud/Ui/Panels/SChimeraCommandCard.*`, `grep -rnE 'SNew\(STextBlock' $H/Source | grep -v ChimeraUi.cpp`,
`grep -rnE 'FSlateDynamicImageBrush|FSlateVectorImageBrush|LoadFileToArray|FSlateRoundedBoxBrush' $H/Source | grep -vE 'ChimeraUi.cpp|ChimeraBakes.cpp'` all empty;
`find $H/Content -name '*.uasset' -not -path '*/LookTest/*' | wc -l` prints 0. The reviewer reads the Source diff (reference-derived data baked into code, per-element offsets, image loads
outside the loader) and looks at the scorecards, the 5 worst crops at 1:1 and 4x, the flicker GIF and the UI-scale shot, and writes a short verdict to `review.md`.
Then the main session sends the PNGs plus the verdict to Alec (SendUserFile, render) and commits R by explicit paths (`T/**` scripts, JSON, CSV, `docs/unreal-move/trial-checks/plan-b-match-hud.md`) and U likewise.

## 7. Proof of done (all must hold on one final build)
1. Pairs A and B both print `RESULT PASS 0/38` against the frozen thresholds; the hashes printed by the compare match `calibration.json`, and `thresholds/regions/calibration` equal the last
   calibration commit. Hard gates: 36 probes ±2/255 plus auto probes (G1); border lines ≥ 95% (G2); fixed shift (0,0), flow ≤ 1 px, with flow regions scored at their shift (G3); flat MAD ≤ 1.0 and bad
   pixels ≤ 1%; placeholders MAD ≤ 2; world.open MAD ≤ 0.5 (G6); text runs left/top/bottom ±1 px, right ±max(1 px, 0.5%). Soft gates within calibrated values; HUD-mask MAD and SSIM as headline.
2. Calibration per pair: positives 5/5 pass, negatives 8/8 fail only in their listed regions, every control differs from its base. Comparator self-tests pass.
3. Capture: `scale=1.000 local=1920x1080`, viewport 1920x1080 (mode recorded), desktop pre-flight OK, freeze 3.000, allow-list clean (sha256 manifest), repeat shot MAD 0.000; the forced-timeout run exits 3.
4. Text-first: no widget Blueprint, no `.uasset` outside the optional bonus's imported art; H versioned in U and mirrored to R (`MIRROR OK, 0 secrets`); no `HUD:` commit touches `godot/`
   (the sim baseline 6392 / 0 / 1 skipped is untouched by construction); no third-party image tracked in R or U.
5. The Opus reviewer's written verdict, and on Alec's phone: both scorecards, the side-by-side, the worst-5 crops, the flicker GIF, the UI-scale shot, and the HUD frame cost.

## 8. Risks and fallbacks
- **Text misses G5 or the soft text gates** after SChimeraText, the hinting choice and per-style nudges: the residual is measured and taken to Alec (§10), never hidden by loosening thresholds.
- **SChimeraText cost**: per-glyph elements and 16x measures; placement is cached per string and static panels can sit in an invalidation panel. T8's HUD cost shows the price.
- **nanosvg anti-aliasing differs from Skia** (UNVERIFIED): T4a measures svg vs png; the PNG route (Skia-identical, from our SVGs) is the fallback and loses resolution independence.
- **Engine overlays in the capture**: `DisableAllScreenMessages`, the compile-idle wait, and G6.
- **DPI, HDR or desktop drift** (Parsec can change the host resolution): the pre-flight fails fast before the lock; the geometry log, §4.1 and G6 fail loudly. Fallback: `SetRes 1920x1080f` in ExecCmds (r5 §4).
- **First run compiles shaders** (P took ~12 min, HANDOFF:46; r3:380): T3's explicit warm-up with a 30-min cap; shot timeouts count from compile-idle.
- **Lock contention with (a) and (c)**: one hold per iteration (~4-5 min); T9 is optional and last. `ue_lock.sh` exits 75 after 6 h; the task reports that rather than retrying.
- **Threshold gaming**: hash-frozen thresholds, regions and refs; git check against the calibration commit; `--recalibrated` logged; the allow-list with sha256; grep audits and the Opus review.
- **Reference drift**: fonts pinned by sha256 and asserted loaded; browser meta recorded and required equal for every control.
- **Font source**: P1 measures static TTFs against the variable fonts; if P1 fails a hard gate, use static instances generated from the variable font with fontTools (r5 §1.3).
- **Third-party pixels**: they exist only under `H/HudRef/` and `H/HudData/Placeholders/` (ignored in U); R ignores trial-check images; T0 and T3 check with `-uall`; commits name explicit paths.
- **Check (a) edits `godot/` in R at the same time**: (b) never touches it, and its audit checks its own `HUD:` commits, not the working tree.
- **On the fixed decisions**: no disagreement. One correction: the fixed decisions describe P as "NOT a git repo"; its parent U became one today (F23), so H is versioned there.
  Two requests to the main session: make `ue_lock.sh` re-entrant (§2.9), and decide when T9 runs (it is optional and costs up to 30 min of lock).

## 9. Out of scope
Every other board (3.2a-g, 3.3a, 3.4a-b, 3.6a) and their components. Interaction: hover, focus, clicks, hotkeys, tooltips, minimap drag and ping. Motion beyond the frozen t=3.0 frame.
World-space rings and bars as decals, the live minimap render, the 3D portrait render target, fog of war. UI scale 80-150% as a gate (one ungated 0.8 shot only). Packaging and loose-file staging.
Sim integration (check a), terrain (check c), and merging the three projects.

## 10. Decisions that are Alec's
1. **What "pixel-faithful" means for text, needed before T2 freezes thresholds.** Slate draws grayscale text, so it can never equal the approved mockup's subpixel (LCD) text (F29: 961 of 1341
   ink pixels on the unit name are colour-fringed). Recommendation: compare against a grayscale re-render of the same mockup; hold layout, size, colour and shape to the exact gates; place text
   with fractional glyph positions so runs match Chromium's widths (±1 px edges); judge weight and colour by ink mass with tolerances set by measured controls, not by eye.
   He gets `lcd_vs_gray_text_4x.png` after T0 so the choice is concrete; an answer of "proceed" adopts the recommendation.

## Review log (revision 2, 2026-10-01; each issue checked against the cited file)
API lens:
1. Fullscreen vs WindowedFullscreen: **accepted**. 9/9 logs say WindowedFullscreen; gate on size, record the mode (F18, §2.9, T3, §7.3).
2. Integer advances shift chips 2-3 px: **accepted**. Arithmetic confirmed (JBM advance 600/1000, chips 8/112/200/288); SChimeraText with exact desired width is now the default in T4a; cumulative-G3 alternative rejected as weaker.
3. RenderOpacity blends per element: **accepted**. SWidget.cpp:1490 confirmed; locked buttons pre-composite every colour and use no RenderOpacity (§2.4, grep gate).
4. Manor Lords pixels inside R, invisible to the T0 gate: **accepted**. `-uall` lists 5 images; T0 moves them to H/HudRef/r4, adds an ignore rule, gates with `-uall` and `ls-files`; commits by explicit path.
5. U is a git repo and does not ignore HudRef: **accepted**. Confirmed (30b6744, 9f13ed6, no remote); H versioned in U, ignores added in T3, mirror kept as the off-machine copy.
6. T8b trigger misses predicted drift: **accepted**. T8b removed; SChimeraText decided up front, with `text_drift.py` as the evidence (T1) and the route test in T4a.
7. Shader compile vs 6000-frame give-up: **accepted**. Time-based phases from compile-idle, explicit warm-up mode (30 min), null-checked manager, asset-compile wait (§2.9).
8. "0 Failed to load" always fails: **accepted**. 4 profiler-DLL lines confirmed; T9 regex matches asset failures only.
9. RequestExitWithStatus(false, 3) exits 0: **accepted**. Confirmed in WindowsPlatformMisc.cpp and Launch.cpp; failures use the forced path, success lines are logged and checked.
10. Include paths and module file: **accepted**. V7 drops legacy paths (ModuleRules.cs); `PublicIncludePaths.Add(ModuleDirectory)` as in TP_ThirdPerson; module .cpp listed.
11. MakeGradient orientation and pixel stops: **accepted**. ElementBatcher.cpp:1737-1797 confirmed; §2.4 states Orient_Vertical and pixel positions.
12. Live Google Fonts: **accepted**. Fonts fetched once, sha256-pinned, routed locally and asserted loaded (§3).
13. Anchors inside rope bands: **accepted**. Worse than stated: no rope-free column exists in any band (F25, x=900 included); anchors moved outside the bands (§4.1).
14. Probe count and AA rope rows: **accepted**. 37 single + 4 rope = 41 in r4 §13; explicit `probes[]`, 36 hard, ring edge and rope rows in the vector class.
15. Inset shadow out of paint order: **accepted with a different fix**. The placeholder is now the photo crop only; Slate draws inset, dim and fog above it in CSS order (keeps the frame chrome in Slate, matching the future live render).
16. PNG-icon route breaks the allow-list: **accepted**. Allow-list includes generated icons/ornaments verified against a sha256 manifest.
17. F10 overstated softness: **accepted**. Shader read (fill spread .5, outline spread 1); F11 rewritten, fill-only keycaps mandated, outline constructor banned, nodes baked.
18. T4 too large: **accepted**. Split into T4a (vocabulary, text and icon routes) and T4b (top strip); bakes moved to T5/T6.
19. Route CSV count: **accepted**. Exact 8-row matrix stated.
20. N1 also moves orn.card: **accepted**. Intended regions per control live in `ref_controls.json`.
21. T9 plugin needs a rebuild: **accepted**. Rebuild inside T9's hold before any shot.
22. regions.json built before T0's refs: **accepted**. `make_regions.py --ref`; regenerated from ref_hudonly.png as T2's first step.
23. world_layer vs ref_backdrop unchecked: **accepted**. ref_check compares them over world.open, max ≤ 1.
24. Shaping is an STextBlock argument: **accepted**. STextBlock.h:142 confirmed; `ChimeraUi::Text()` factory, cvar `Slate.DefaultTextShapingMethod=2`, grep gate.
25. Ambiguous pipeline rule: **accepted**. T3 requires max 0 (10-bit round trip is exact, F20); G6 limits apply to pairs only.
Proof lens:
26. Whole-pixel advances fail T4 (blocker): **accepted**. Same as API 2/6; SChimeraText reports the exact desired width; T8b removed.
27. Fullscreen mode: **accepted**. Same as API 1, plus the desktop pre-flight.
28. G2/G4/G1 not shift-compensated: **accepted**. Flow regions are scored at G3's shift; T1 self-tests for 1 px (pass) and 2 px (G3 only) chip shifts.
29. Chromium-only controls: **accepted**. P5 Slate-model positive (FreeType grayscale, rounded origins, raw coverage), per pair; one planned recalibration if hinting changes; escalation when no threshold separates. P6 (non-Skia icons) rejected: the PNG icon route is the remedy for nanosvg differences.
30. Manor Lords pixels in R and T10 committing R/docs: **accepted**. Same as API 4; `grep -c` exit-1 trap avoided with `! grep -q`.
31. T9 "Failed to load": **accepted**. Same as API 8; T9 also no longer blocks T10.
32. godot/ check breaks while (a) edits godot/: **accepted**. Audit checks `HUD:` commits since the recorded base commit.
33. Font loading unchecked: **accepted**. Same as API 12.
34. No-op positive control: **accepted**. CSS transforms do not apply to inline text; P3 uses relative offsets; every control must change pixels in its regions.
35. Shot timeouts vs warm-up: **accepted**. Same as API 7.
36. Screenshot delegate fires on failure: **accepted**. Confirmed (GameViewportClient.cpp:2583 outside the success block); file checked in the delegate, post-request watchdog, exit codes 4/5.
37. Lock re-entrancy: **accepted**. Private bypass variable removed; the main session makes ue_lock.sh re-entrant via `UE_LOCK_HOLDER`; T3 nested self-test.
38. T9 cost unlocked, 2 runs: **accepted**. Cost moved to T8 on the Entry map, under LOCK, 3 reps, min/median of frame, game- and render-thread times.
39. Tamper by regenerating calibration: **accepted**. Calibration commits logged; T10 diffs against the last one; `--recalibrated` logged.
40. Self-reported allow-list, no nudge check: **accepted in part**. sha256 manifest, single loader, grep gates and the Opus source review; a generic "numeric array > 64" grep rejected as noisy (the reviewer covers it).
41. Inset shadow order: **accepted**. Same as API 15.
42. Headline SSIM inflated: **accepted**. G7 over the dilated HUD mask; world.open separate.
43. Gamma vs linear blending untested: **accepted**. T3 blend test with a stop rule.
44. Route CSV count: **accepted**. Same as API 19.
45. Probe ambiguity, (336,1040) on the figure: **accepted**. Figure box confirmed (312..359 x 975..1052); probe moved, explicit list.
46. F10 and keycap brush form: **accepted**. Same as API 17; the keycap bottom probe (1645,896) stays in G1.
47. Parsec desktop changes: **accepted**. `preflight.ps1` before the lock.
48. T9 bonus cost: **accepted**. T9 optional, off T10's path; HUD cost on the Entry map in T8.
49. Calibration for one pair only: **accepted**. 13 controls per pair, thresholds per pair.
50. Negative that passes: **accepted**. Stop and escalate; add a hard metric, never drop the control.
51. Alec's text decision vs freeze: **accepted**. T2 waits for his answer; T0 produces the 4x comparison PNG.
52. Nobody looks before done: **accepted**. Opus reviewer verdict in T10, sent with the PNGs.
53. Card laser at the freeze boundary: **accepted**. CSS boundary rule stated; T0 records whether the head is visible.
54. Minimap anchor under the rope: **accepted, proposed fix rejected**. x=20 is not clean (rope at rows 861/862/867/868); anchors moved outside the bands instead.
55. SecurityToken in the mirror: **accepted**. Confirmed in P's ini (already committed in U); sync_src.sh redacts and `--check` counts secrets.
56. Loose files under Content auto-import: **accepted**. Defaults confirmed ON (SettingsClasses.cpp:253-264); runtime files moved to `H/HudData/`.
57. Raster mode not pinned: **accepted**. Headless pinned; UA, WebGL renderer and args recorded; controls must match.
58. Nothing exercises anchoring or UI scale: **accepted**. Ungated `-HudUiScale=0.8` shot in T8, reviewed in T10.

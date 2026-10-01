# R5: Pixel-faithful HUD in pure C++ Slate (UE 5.8.3)

Date 2026-10-01. UNDERSTAND phase, read-only. Nothing was compiled or launched in Unreal. Every API claim below was read in the installed
source; `UE:` = `D:/Epic Games/UE_5.8/Engine/`. Claims I could not prove are tagged **UNVERIFIED**. Measurements marked **MEASURED** were run
today with `playwright-cli` and fontTools on this machine (scratch files in the session scratchpad, not in either project).

## 0. Verdict and the five facts that decide the design

Slate can reach the mockup with **no widget Blueprints and no binary assets beyond the font files** (TTFs are loose files, not assets).
Rectangles, hairlines, keycaps, icons, panels: pixel-exact or within 1 px. **Text is the hard part**; it will be close but not identical
unless we take extra steps. Five facts, each proven below:

1. **CSS px to Slate font size is exactly `Size = px * 0.75`** (render DPI is hard-coded 96, points are 1/72 in). 13 px -> 9.75. Section 1.2.
2. **Slate rounds every glyph advance to whole pixels and renders grayscale only.** Chromium here renders subpixel (LCD) text and fractional advances. Section 5.
   For a 30-char 13 px Inter label the width differs by up to ~3 px (modelled; real hinted numbers UNVERIFIED).
3. **Kerning is silently OFF for Inter, Cinzel and JetBrains Mono** unless we force full shaping AND set a non-zero `LetterSpacing`. They have GPOS kerning
   only (no legacy `kern` table, MEASURED) and Slate only enables the HarfBuzz `kern` feature when the face has a `kern` table or letter spacing != 0. Section 1.5.
4. **Letter spacing is truncated to whole pixels** (`int16` cast), so `.16em` at 12 px (1.92 px) becomes 1 px. Section 1.3.
5. **Slate's rounded-box brush is soft-edged (smoothstep +-1 px), so never use it for the mockup's 0-radius boxes**; use `FSlateColorBrush` quads
   (pixel-snapped, crisp). SVG icons ARE supported at runtime (nanosvg) and the mockup's icon set is plain primitives. Sections 2.1, 2.5.

Recommended build route: one `SChimeraText` leaf widget (fractional glyph placement from 16x measurement) is **contingency**, not step 1. Step 1 is plain
`STextBlock` + `FullShaping` + `EFontHinting::None`, measured against a grayscale Chromium reference, and only then decide.

## 1. Fonts

### 1.1 Loading a TTF from disk at runtime (non-deprecated route)
- `FSlateFontInfo(const FString& InFontName, float, EFontHinting, ...)` and the FName/ANSI/WIDE variants are `UE_DEPRECATED(5.6, "Use constructor with FCompositeFont instead.")`
  (`UE:Source/Runtime/SlateCore/Public/Fonts/SlateFontInfo.h:245,255,265`). Internally the filename ctor just wraps one `FStandaloneCompositeFont(NAME_None, path, hinting, LazyLoad)`
  (`Private/Fonts/LegacySlateFontInfoCache.cpp:89`, via `SlateFontInfo.cpp:227-236`). So use the composite-font ctor directly:
  `FSlateFontInfo(TSharedPtr<const FCompositeFont>, float Size, const FName& TypefaceFontName, const FFontOutlineSettings&)` (`SlateFontInfo.h:227`).
- `FStandaloneCompositeFont` (`Public/Fonts/CompositeFont.h:494-505`) derives from `FGCObject` (create on the game thread, keep the `TSharedRef` alive for the process).
  `FCompositeFont::DefaultTypeface` is an `FTypeface` with `AppendFont(FName, FString File, EFontHinting, EFontLoadingPolicy)` (`CompositeFont.h:369`).
  `FFontData(file, hinting, policy)` has `check(policy != Inline)` (`Private/Fonts/CompositeFont.cpp:135`): use `LazyLoad` (whole file read into RAM, `FontCacheCompositeFont.cpp:737`)
  or `Stream`. `Slate.Font.AsyncLazyLoad` defaults **false** (`FontCacheCompositeFont.cpp:19`), so loading is synchronous; no blank first frame.
- Missing glyphs fall back to the composite font's `FallbackTypeface`/engine fonts; a missing file logs a warning and draws nothing (`FontCacheCompositeFont.cpp:60`).
- Packaging: loose files under `Content/` need `+DirectoriesToAlwaysStageAsNonUFS=(Path="ChimeraUi")` in `[/Script/UnrealEd.ProjectPackagingSettings]`
  (property at `UE:Source/Developer/DeveloperToolSettings/Classes/Settings/ProjectPackagingSettings.h:599`). Not needed for `-game` from the project dir.

### 1.2 CSS px -> Slate size (exact formula)
- `FontConstants::RenderDPI = 96` (`SlateFontInfo.h:15`); doc comment: "conversion of points to Slate Units is done at 96 DPI" (`SlateFontInfo.h:170`).
- `ComputeFontPixelSize` (`Private/Fonts/FontCacheFreeType.cpp:119-135`): `ppem = round( Size_26.6 * 96 / 72 * LayoutScale )`; then `FT_Set_Pixel_Sizes(ppem, ppem)` (`:147`).
- Therefore at layout scale 1.0: **ppem (CSS px) = Size * 4/3, so Size = cssPx * 0.75.**

| CSS px | 10 | 11 | 12 | 13 | 14 | 16 | 22 | 24 | 30 | 48 |
|---|---|---|---|---|---|---|---|---|---|---|
| Slate Size | 7.5 | 8.25 | 9 | 9.75 | 10.5 | 12 | 16.5 | 18 | 22.5 | 36 |

- `ppem` is an **integer**: a fractional CSS size (11.5 px) renders at 12. The mockup uses integer sizes; check any `rem`/`em` before converting.
- The project's `DefaultEngine.ini` has `FontDPIPreset=Standard`, `FontDPI=72`. Those only drive the UMG *editor's* size display: `FontDPIPreset` is a
  `WITH_EDITORONLY_DATA` property and `ConvertFontSizeFromDisplayToNative` just does `Display*FontDPI/96` (`UE:Source/Runtime/Engine/Private/UserInterfaceSettings.cpp:186-198`,
  `Classes/Engine/UserInterfaceSettings.h:254-272`). `FontDPI` is not a property at all. Neither changes runtime rendering. Same 0.75 factor, so consistent with the formula.
- With DPI scale s (section 3) the ppem is `round(Size*4/3*s)`, so scaling the HUD re-rasterises glyphs (fine, sharp).

### 1.3 Letter spacing, line height, weights
- **`LetterSpacing` unit = 1/1000 of `Size` (Slate units, not em of the CSS px)**: kerning-only path `px = (int16)(LS * FontScale * Size / 1000)` (`Private/Fonts/SlateTextShaper.cpp:436-438`);
  HarfBuzz path `px = (int32)(LS * Size / 1000)` **without FontScale** (`:729-731`). Both truncate toward zero to **whole pixels**, and the value is added to each glyph's integer `XAdvance` (`:502, :814`).
  `LetterSpacing` is `int32` in `[-1000, 10000]` (`SlateFontInfo.h:179`). Non-zero letter spacing also disables the `liga` feature only when the resulting px != 0 (`:735`), matching CSS.
- To get a whole-pixel target `n = round(em * cssPx)`: `LS = ceil(1000*n/Size) + 1`. Examples (kicker 12 px, .16em = 1.92 px -> n=2, Size 9 -> LS=224). The sub-pixel remainder (here .08 px/glyph,
  1.6 px over 20 chars) cannot be expressed. Table for the mockup's recurring values: .14em@14 = 1.96 -> 2; .12em@11 = 1.32 -> 1 (error .32/glyph, 6 px over 20 chars); .08em@14 = 1.12 -> 1; .1em@10 = 1 -> 1.
- **Line height**: `STextBlock::LineHeightPercentage` (`Slate/Public/Widgets/Text/STextBlock.h:68,130`) multiplies the face height; face height = `round(FT face->height scaled)` for `EFontLayoutMethod::Metrics`
  (`FontCacheFreeType.cpp:257-270`, `SlateFontRenderer.cpp:200-221`), baseline = `LineY + MaxHeight + descender` (`Private/Rendering/ElementBatcher.cpp:3486`, descender from `SlateTextShaper.cpp:318`).
  This is **not** CSS half-leading. For a fixed-height row, put the `STextBlock` in an `SBox` with `HeightOverride` and `VAlign_Center`, then correct by a per-style Y nudge found in the pixel diff.
  Font metrics (MEASURED, fontTools): Inter upm 2048, hhea asc/desc/gap 1984/-494/0 (0.969/0.241 em, content 1.21 em); Cinzel upm 1000, 976/-372; JetBrains Mono upm 1000, 1020/-300; typo == hhea for all, USE_TYPO set.
- **Text shadow** (mockup uses none, `text-shadow`: 0 hits): `STextBlock::ShadowOffset`/`ShadowColorAndOpacity` (`STextBlock.h:98-101`) draws the text twice.
  **Outline** (mockup: 0 hits of `-webkit-text-stroke`): `FFontOutlineSettings{OutlineSize(int px, scaled & rounded), OutlineColor, bMiteredCorners, bSeparateFillAlpha, bApplyOutlineToDropShadows}`
  (`SlateFontInfo.h:36-66`); rasteriser choice `Slate.OutlineFontRenderMethod` 0 = FreeType stroker (`SlateFontRenderer.cpp:28-34`).
- **Weights come from separate files.** Two facts: (a) **no variable-font support**: `grep FT_Set_Var|FT_Get_MM_Var` over `SlateCore/Private/Fonts` returns nothing, so a variable TTF
  renders only its default instance. The machine's `C:/Users/MD_Ki/AppData/Local/Microsoft/Windows/Fonts` has Inter and JetBrains Mono as **variable** files, which are therefore unusable as-is.
  (b) Use static instances: `fonttools varLib.instancer Inter-VariableFont_opsz,wght.ttf wght=600 opsz=14 -o Inter-SemiBold.ttf` (fontTools 4.63.0 is installed). A parallel task already put static
  `Cinzel-{Medium,SemiBold,Bold}`, `Inter-{Regular,Medium,SemiBold,Bold}`, `JetBrainsMono-{Regular,Medium,SemiBold}` in `docs/unreal-move/trial-checks/research/hud-ref/fonts/` (provenance UNVERIFIED; I did not write them).
  Load them as ONE `FCompositeFont` per family with named typefaces and pick with `FSlateFontInfo::TypefaceFontName` (None = first entry, `SlateFontInfo.h:166`).
  Chromium's `font-optical-sizing:auto` sets Inter's `opsz` from px size (14..32); whether the Google-Fonts-served Inter the mockup used pinned opsz is UNVERIFIED, so pick one opsz and compare.
  All three families are SIL OFL.

### 1.4 Hinting and anti-aliasing
- `EFontHinting {Default, Auto, AutoLight, Monochrome, None}` (`CompositeFont.h:24-35`) is a **per-`FFontData`** setting (set when appending the face), mapped in `SlateFontRenderer.cpp:55-82`:
  Default -> `FT_LOAD_TARGET_NORMAL` (bytecode interpreter v40, vertical-only hinting), Auto -> `FORCE_AUTOHINT`, AutoLight -> `TARGET_LIGHT`, Monochrome -> `TARGET_MONO|FORCE_AUTOHINT`, **None -> `NO_AUTOHINT|NO_HINTING`**.
  Glyphs are always rasterised `FT_RENDER_MODE_NORMAL` = 8-bit **grayscale** coverage (`SlateFontRenderer.cpp:674,684`) into an A8 atlas (`SlateElementPixelShader.usf:392-397`). There is no LCD mode.
- cvars: `Slate.EnableFontAntiAliasing` (default 1; 0 = 1-bit mono, `SlateFontRenderer.cpp:38`), `Slate.EnableLegacyFontHinting` (TT interpreter v35 vs v40, `FontCacheFreeType.cpp:21-22,359`).
  There is **no text gamma or contrast cvar for glyph coverage** (`GSlateContrast` default 1 only affects colours, `SlateCoreClasses.cpp:28`; `SlateElementPixelShader.usf:70-83`).
- Recommendation: start with `EFontHinting::None` (outline-faithful, closest to a hint-free reference); run the 3-way test {None, Default, AutoLight} against the reference and keep the lowest RMSE.
- An experimental SDF/MSDF text path exists (`Public/Fonts/FontRasterizationMode.h`, `FontCache.cpp:1327-1360`); runtime-file enablement and quality UNVERIFIED. Not recommended for pixel matching.

### 1.5 Kerning and shaping (the silent trap)
- `ETextShapingMethod::Auto` = **KerningOnly for left-to-right text** (`Public/Fonts/FontCache.h:51-62`). KerningOnly uses FreeType's legacy `kern` table via `FT_Get_Kerning` only.
- Fonts checked (MEASURED, `fontcheck.py`): all of Inter/Cinzel/JetBrains Mono static files have **no `kern` table**, kerning lives in GPOS (`GPOS features: kern, mark[, mkmk]`; JetBrains Mono: `mark` only, which is fine, monospace).
- The HarfBuzz path passes `kern` = `FT_HAS_KERNING(face) || LetterSpacing != 0` and `liga` = `(letter-spacing px == 0)` as explicit features (`SlateTextShaper.cpp:723, :733-736`).
  So full shaping alone still leaves Inter unkerned. **Recipe:** `STextBlock.TextShapingMethod(ETextShapingMethod::FullShaping)` (arg at `STextBlock.h:142`; global alternative `Slate.DefaultTextShapingMethod=2`, `FontCache.cpp:57-59`)
  **plus `LetterSpacing >= 1`** on every font used (1 yields 0 px, so ligatures stay on and GPOS kerning turns on). Chromium applies kerning and `liga`/`calt` by default; `calt` stays on in HarfBuzz defaults.
- `font-variant-numeric: tabular-nums` (2 uses in Match.dc.html) cannot be requested: the feature list is fixed. Bake `tnum` into a static instance (`pyftfeatfreeze`) or use JetBrains Mono for those numbers.
- Chromium CSS `text-transform: uppercase` (37 uses) = `STextBlock::TransformPolicy(ETextTransformPolicy::ToUpper)`; `text-overflow: ellipsis` = `OverflowPolicy(ETextOverflowPolicy::Ellipsis)` (`STextBlock.h:124,151`).

## 2. Brushes and drawing

### 2.1 Colour handling (read this before any colour)
- Slate takes `FLinearColor` and packs the vertex colour with an **sRGB encode** (`Public/Rendering/ElementBatcher.h:282-287`, `IsVertexColorInLinearSpace()==false`, `SlateRHIRenderingPolicy.h:37`);
  the vertex shader decodes to linear (`Shaders/Private/SlateVertexShader.usf:26-29`) and the pixel shader re-encodes (`SlateElementPixelShader.usf:87-98, 652-653`, `GammaCorrectionCommon.ush:65-83`, exact sRGB, `GammaCurveRatio = 2.2/DisplayGamma = 1` at the default 2.2).
  Round trip is exact, so **a CSS hex must be passed as `FLinearColor::FromSRGBColor(FColor(r,g,b))`** (`UE:Source/Runtime/Core/Public/Math/Color.h:180`).
  `FSlateColorBrush(FColor)` uses `ReinterpretAsLinear` (`SlateColorBrush.h:30`) which would treat the bytes as already linear and come out washed out; do not use that overload.
- Compositing happens in the sRGB-encoded 8-bit backbuffer (the shader does the encode itself; SDR output writes straight to the back buffer, `SlateRHIRenderer.cpp:987-1011`: the separate UI texture exists only for HDR display).
  That equals Chromium's gamma-space blending. **Windows HDR must be off for fidelity runs** (HDR composite path differs). Render-target view format itself: UNVERIFIED.

### 2.2 Rectangles, hairlines, borders (the 90% case)
- Mockup radius is 0 everywhere except keycaps (2-3 px) (`docs/ui-redesign/README.md`, Shape section); 87 `outline`, 27 `border-radius`, 11 `box-shadow` hits in Match.dc.html, and the `box-shadow` values are almost all hairline rings
  (`0 0 0 1px #2A2F38` x5, `0 0 0 2px/3px` rings) plus ONE soft shadow (`0 3px 6px rgba(0,0,0,.4)`) and ONE soft inset (`inset 0 2px 6px rgba(0,0,0,.6)`).
- `FSlateColorBrush` quads are pixel-snapped by default (`ESlateDrawEffect::NoPixelSnapping` is opt-OUT, `RenderingCommon.h:106`, `DrawElementTypes.h:349`) and crisp. A 1 px border = nested `SBorder`
  (outer colour brush with `Padding(1)`, inner fill brush). This is identical to CSS `box-sizing:border-box` + `1px solid`. A ring box-shadow = an outer `SBorder` with negative-space padding, same trick.
- **`FSlateRoundedBoxBrush` is NOT crisp even at radius 0**: the shader blends `smoothstep(+1,-1,dist)` (`Shaders/Private/SlateShaderCommon.ush:84-120`), so an on-grid edge pixel is 84% / 16%, a 2 px soft edge.
  Use it only for keycaps (radius 2-3) where Chromium also anti-aliases.
- `FSlateRoundedBoxBrush` details: corner radii `FVector4(X=TL, Y=TR, Z=BR, W=BL)` (`SlateBrush.h:200`); outline via `(Fill, Radius, OutlineColor, Width)` ctor (`SlateRoundedBoxBrush.h:165`).
  **Trap:** the 3-arg `(Fill, OutlineColor, Width)` ctor defaults `RoundingType = HalfHeightRadius` (a pill, `SlateBrush.h:138,162`; 3-arg ctor `SlateRoundedBoxBrush.h:116`); pass an explicit radius vector.

### 2.3 Gradients
- `FSlateDrawElement::MakeGradient(List, Layer, PaintGeometry, TArray<FSlateGradientStop>, EOrientation, ESlateDrawEffect, FVector4f CornerRadius)` (`DrawElementTypes.h:180`): **axis-aligned only** (horizontal or vertical), N stops
  (`FSlateGradientStop{Position, Color}`, `DrawElementTypes.h:396-408`). No diagonal, no radial, no conic.
- **Interpolation is in linear light** (vertex colours are decoded to linear before the rasteriser interpolates; section 2.1), while CSS interpolates in sRGB. A dark-to-transparent ramp therefore looks lighter in Slate. Fix: emit
  a dense stop list (8-16) sampled from the CSS curve in sRGB. Alpha: Slate vertex alpha is straight, CSS premultiplies: also pre-sample.
- Mockup inventory (Match.dc.html): 12 `linear-gradient` (mostly 135/160/180 deg terrain/portrait stand-ins; vertical ones map to `MakeGradient`; `90deg,#4A4234,#8A6E3C 50%,#4A4234` = horizontal 3-stop), 11 `radial-gradient`
  (vignettes and portrait glows), 2 `repeating-*`, 3 `clip-path: polygon` (silhouettes), `mask` x2. README says backdrop, minimap terrain and portraits are placeholders to be replaced by renders.
- For radial/diagonal/polygon/mask: **CPU-bake a BGRA bitmap at startup** and draw it as an image brush (route in 2.4), or `FSlateDrawElement::MakeCustomVerts` with per-vertex colour (`DrawElementTypes.h:305`, linear interpolation, triangle fans for vignettes).

### 2.4 Image brushes from PNG at runtime
- **Slate loads PNG/JPG itself** for a dynamic brush: `FSlateDynamicImageBrush(FName AbsolutePath, FVector2f Size, FLinearColor Tint)` (`Brushes/SlateDynamicImageBrush.h:69-80`) sets `bIsDynamicallyLoaded`; on first draw
  `FSlateRHIResourceManager::LoadTexture` calls `FImageUtils::LoadImage(path)` and converts to BGRA8 sRGB (`SlateRHIResourceManager.cpp:514-531, 889-897`). No `IImageWrapper` needed. Keep the brush alive (it is `TSharedFromThis`; call `ReleaseResource()` at teardown).
- Procedural bitmaps: `FSlateDynamicImageBrush::CreateWithImageData(FName UniqueName, FVector2f Size, TArray<uint8> BGRA, Tint)` (`SlateDynamicImageBrush.h:90`). The renderer **hard-codes BGRA8 sRGB**
  (`SlateRHIResourceManager.cpp:729`), so write sRGB bytes. Use this for baked gradients, dashed borders, soft shadows. Only use `IImageWrapper` (module `ImageWrapper`) if you must decode a PNG yourself.
- Sampling is bilinear; keep 1:1 texel:pixel (snapped vertices) to stay exact.

### 2.5 SVG icons at runtime: YES, supported in 5.8
- `FSlateVectorImageBrush(FString AbsSvgPath, FVector2f Size, FLinearColor Tint)` (`SlateImageBrush.h:85-93`) sets `ESlateBrushImageType::Vector`; `FSlateRHIResourceManager::GetShaderResource` routes Vector brushes to `FSlateVectorGraphicsCache`
  (`SlateRHIResourceManager.cpp:645-648`), which rasterises **at LocalSize x DrawScale** with `FSlateSVGRasterizer` = **nanosvg** (`Private/Rendering/SlateSVGRasterizer.cpp:16-60`, linked in `SlateCore.Build.cs:50`),
  atlas page 1024 (`SlateVectorGraphicsCache.cpp:84`). Resolution-correct at any HUD scale.
- nanosvg (`UE:Source/ThirdParty/nanosvg/src/nanosvg.h`) parses `rect, circle, ellipse, line, polyline, polygon, path` and `stroke-dasharray` (`:162, :1811-1890, :2888-2913`). `docs/ui-redesign/icons.js`
  is a primitive list on a 24 px grid with 1.5 stroke (`["polygon",{points}]`, `["path",{d}]`, `["rect",..,"stroke-dasharray":"2.5 2"]`): a 20-line script can emit one `<svg viewBox="0 0 24 24" stroke="#fff" fill="none" stroke-width="1.5">` per icon, then tint at draw time.
  Limits: no CSS, filters or `currentColor`; draw white and tint. AA quality vs Chromium: UNVERIFIED (compare icon crops first; fallback = pre-rasterise with the same bake path as 2.4).
  Ornaments (`ornaments.js`: circles, hexagrams, medallions) are also primitives; same route.

### 2.6 Shadows, blur, dashes
- `SBackgroundBlur` (`Slate/Public/Widgets/Layout/SBackgroundBlur.h`) blurs everything beneath; cvar `Slate.AllowBackgroundBlurWidgets`; **cost not measurable from source (UNVERIFIED)**. README states "There is no blur"; not needed.
- Soft box-shadows (2 in board 3.1): bake a 9-slice or a small bitmap (2.4). No native shadow in `FSlateBrush`.
- **Dashed 1 px border** (10 uses): no native brush. **MEASURED Chromium** (121x41 box, `1px dashed`, grayscale AA): dash = 3 px solid, gap ~= 1.92 px, spacing re-fitted to the side length so dashes end flush
  (coverage row: `1,1,1,0, .08,1,1,.92, 0, .17,1,1,.83, 0, ...`, i.e. a 4.92 px period drifting by .08 px per dash). Slate boxes cannot do fractional dash ends (no feathering: `GSlateFeathering=0`,
  `ElementBatcher.cpp:36`), so drawing integer dashes is within 1 px. For exactness bake the pattern with the same arithmetic into a tiled bitmap (2.4).
- Lines: `MakeLines(List, Layer, Geo, TArray<FVector2f>, Effects, Tint, bAntialias=true, Thickness)` (`DrawElementTypes.h:230`), splines `MakeSpline/MakeCubicBezierSpline` (`:195,210`), rotated box `MakeRotatedBox` (`:110`), text `MakeText` (`:134-138`).

### 2.7 Layout primitives
- Absolute px placement: `SConstraintCanvas` slots with `Anchors`, `Offset`, `Alignment`. Per axis: stretch if `Anchors.Min != Anchors.Max` (Offset = L/T/R/B margins), else point anchor (Offset = X, Y, **SizeX, SizeY**);
  default Alignment is (0.5,0.5), so set `(0,0)` for top-left (`Slate/Private/Widgets/Layout/SConstraintCanvas.cpp:246-275`, defaults `SConstraintCanvas.h:40-44`).

## 3. DPI: 1 Slate unit = 1 px at 1920x1080, and how a widget gets into the viewport

- `UGameViewportClient::AddViewportWidgetContent(TSharedRef<SWidget>, int32 ZOrder = 0)` adds a slot to `ViewportOverlayWidget` (`Engine/Private/GameViewportClient.cpp:3376-3385`, decl `Classes/Engine/GameViewportClient.h:258`).
  Larger ZOrder = on top. That overlay is the content of `SGameLayerManager`'s viewport layer, which sits **inside an `SDPIScaler`** (`Private/Slate/SGameLayerManager.cpp:113-141`; viewport wiring `GameViewportClient.cpp:1302-1326`).
  So raw Slate added this way IS DPI-scaled by the project's UI scale settings. `RemoveViewportWidgetContent(TSharedRef)` removes it (`:3387`).
- Scale = `UUserInterfaceSettings::GetDPIScaleBasedOnSize(viewport size) * ApplicationScale`, then divided by the platform window scale (`SGameLayerManager.cpp:467-498`, `UserInterfaceSettings.cpp:87-162`).
  **Defaults** (`UE:Config/BaseEngine.ini:1473-1477`): `UIScaleRule=ShortestSide`, curve keys (480,.444) (720,.666) (**1080, 1.0**) (8640, 8.0), linear between; `bAllowHighDPIInGameMode=False`; `ApplicationScale=1`
  (`UserInterfaceSettings.cpp:17`). So at exactly 1920x1080 the default is already 1.0, but 1920x1200 would give 1.11. The project `DefaultEngine.ini` has no scale keys (read: only `FontDPIPreset`, `FontDPI`).
- **Recommended `DefaultEngine.ini`** (a later edit, not made now): make the scale a pure function of the 1920x1080 design size, which also gives the README's "UI scale 80-150%" via `ApplicationScale`:
  ```ini
  [/Script/Engine.UserInterfaceSettings]
  UIScaleRule=ScaleToFit
  DesignScreenSize=(X=1920,Y=1080)
  ApplicationScale=1.0
  bAllowHighDPIInGameMode=False
  ```
  `ScaleToFit` returns `min(W/1920, H/1080)` (`UserInterfaceSettings.cpp:155-156`, enum `UserInterfaceSettings.h:30-46`, `DesignScreenSize` `:200`).
- The only remaining ways to break "1 unit = 1 px" are Windows display scaling combined with `bAllowHighDPIInGameMode=True` and `r.ScreenPercentage` (Slate is not scaled by it, but keep 100 for fidelity runs). Machine display scale: UNVERIFIED.
- Which class adds the widget: `AHUD::BeginPlay` (or `AGameModeBase` -> `HUDClass`, `GameModeBase.h:104`). Selecting the game mode without an asset: URL option `?game=/Script/ProjectChimera.ChimeraHudGameMode`
  (`Engine/Private/GameInstance.cpp:1514-1537`) or `GlobalDefaultGameMode=` under `[/Script/EngineSettings.GameMapsSettings]` (`GameMapsSettings.h:121`).
- Hit testing: a root `SCompoundWidget` is `Visible` by default and swallows 3D-view clicks; call `SetVisibility(EVisibility::SelfHitTestInvisible)` on the root and let only real controls be `Visible`.

## 4. Screenshots: the final frame including UI, exactly 1920x1080, automated

- **`HighResShot` does NOT include Slate UI.** It re-renders the scene into a dummy viewport (`UnrealClient.cpp:1719-1721`, `GameViewportClient.cpp:4262-4271`). The look-test tooling used it (`tools/unreal-looktest/run_fps.ps1:72`); it is wrong for HUD checks.
- UI-inclusive capture: `FScreenshotRequest::RequestScreenshot(FString Filename, bool bShowUI, bool bAddUniqueSuffix, bool bHdr, FIntRect, bool bRestrictToGameViewport)` (`Engine/Public/UnrealClient.h:219`).
  With `bShowUI` the engine calls `FSlateApplication::TakeScreenshot`, which **re-draws the window into a readback of the widget's rect** (`GameViewportClient.cpp:2380-2413`, `Slate/Private/Framework/Application/SlateApplication.cpp:4429-4450`).
  Pass `bRestrictToGameViewport=true` (only reachable from C++, the console command leaves it false) so a title bar or editor chrome can never offset the crop.
  Alpha is forced to 255 (`GameViewportClient.cpp:2540-2548`).
- Console form: `Shot SHOWUI -nosuffix filename=D:/path/hud.png` (`GameViewportClient.cpp:3585-3588, 4286-4306`; `SHOWUI` must be the first token or `-showui` anywhere; `-nosuffix` keeps the exact name; `.png` appended if no extension, `UnrealClient.cpp:242-246`).
  A name containing `/` or `\` is used verbatim, otherwise it lands in `GameScreenshotSaveDirectory` (`UnrealClient.cpp:302-307`; the default value of that directory is UNVERIFIED, so always pass an absolute path).
  **`-ExecCmds` fires at startup, before the HUD has drawn**, and there is no delay syntax. So the robust automation is in-process (section 6 `HudShot`): wait N frames with `FTSTicker` + `GFrameCounter`, request the shot, and quit from
  `FScreenshotRequest::OnScreenshotRequestProcessed()` (`UnrealClient.h:266`, fired after the file is written, `GameViewportClient.cpp:2583`) with `FPlatformMisc::RequestExit(false)`.
- Resolution: **`-game -fullscreen -ResX=1920 -ResY=1080`** is proven on this machine (`run_fps.ps1:67`, and its check at `:112-118` greps `Scene viewport resized to 1920x1080`; reuse that proof). `-windowed` is allowed at 1920x1080 on a 1080p display
  (`GameEngine.cpp:355-358, 417` only rejects sizes larger than the desktop; `-ForceRes` skips the check, `:414`) but the OS title bar makes the window taller than the screen, so it may be moved or clipped: **UNVERIFIED here, avoid.**
  Runtime change: the console command is **`SetRes 1920x1080f`** (`w` windowed, `wf` windowed-fullscreen; `GameViewportClient.cpp:3573-3576, 4222-4256`); an `r.SetRes` alias is not in the source (grep).
- Fidelity-run flags: `-game -fullscreen -ResX=1920 -ResY=1080 -nosound -unattended -nosplash -ExecCmds="t.MaxFPS 60,r.VSync 0,r.ScreenPercentage 100" -HudShot=D:/.../out/hud.png -ABSLOG=...`. Disable Windows HDR.
- File lands exactly at the given absolute path. Verify `ls` + PNG dims = 1920x1080 in the script (as `run_fps.ps1` does).

## 5. Text differences against Chromium, and the settings that close them

| # | Difference | Evidence | Closest Slate setting |
|---|---|---|---|
| 1 | **Chromium here uses LCD (subpixel RGB) AA by default; Slate is grayscale** | MEASURED: `playwright-cli` default render of Inter 13 px: 2344 of 2861 text pixels have unequal per-channel coverage (R/G/B shifted, e.g. `[0.55,0.81,0.97]`). With launch args `--disable-lcd-text --font-render-hinting=none`: **0 of 2861** | Produce the reference with grayscale: `playwright-cli open <url> --config=g.json` where `g.json = {"browser":{"launchOptions":{"args":["--disable-lcd-text","--font-render-hinting=none"]}}}` (MEASURED to work). The mockup's `-webkit-font-smoothing:antialiased` is a no-op on Windows. |
| 2 | Integer glyph advances in Slate (`int16 XAdvance`, rounded per glyph) vs Chromium fractional | `SlateTextShaper.cpp:476, 797` (`Convert26Dot6ToRoundedPixel<int16>`); `FShapedGlyphEntry::XAdvance` is `int16` (`FontCache.h:153`). MODEL (fontTools hmtx, Inter-Regular, 7 mockup strings, no kerning): at 13 px mean width 171.6 exact vs 173.6 rounded, worst string +5.2 px; at 16 px worst 6.5 px; "Locked buttons say what they need" 218.99 vs 222. Chromium measured 135.125 px for "Command card · build" vs fontTools exact 135.21 (so exact sum is the right model) | `EFontHinting::None`. No cvar removes the rounding. If widths matter: section 5.1 |
| 3 | Kerning absent for these fonts (GPOS only) | section 1.5, MEASURED tables | `FullShaping` + `LetterSpacing>=1` |
| 4 | Letter spacing truncated to whole px | section 1.3 | `LS = ceil(1000*n/Size)+1`; or section 5.1 |
| 5 | Hinting: Slate default is FreeType v40 vertical hinting; Chromium hinting unknown here | `FontCacheFreeType.cpp:359`; Chromium side UNVERIFIED (flag `--font-render-hinting=none` pins it for the reference) | Run the 3-way hinting test; reference pinned to `none` |
| 6 | Gamma/contrast of glyph coverage: Chromium/Skia applies a text-gamma LUT, Slate uses raw coverage with straight alpha in sRGB space | Slate: shader multiplies colour by A8 coverage (`SlateElementPixelShader.usf:408-420`); Skia LUT behaviour on Windows UNVERIFIED | No cvar. Compensate per colour pair if a diff shows systematic thin/bold text (tint alpha or next font weight) |
| 7 | Glyph quads are pixel-snapped; Chromium positions at subpixel (Skia 1/4 px) | `AddShapedTextElement<Rounding::Enabled>` default, `ElementBatcher.cpp:410-421` | Leave snapped (crisper); each glyph is <= 0.5 px off its ideal origin |
| 8 | Vertical metrics: Chromium rounds ascent/descent and centres half-leading; Slate rounds face height and baseline | `ElementBatcher.cpp:3486`; Blink rounding UNVERIFIED | Per-style Y nudge from the diff |
| 9 | `ppem` integer (no fractional font sizes) | section 1.2 | Use integer CSS px |

### 5.1 Contingency: `SChimeraText` (fractional glyph placement without extra dependencies)
Measure at 16x then place per glyph: `FSlateFontMeasure::Measure(FStringView, FSlateFontInfo, float FontScale)` (`Public/Fonts/FontMeasure.h:29`, obtained from
`FSlateApplication::Get().GetRenderer()->GetFontMeasureService()`, `Rendering/SlateRenderer.h:136`) with `FontScale=16` gives advances quantised to 1/16 px (includes kerning when shaped with FullShaping). Prefix widths `W(0..i)/16 + i*letterSpacingPx`
give exact fractional x for glyph `i`; emit one `FSlateDrawElement::MakeText` per glyph (or per word) at `round(x)`. Error is <= 0.5 px per glyph and **non-cumulative**, versus unbounded drift today. Cost: re-shaping per frame, so cache the placement
and wrap static panels in an invalidation panel. Build this only if the first diff shows tail drift on key labels.

## 6. Compile-ready skeleton (verified against the headers named; NOT compiled, a UE build is out of scope in this phase)

Layout under `Source/ProjectChimera/Hud/`. Everything lives in the existing primary game module (`ProjectChimera.Build.cs` already has the commented Slate line).

`ProjectChimera.Build.cs`: replace the commented line with
```csharp
PrivateDependencyModuleNames.AddRange(new string[] { "Slate", "SlateCore" });
```
(`Core, CoreUObject, Engine, InputCore, EnhancedInput` are already public; `UnrealClient.h`, `GameViewportClient.h`, `FTSTicker`, `FPlatformMisc` are in Engine/Core.)

`ChimeraUi.h`
```cpp
#pragma once
#include "CoreMinimal.h"
#include "Fonts/SlateFontInfo.h"
#include "Fonts/CompositeFont.h"

namespace ChimeraUi
{
	/** CSS #RRGGBB (sRGB) to the FLinearColor Slate expects. See ElementBatcher.h:282-287. */
	inline FLinearColor Hex(uint32 Rgb, float Alpha = 1.f)
	{
		FLinearColor C = FLinearColor::FromSRGBColor(FColor((Rgb >> 16) & 0xFF, (Rgb >> 8) & 0xFF, Rgb & 0xFF, 255));
		C.A = Alpha;
		return C;
	}

	enum class EFace : uint8 { Inter, Cinzel, Mono };

	/** CSS px + weight + letter-spacing (em) -> FSlateFontInfo. Weight is one of 400/500/600/700. */
	FSlateFontInfo Font(EFace Face, int32 Weight, float CssPx, float LetterSpacingEm = 0.f);
}
```

`ChimeraUi.cpp`
```cpp
#include "ChimeraUi.h"
#include "Misc/Paths.h"

namespace
{
	/** One composite font per family, one named typeface per weight file. Kept alive for the process (FGCObject). */
	TSharedRef<FStandaloneCompositeFont> MakeFamily(const TCHAR* Dir, TArrayView<const TPair<const TCHAR*, const TCHAR*>> Faces)
	{
		TSharedRef<FStandaloneCompositeFont> Family = MakeShared<FStandaloneCompositeFont>();
		for (const TPair<const TCHAR*, const TCHAR*>& Face : Faces)
		{
			// EFontHinting::None: see section 1.4. LazyLoad: whole file in RAM, no per-glyph file IO.
			Family->DefaultTypeface.AppendFont(FName(Face.Key),
				FPaths::ProjectContentDir() / TEXT("ChimeraUi/Fonts") / Dir / Face.Value,
				EFontHinting::None, EFontLoadingPolicy::LazyLoad);
		}
		return Family;
	}

	const TSharedRef<FStandaloneCompositeFont>& Family(ChimeraUi::EFace Face)
	{
		static const TPair<const TCHAR*, const TCHAR*> InterFaces[] = {
			{TEXT("400"), TEXT("Inter-Regular.ttf")}, {TEXT("500"), TEXT("Inter-Medium.ttf")},
			{TEXT("600"), TEXT("Inter-SemiBold.ttf")}, {TEXT("700"), TEXT("Inter-Bold.ttf")} };
		static const TPair<const TCHAR*, const TCHAR*> CinzelFaces[] = {
			{TEXT("500"), TEXT("Cinzel-Medium.ttf")}, {TEXT("600"), TEXT("Cinzel-SemiBold.ttf")}, {TEXT("700"), TEXT("Cinzel-Bold.ttf")} };
		static const TPair<const TCHAR*, const TCHAR*> MonoFaces[] = {
			{TEXT("400"), TEXT("JetBrainsMono-Regular.ttf")}, {TEXT("500"), TEXT("JetBrainsMono-Medium.ttf")}, {TEXT("600"), TEXT("JetBrainsMono-SemiBold.ttf")} };
		static const TSharedRef<FStandaloneCompositeFont> Inter  = MakeFamily(TEXT("Inter"), InterFaces);
		static const TSharedRef<FStandaloneCompositeFont> Cinzel = MakeFamily(TEXT("Cinzel"), CinzelFaces);
		static const TSharedRef<FStandaloneCompositeFont> Mono   = MakeFamily(TEXT("JetBrainsMono"), MonoFaces);
		return Face == ChimeraUi::EFace::Inter ? Inter : Face == ChimeraUi::EFace::Cinzel ? Cinzel : Mono;
	}
}

FSlateFontInfo ChimeraUi::Font(EFace Face, int32 Weight, float CssPx, float LetterSpacingEm)
{
	const float Size = CssPx * 0.75f;                                   // section 1.2: px * 72/96
	FSlateFontInfo Info(Family(Face).ToSharedPtr(), Size, FName(*FString::FromInt(Weight)));
	// Section 1.3/1.5: whole-pixel letter spacing; LS >= 1 also switches HarfBuzz 'kern' (GPOS) on.
	const int32 Px = FMath::RoundToInt(LetterSpacingEm * CssPx);
	Info.LetterSpacing = Px > 0 ? FMath::CeilToInt(1000.f * Px / Size) + 1 : 1;
	return Info;
}
```
(`ToSharedPtr()` on `TSharedRef<FStandaloneCompositeFont>` converts to `TSharedPtr<const FCompositeFont>`; if the compiler objects, write `TSharedPtr<const FCompositeFont>(Family(Face))`.)

`SChimeraMatchHud.h`
```cpp
#pragma once
#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Brushes/SlateColorBrush.h"
#include "ChimeraUi.h"

/** Board 3.1 chrome. Root is click-through; only real controls are hit-testable. */
class SChimeraMatchHud : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraMatchHud) {}
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

private:
	// Brushes must outlive the widgets that point at them (SBorder stores a raw FSlateBrush*).
	FSlateColorBrush Surface1{ChimeraUi::Hex(0x1C1F25)};   // HUD console
	FSlateColorBrush Hairline{ChimeraUi::Hex(0x2A2F38)};
	FSlateColorBrush Void{ChimeraUi::Hex(0x0B0C0E)};
};
```

`SChimeraMatchHud.cpp`
```cpp
#include "SChimeraMatchHud.h"
#include "Widgets/Layout/SConstraintCanvas.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Text/STextBlock.h"
#include "Fonts/FontCache.h"          // ETextShapingMethod

void SChimeraMatchHud::Construct(const FArguments& InArgs)
{
	SetVisibility(EVisibility::SelfHitTestInvisible);   // never swallow 3D-view clicks

	ChildSlot
	[
		SNew(SConstraintCanvas)

		// Top strip: full width, 40 px tall, 1 px hairline at the bottom (nested-border trick, section 2.2).
		+ SConstraintCanvas::Slot()
		.Anchors(FAnchors(0.f, 0.f, 1.f, 0.f))          // stretch in X, point in Y
		.Offset(FMargin(0.f, 0.f, 0.f, 40.f))           // L=0, T=0, R=0 (margin), Bottom=40 (height)
		.Alignment(FVector2D(0.f, 0.f))
		[
			SNew(SBorder).BorderImage(&Hairline).Padding(FMargin(0.f, 0.f, 0.f, 1.f))
			[
				SNew(SBorder).BorderImage(&Surface1).Padding(FMargin(16.f, 0.f))
				.VAlign(VAlign_Center)
				[
					SNew(STextBlock)
					.Text(FText::FromString(TEXT("Round 2 \u00B7 Batch 3")))
					.Font(ChimeraUi::Font(ChimeraUi::EFace::Inter, 600, 12.f, 0.16f))
					.ColorAndOpacity(ChimeraUi::Hex(0xC9A86A))
					.TransformPolicy(ETextTransformPolicy::ToUpper)
					.TextShapingMethod(ETextShapingMethod::FullShaping)   // section 1.5
				]
			]
		]
	];
}
```
(`SBorder` args `BorderImage`, `Padding`, `VAlign`: `Slate/Public/Widgets/Layout/SBorder.h:59-66`; `STextBlock` args `STextBlock.h:83-142`.)

`ChimeraHud.h` / `ChimeraHud.cpp`
```cpp
// ChimeraHud.h
#pragma once
#include "CoreMinimal.h"
#include "GameFramework/HUD.h"
#include "ChimeraHud.generated.h"

UCLASS()
class AChimeraHud : public AHUD
{
	GENERATED_BODY()
protected:
	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type Reason) override;
private:
	TSharedPtr<SWidget> Root;
};
```
```cpp
// ChimeraHud.cpp
#include "ChimeraHud.h"
#include "SChimeraMatchHud.h"
#include "Engine/World.h"
#include "Engine/GameViewportClient.h"
#include "UnrealClient.h"               // FScreenshotRequest
#include "Containers/Ticker.h"          // FTSTicker
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "HAL/PlatformMisc.h"
#include "CoreGlobals.h"                // GFrameCounter

void AChimeraHud::BeginPlay()
{
	Super::BeginPlay();

	UGameViewportClient* Viewport = GetWorld() ? GetWorld()->GetGameViewport() : nullptr;   // null on servers/commandlets
	if (!Viewport) { return; }

	Root = SNew(SChimeraMatchHud);
	Viewport->AddViewportWidgetContent(Root.ToSharedRef(), /*ZOrder*/ 10);                    // GameViewportClient.cpp:3376

	// Automated fidelity capture: -HudShot="D:/path/hud.png"  (section 4)
	FString ShotPath;
	if (FParse::Value(FCommandLine::Get(), TEXT("HudShot="), ShotPath) && !ShotPath.IsEmpty())
	{
		const uint64 StartFrame = GFrameCounter;
		FTSTicker::GetCoreTicker().AddTicker(TEXT("ChimeraHudShot"), 0.f, [ShotPath, StartFrame](float) -> bool
		{
			if (GFrameCounter < StartFrame + 90) { return true; }   // let fonts load and layout settle
			FScreenshotRequest::OnScreenshotRequestProcessed().AddLambda([]
			{
				FPlatformMisc::RequestExit(false, TEXT("HudShot"));
			});
			FScreenshotRequest::RequestScreenshot(ShotPath, /*bShowUI*/ true, /*bAddUniqueSuffix*/ false,
				/*bHdr*/ false, FIntRect(), /*bRestrictToGameViewport*/ true);
			return false;                                            // one-shot
		});
	}
}

void AChimeraHud::EndPlay(const EEndPlayReason::Type Reason)
{
	if (Root.IsValid())
	{
		if (UGameViewportClient* Viewport = GetWorld() ? GetWorld()->GetGameViewport() : nullptr)
		{
			Viewport->RemoveViewportWidgetContent(Root.ToSharedRef());
		}
		Root.Reset();
	}
	Super::EndPlay(Reason);
}
```

`ChimeraHudGameMode.h` (header-only is fine; `.cpp` empty stub)
```cpp
#pragma once
#include "GameFramework/GameModeBase.h"
#include "ChimeraHud.h"
#include "ChimeraHudGameMode.generated.h"

UCLASS()
class AChimeraHudGameMode : public AGameModeBase
{
	GENERATED_BODY()
public:
	AChimeraHudGameMode() { HUDClass = AChimeraHud::StaticClass(); DefaultPawnClass = nullptr; }
};
```
Run (after a build, outside this phase):
`UnrealEditor.exe ProjectChimera.uproject "/Engine/Maps/Entry?game=/Script/ProjectChimera.ChimeraHudGameMode" -game -fullscreen -ResX=1920 -ResY=1080 -nosound -unattended -HudShot=D:/.../out/hud.png`
(From Git Bash set `MSYS_NO_PATHCONV=1`, per `PLAN_DELTA.md` D7.) Font files go in `Content/ChimeraUi/Fonts/{Inter,Cinzel,JetBrainsMono}/`. Missing-`#include` risk is the main uncertainty: `FAnchors` comes via `Widgets/Layout/Anchors.h`
(already pulled by `SConstraintCanvas.h:15`).

## 7. Build-order recommendation, risks, open questions

**Do this order (each step is one screenshot diff):** (1) grayscale 1920x1080 Chromium reference of board 3.1 via the `--disable-lcd-text --font-render-hinting=none` config; (2) skeleton above + top strip + one panel, `HudShot`, diff;
(3) 3-way hinting test and kerning on/off test on one label row; (4) crisp rect/hairline/keycap vocabulary, then SVG icons, then baked bitmaps; (5) only if text tails drift, build `SChimeraText` (5.1).
A diff metric should tolerate text-edge AA (it cannot be zero), so judge text by glyph-origin error and rect/icon areas by exact pixels.

**Risks**
- Text can only be "within about 1-3 px of tail drift and different AA texture" without section 5.1 work; a pure pixel-equal diff on text will never pass. Decide the acceptance rule first.
- Kerning/letter-spacing quirks are silent (no warning). A label that looks right in English can still be unkerned; add a one-line test string per font.
- Rounded-box softness and linear-light gradients are easy to introduce by default; both change colours visibly on dark UI.
- Variable fonts silently render only the default instance.
- Windows HDR or display scaling can change the UI path or scale; record both in the run log.
- The reference may have been produced with LCD text by whoever made `hud-ref/board-3.1a-with-backdrop-1920x1080.png` (UNVERIFIED); re-render it with the grayscale config before comparing.
- Nothing here was compiled; first build may need include/signature fixes. `ComputeDesiredSize` of any custom leaf must return `FVector2D` in 5.8 (`SLeafWidget.h:60`); `ToPaintGeometry(LocalSize, FSlateLayoutTransform)` is the non-deprecated form (`Geometry.h:329`).

**UNVERIFIED list:** loose-font packaging beyond the setting name; render-target view format (sRGB vs UNORM) of the SDR back buffer; nanosvg AA parity with Chromium; Blink ascent/descent rounding; Skia text-gamma on Windows;
`SBackgroundBlur` cost; whether `hud-ref/fonts` were generated with the intended `opsz`; `-windowed` 1920x1080 on this display; Windows display scale on this machine; SDF text on runtime-file fonts.

**Open questions for Alec**
1. Acceptance rule for "pixel-faithful" text: a numeric tolerance (e.g. rect/icon areas exact, text within X% edge difference), or glyph-origin error <= 1 px?
2. Can the mockup reference be re-rendered with grayscale AA (my recommendation), or must UE match LCD text? (Slate cannot produce LCD text.)
3. Cinzel/Inter/JetBrains Mono confirmed as final (README: "Alec may commission a custom font")? A custom font changes the kerning/`kern`-table analysis.

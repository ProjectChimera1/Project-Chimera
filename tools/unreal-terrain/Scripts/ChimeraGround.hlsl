// Project Chimera terrain trial (plan C 3.5; C7, G1 look pass). Original Chimera code.
// Body of the single Custom node of /Game/Terrain/M_ChimeraGround; make_ground_material.py reads this file into
// UMaterialExpressionCustom::Code (MaterialExpressionCustom.h). The translator wraps it in a function whose parameters are the
// inputs below (HLSLMaterialTranslator.cpp CustomExpression): a texture object input X arrives as `Texture2D X, SamplerState XSampler`
// (each with its asset's own sampler state), an LWC vector input (WP) arrives demoted to float3, and every additional output is an
// `inout` parameter. The main output (CMOT_Float3) is the base colour.
//
// Inputs:  WP (absolute world position, cm), VN (VertexNormalWS), PD (PixelDepth, cm), SplatTex (RGBA8 weights R Grass, G Dirt,
//          B Rock, A Snow; linear, clamp), NoiseTex (macro variation), <Layer>C / <Layer>N / <Layer>ARH for Grass, Dirt, Rock, Snow
//          (albedo sRGB; BC5 normal; AO, rough, height), and the scalars listed in make_ground_material.py SCALARS.
//          G1 round 1: GrassC carries the grass height in alpha (fetch_textures.py HEIGHT_IN_ALPHA); GrassARH is no longer read.
// Outputs: return = BaseColor, CgNormal (tangent space), CgRough, CgAO, CgEmissive (brush ring).
//
// Maths (plan C 3.5):
//  * splat UV from absolute world position: u = (X/100 + E) / (2E), v likewise (E = HalfExtentM), sampled at mip 0 (the splat has one).
//  * world-aligned top-projection UVs at two scales per layer (P / Tile and P * Scale2 / Tile). G1: the small scale is used near and
//    the large one takes over with view depth (FarStartM, FarRangeM); a scale whose weight is ~0 is not fetched (risk 5 fallback 1:
//    most pixels pay one fetch per map instead of two); repetition is broken by a low-frequency UV warp whose amplitude follows the
//    tile in use (WarpNearM near, WarpM far; round 1: the full warp sheared the near tile into swirls).
//  * auto-rock where N.z < RockCos (cos 35 deg), ramped over +-RockBand; rock is projected on three axes so steep faces do not stretch.
//    Round 1: a dirt scree toe where the auto-rock ramp is partial (ScreeStrength), sharper triplanar weights (pow 8).
//  * height blend  w'_i = max(0, w_i + h_i*depth - max_j(w_j + h_j*depth) + depth), normalised; snow fills rock hollows (SnowFill).
//  * only layers with weight are sampled: every fetch is SampleGrad with gradients taken outside the branches, so the branches are legal
//    and a pure-grass pixel pays for grass only (risk 5: the material's GPU cost).
//  * G1 colour (ALU only, inside the existing branches): per-layer saturation and value; meadow patches (round 1: built from both noise
//    fetches at unrelated, rotated periods, warped into streaks, with brightness as well as hue, and drier on sun-facing slopes);
//    grain contrast and a grain-keyed hue split from the grass height in the albedo alpha; a trampled halo beside paths; dirt contrast and
//    tint; moss on rock ledges; snow tint; a roughness floor (RoughMin).
//  * G1 round 2: a long patch octave (three rotated sines at PatchLongM x 1, 1.25, 1.54; ALU, no fetch) so dry zones gather into
//    fewer, larger fields; no sun-facing dryness on rock, scree or path (Tie cut by the dirt and rock weights); a clump term (one more
//    NoiseTex fetch at ClumpM, rotated +63 deg and warped, inside the grass weight branch, multiplied with the 26.6 m noise so it never
//    repeats) that varies grass value, chroma and hue and clusters the yellow blade tips; the same clump noise breaks the grass height
//    at path and rock edges; contact darkening of grass at rock; a darker scree toe and a compacted path centre; rock macro value and
//    cavity darkening; moss broken by the patch noise.
//  * G1 round 3: the clump noise is fetched before the auto-rock, inside the grass-or-dirt splat branch, so it can also break the
//    auto-rock threshold (no triangle teeth), the scree apron, the path crown and the snow edge; a bimodal patch field (soft-edged
//    fields, not blobs) with sun-facing dryness only on real slopes; tussocks; worn twin ruts, a grassy crown
//    and a paler worn margin on paths; a material-only micro-relief normal from the noise already fetched; rock side projections
//    rotated, warped and on their own tile, lichen and lighter upward faces; less grain and a broken edge on snow. ALU only: the
//    texture fetch count is unchanged.
//  * G1 round 4: round 2's sunlit lime palette with round 3's variety: a brighter, yellower lush end and a paler straw dry end; soft
//    field edges dithered by the clump noise; the closeup whorls removed (the 5 m clump noise was warped by 4 m, ClumpWarpM 4 -> 1);
//    the tussocks and the micro-relief normal dropped (ALU); rock veins compressed;
//    the grass normal not fetched beyond GrassNFarM (cost). Lighting changes are text in DefaultGame.ini LookOverrides.
//  * brush ring from BrushX/BrushY/BrushRadius (metres, XY distance, so it follows the surface); BrushRadius = 0 draws nothing.

float2 P = WP.xy * 0.01;
float PZ = WP.z * 0.01;
float2 dPx = ddx(P);
float2 dPy = ddy(P);

// Splat weights.
float2 SplatUV = (P + HalfExtentM) / (2.0 * HalfExtentM);
float4 W = max(Texture2DSampleLevel(SplatTex, SplatTexSampler, SplatUV, 0.0), 0.0);
W /= max(dot(W, float4(1.0, 1.0, 1.0, 1.0)), 1e-4);
float3 Nw = normalize(VN);

// Noise. Round 1: both fetches use rotated UVs (NoiseB by -41 deg, the patch fetch by +27 deg) so the combined field does not repeat
// inside the map and does not line up with its axes; the patch UV is warped by NoiseB into streaks (PatchWarpM metres).
float2 PB = float2(0.7547 * P.x + 0.6561 * P.y, -0.6561 * P.x + 0.7547 * P.y);
// Round 3 (cost): the rotated UVs' gradients are taken with ddx/ddy (the same values as rotating dPx/dPy, fewer instructions).
float2 dPBx = ddx(PB);
float2 dPBy = ddy(PB);
float2 PR = float2(0.8910 * P.x - 0.4540 * P.y, 0.4540 * P.x + 0.8910 * P.y);
float2 dPRx = ddx(PR);
float2 dPRy = ddy(PR);
float2 PCr = float2(0.4540 * P.x - 0.8910 * P.y, 0.8910 * P.x + 0.4540 * P.y);
float2 dPCx = ddx(PCr);
float2 dPCy = ddy(PCr);
float3 NoiseB = Texture2DSampleGrad(NoiseTex, NoiseTexSampler, PB / WarpNoiseM + float2(0.31, 0.67), dPBx / WarpNoiseM, dPBy / WarpNoiseM).rgb;
float2 PRw = PR + PatchWarpM * (NoiseB.rb - 0.5) * 2.0;
float3 NoiseP = Texture2DSampleGrad(NoiseTex, NoiseTexSampler, PRw / PatchM + float2(0.73, 0.19), dPRx / PatchM, dPRy / PatchM).rgb;

float DepthM = PD * 0.01;
// The small tiling scale near, the large one with view depth; the UV warp follows the tile in use.
float MixAB = saturate((DepthM - FarStartM) / max(FarRangeM, 1e-3));
float2 Pw = P + lerp(WarpNearM, WarpM, MixAB) * (NoiseB.rb - 0.5) * 2.0;
// Two-scale top projection into OUT; a scale whose weight is below 0.001 is not fetched.
#define CG_TOP(OUT, Tex, T) { float4 CgA_ = 0.0; float4 CgB_ = 0.0; \
	[branch] if (MixAB < 0.999) { CgA_ = Texture2DSampleGrad(Tex, Tex##Sampler, Pw / (T), dPx / (T), dPy / (T)); } \
	[branch] if (MixAB > 0.001) { CgB_ = Texture2DSampleGrad(Tex, Tex##Sampler, Pw * (Scale2 / (T)), dPx * (Scale2 / (T)), dPy * (Scale2 / (T))); } \
	OUT = lerp(CgA_, CgB_, MixAB); }

// Round 2 clump noise (ClumpM, rotated +63 deg, warped by the 26.6 m noise). Round 3: fetched here, before the auto-rock, inside the
// grass-or-dirt splat branch (round 2's grass pixels plus the painted paths), so it can also break the rock threshold.
// The grass albedo (height in alpha) and normal are fetched in the same branch, so they do not wait on the auto-rock and blend maths (round 3, cost:
// a grass fetch keyed on the post-rock weight made it wait on the clump fetch).
float3 NoiseC = float3(0.5, 0.5, 0.5);
float4 TcG = float4(0.0, 0.0, 0.0, GrassHMean);
float4 TnG = float4(0.5, 0.5, 1.0, 0.0);
[branch] if (W.x + W.y > 0.001)
{
	CG_TOP(TcG, GrassC, TileGrass);
	// Round 4 (cost): the grass normal is not fetched beyond GrassNFarM of view depth (faded out over GrassNFadeM before it); at rts80
	// distance its mips are nearly flat and the grain lives in the albedo and its height (risk 5 fallback 1: less detail far away).
	[branch] if (DepthM < GrassNFarM)
	{
		CG_TOP(TnG, GrassN, TileGrass);
		TnG.rg = lerp(TnG.rg, 0.5, saturate((DepthM - GrassNFarM + GrassNFadeM) / max(GrassNFadeM, 1e-3)));
	}
	float2 PC = PCr + ClumpWarpM * (NoiseB.rb - 0.5) * 2.0;
	NoiseC = Texture2DSampleGrad(NoiseTex, NoiseTexSampler, PC / ClumpM + float2(0.13, 0.51), dPCx / ClumpM, dPCy / ClumpM).rgb;
}
float ClumpR = (NoiseC.r - 0.5) * 2.0;
// Round 3: the clump amplitude mixes the 26.6 m and the 140 m noise (ClumpAmpMix), so the 26.6 m period shows less in the far rows.
float ClumpAmp = lerp(0.5 + NoiseB.b, 0.5 + NoiseP.b, ClumpAmpMix);
float Clump = ClumpR * ClumpAmp;

// Auto-rock where N.z < RockCos (round 3: the threshold is broken by the clump noise, RockBreak, so cliff triangles do not draw teeth);
// a dirt scree toe where the rock ramp is partial (round 3: a wider apron, ToeWidth, broken by the clump noise, ApronNoise).
// Round 3 (cost): skipped (coherently) on ground flatter than the ramp, where RockAuto and Toe are 0.
float Toe = 0.0;
float RockZ = Nw.z + RockBreak * ClumpR;
[branch] if (RockZ < RockCos + RockBand)
{
	float RockAuto = smoothstep(RockCos + RockBand, RockCos - RockBand, RockZ);
	Toe = ScreeStrength * saturate(1.0 - abs(RockAuto - 0.3) * ToeWidth) * (W.x + W.y) * saturate(1.0 + ApronNoise * ClumpR);
	W = lerp(W, float4(0.0, 0.0, 1.0, 0.0), RockAuto);
}
// Painted dirt (paths), before the scree toe joins the dirt weight.
float Wd = W.y;
W = lerp(W, float4(0.0, 1.0, 0.0, 0.0), Toe);
// Round 3: a grassy crown along the path centre, broken by the clump noise (grass back where the painted dirt is about 1).
[branch] if (Wd > 0.85)
{
	float Crown = CrownStrength * smoothstep(0.85, 1.0, Wd) * saturate(0.5 + CrownNoise * ClumpR) * (1.0 - Toe);
	W.x += Crown;
	W.y -= Crown;
}

float Macro = NoiseP.g;
//#INCLUDE ChimeraPatch.hlsl

// Rock side projections (faces looking along X use (Y, Z), along Y use (X, Z)). Round 1: pow 8 (sharper, fewer side fetches).
// Round 3: the side UVs are rotated (RockRotC/RockRotS = cos/sin of about 35 deg), warped by the 26.6 m noise (RockWarpM) and use
// their own tile (TileRockSide), so the top tile's motif does not repeat as a grid on the plateau walls.
// Round 3 (cost): the projection weights and side UVs are only computed where there is rock weight; their gradients are the rotated
// gradients of P and PZ (taken outside the branch; the warp's own gradient is left out), so the branch needs no ddx.
float2 dPZ = float2(ddx(PZ), ddy(PZ));
float3 TW = float3(0.0, 0.0, 1.0);
float2 SX = 0.0, SY = 0.0, dSXx = 0.0, dSXy = 0.0, dSYx = 0.0, dSYy = 0.0;
[branch] if (W.z > 0.001)
{
	TW = abs(Nw);
	TW *= TW;
	TW *= TW;
	TW *= TW;
	TW /= max(dot(TW, float3(1.0, 1.0, 1.0)), 1e-4);
	float2 RkWarp = RockWarpM * (NoiseB.rb - 0.5) * 2.0;
	SX = float2(RockRotC * P.y - RockRotS * PZ, RockRotS * P.y + RockRotC * PZ) + RkWarp;
	SY = float2(RockRotC * P.x + RockRotS * PZ, -RockRotS * P.x + RockRotC * PZ) + RkWarp;
	dSXx = float2(RockRotC * dPx.y - RockRotS * dPZ.x, RockRotS * dPx.y + RockRotC * dPZ.x);
	dSXy = float2(RockRotC * dPy.y - RockRotS * dPZ.y, RockRotS * dPy.y + RockRotC * dPZ.y);
	dSYx = float2(RockRotC * dPx.x + RockRotS * dPZ.x, -RockRotS * dPx.x + RockRotC * dPZ.x);
	dSYy = float2(RockRotC * dPy.x + RockRotS * dPZ.y, -RockRotS * dPy.x + RockRotC * dPZ.y);
}

#define CG_SIDE(Tex, T, UV, DX, DY) Texture2DSampleGrad(Tex, Tex##Sampler, (UV) / (T), (DX) / (T), (DY) / (T))
#define CG_ROCK(OUT, Tex) { float4 CgT_; CG_TOP(CgT_, Tex, TileRock); OUT = TW.z * CgT_; \
	[branch] if (TW.x > 0.01) { OUT += TW.x * CG_SIDE(Tex, TileRockSide, SX, dSXx, dSXy); } \
	[branch] if (TW.y > 0.01) { OUT += TW.y * CG_SIDE(Tex, TileRockSide, SY, dSYx, dSYy); } }

// Round 1: the grass albedo fetch carries the grass height in alpha, so grass needs no ARH fetch at all: pure and mixed grass use one
// grain term (round 0's pure grass used constants, which switched the grain off on ~99 % of the meadow and lit a fringe at mixed pixels).
// Grass AO follows its height (Grass004: corr 0.78, slope GrassAOSlope); its roughness map sits below RoughMin everywhere.
float4 ArhG = float4(saturate(GrassAOMean + GrassAOSlope * (TcG.a - GrassHMean)), RoughMin, TcG.a, 0.0);
float4 ArhD = float4(1.0, 1.0, DirtHMean, 0.0);
float4 ArhR = float4(1.0, 1.0, RockHMean, 0.0);
float4 ArhS = float4(1.0, 1.0, SnowHMean, 0.0);
[branch] if (W.y > 0.001) { CG_TOP(ArhD, DirtARH, TileDirt); }
[branch] if (W.z > 0.001) { CG_ROCK(ArhR, RockARH); }
[branch] if (W.w > 0.001) { CG_TOP(ArhS, SnowARH, TileSnow); }

// Snow settles into rock hollows: the snow height gains the inverted rock height.
float4 H = float4(ArhG.b, ArhD.b, ArhR.b, ArhS.b + SnowFill * (1.0 - ArhR.b));
// Round 2: the clump noise breaks the grass height at path and rock edges (blend only; the grain below keeps the texture height).
// Round 3: the clump noise also breaks the snow edge (SnowEdge).
float4 WH = W + (H + float4(PathEdge * ClumpR, 0.0, 0.0, SnowEdge * ClumpR)) * BlendDepth;
float MaxWH = max(max(WH.x, WH.y), max(WH.z, WH.w));
float4 B = max(0.0, WH - MaxWH + BlendDepth) * step(0.001, W);
B /= max(dot(B, float4(1.0, 1.0, 1.0, 1.0)), 1e-5);

// Per-layer saturation (toward luma) and value.
#define CG_LUMA(C) dot((C), float3(0.2126, 0.7152, 0.0722))
#define CG_SV(C, S, V) (lerp(CG_LUMA(C).xxx, (C), (S)) * (V))

// Albedo and normals of the layers that survived the blend.
float3 Col = float3(0.0, 0.0, 0.0);
float2 Nxy = float2(0.0, 0.0);
float4 Tc;
float4 Tn;
[branch] if (B.x > 0.001)
{
	// Round 3: a grass tint (GrassR/G/B) toward green under the warm sun.
	float3 G = CG_SV(TcG.rgb, SatGrass, ValGrass) * float3(GrassR, GrassG, GrassB);
	// Grain-keyed hue split: blade tips (high) toward yellow, the low grain toward green.
	// Round 2: the split follows the clump term, so yellow tips cluster instead of sprinkling evenly.
	G *= 1.0 + HueSplit * saturate(0.5 + ClumpTip * Clump) * (TcG.a - GrassHMean) * float3(1.0, 0.0, -1.5);
	// Worn margin beside paths: the painted dirt weight ramps in over the brush falloff (painted dirt only, not the scree toe).
	float Halo = HaloStrength * saturate(Wd * HaloGain);
	// Round 2: clump-scale value, chroma and hue (yellow-green <-> blue-green), each carrying the clump amplitude.
	// Round 3: the value swing is stronger in lush fields (LushClump) and flattened on the worn margin. (A 12-15 m cluster term from the
	// 26.6 m noise was tried and dropped for cost; it also fed the 26.6 m period back into the far rows.)
	float CV = ClumpVal * (1.0 + LushClump * saturate(-Patch)) * (1.0 - Halo);
	G = CG_SV(G, 1.0 + ClumpSat * (NoiseC.g - 0.5) * 2.0 * ClumpAmp, 1.0 + CV * Clump);
	G *= 1.0 + ClumpHue * (NoiseC.b - 0.5) * 2.0 * (1.5 - NoiseB.b) * float3(1.0, 0.0, -1.0);
	// Round 4: round 3's tussocks (a 0.80..0.95 threshold of the 5 m clump noise) are removed (little visible effect; ALU).
	// Meadow patches: Patch > 0 toward dry/fallow grass, Patch < 0 toward deep lush grass (tint, saturation and value each).
	// Round 3 (cost): one saturation/value pass with the side's parameters selected, instead of computing both and picking one.
	const bool PDry = Patch > 0.0;
	float3 PTint = PDry ? float3(DryR, DryG, DryB) : float3(LushR, LushG, LushB);
	[branch] if (abs(Patch) > 0.001) { G = lerp(G, CG_SV(G, PDry ? DrySat : LushSat, PDry ? DryVal : LushVal) * PTint, abs(Patch) * PatchStrength); }
	// Round 3: the worn margin is paler, less saturated grass (HaloSat, HaloVal), no longer tinted toward dry.
	[branch] if (Halo > 0.001) { G = lerp(G, CG_SV(G, HaloSat, HaloVal), Halo); }
	// Round 2: contact darkening where grass meets rock.
	G *= 1.0 - ContactDark * saturate(W.z * ContactGain);
	Col += B.x * G;
	Nxy += B.x * (TnG.rg * 2.0 - 1.0);
}
[branch] if (B.y > 0.001)
{
	CG_TOP(Tc, DirtC, TileDirt);
	CG_TOP(Tn, DirtN, TileDirt);
	float3 D = CG_SV(Tc.rgb, SatDirt, ValDirt) * float3(DirtR, DirtG, DirtB);
	float Dm = CG_LUMA(D);
	D = max(D + (DirtContrast - 1.0) * (Dm - DirtLumaMean) * (D / max(Dm, 1e-4)), 0.0);
	// Round 2: a darker scree toe; a lighter, compacted path centre.
	D *= (1.0 - ToeDark * saturate(Toe * 2.0)) * (1.0 + DirtCompact * smoothstep(0.85, 1.0, W.y));
	// Round 3: twin wheel ruts: two darker bands where the painted weight crosses RutLo..RutHi on either side of the stroke centre.
	D *= 1.0 - RutDark * smoothstep(RutLo, RutLo + 0.2, Wd) * (1.0 - smoothstep(RutHi, RutHi + 0.1, Wd));
	Col += B.y * D;
	Nxy += B.y * DirtNormal * (Tn.rg * 2.0 - 1.0);
}
[branch] if (B.z > 0.001)
{
	CG_ROCK(Tc, RockC);
	CG_ROCK(Tn, RockN);
	// Round 4: the albedo luma is pulled toward the texture mean (RockFlat), so the white veins of rocks_ground_05 do not read as marble.
	Tc.rgb *= lerp(1.0, RockLumaMean / max(CG_LUMA(Tc.rgb), 1e-4), RockFlat);
	float3 Rk = CG_SV(Tc.rgb, SatRock, ValRock);
	// Moss and dirt on ledges and in the low parts of upward faces.
	// Round 2: macro value from both noise fetches, cavity darkening from the rock's own height.
	Rk *= (1.0 + RockMacro * (NoiseB.g + NoiseP.b - 1.0)) * saturate(1.0 + RockCavity * (ArhR.b - RockHMean));
	float Moss = MossStrength * saturate((Nw.z - 0.45) * 3.0) * saturate(1.2 - ArhR.b) * saturate(0.5 + MossNoise * (NoiseP.r - 0.5) * 2.0);
	Rk = lerp(Rk, CG_LUMA(Rk) * float3(MossR, MossG, MossB), Moss);
	// Round 3: faint grey-green lichen on the side faces (broken by the clump noise) and lighter upward faces.
	float Lichen = LichenStrength * (1.0 - TW.z) * saturate(0.5 + 3.0 * (NoiseC.b - 0.5));
	Rk = lerp(Rk, CG_LUMA(Rk) * float3(LichenR, 1.0, LichenB), Lichen) * (1.0 + RockTop * saturate(Nw.z));
	Rk *= float3(RockR, RockG, RockB);
	Col += B.z * Rk;
	Nxy += B.z * lerp(RockSideN, 1.0, TW.z) * (Tn.rg * 2.0 - 1.0);
}
[branch] if (B.w > 0.001)
{
	CG_TOP(Tc, SnowC, TileSnow);
	CG_TOP(Tn, SnowN, TileSnow);
	Col += B.w * CG_SV(Tc.rgb, 1.0, ValSnow) * float3(SnowR, SnowG, SnowB);
	Nxy += B.w * (Tn.rg * 2.0 - 1.0);
}

#undef CG_SV
#undef CG_LUMA
#undef CG_ROCK
#undef CG_SIDE
#undef CG_TOP

// Grain contrast from the layers' own AO and height, centred per layer (the sun ignores material AO, so part of it goes into albedo).
float AOb = saturate(dot(B, float4(ArhG.r, ArhD.r, ArhR.r, ArhS.r)));
float Hc = dot(B, H - float4(GrassHMean, DirtHMean, RockHMean, SnowHMean));
// Round 3: snow takes less grain (SnowGrain), so it does not read as streaked plaster.
float GrainK = 1.0 - SnowGrain * B.w;
Col *= lerp(1.0, AOb, GrainAO * GrainK) * saturate(1.0 + GrainH * GrainK * Hc);
// Round 3 (cost): the mid-scale brightness term (MidStrength, 0 since G1 round 0: its 26.6 m tile showed) is removed.
Col *= lerp(1.0 - MacroStrength, 1.0 + MacroStrength, Macro);
Nxy *= NormalStrength;
// Round 3's micro-relief normal (a tilt from the 26.6 m and 5 m noise values) is removed in round 4 (ALU): switching it off did not
// remove the closeup whorls (those came from the clump warp, ClumpWarpM) and did not visibly change rts80.
CgNormal = float3(Nxy, sqrt(saturate(1.0 - dot(Nxy, Nxy))));
CgRough = max(saturate(dot(B, float4(ArhG.g, ArhD.g, ArhR.g, ArhS.g))), RoughMin);
CgAO = lerp(1.0, AOb, AOStrength);

// Brush ring: XY distance to the brush centre, at least 1.5 px wide.
// Round 3 (cost): only pixels within BrushRadius + 2 m of the centre evaluate it; the pixel footprint bounds fwidth(Dist) (|grad Dist| = 1)
// so the branch needs no derivative.
CgEmissive = float3(0.0, 0.0, 0.0);
float2 BrD = P - float2(BrushX, BrushY);
float BrOut = BrushRadius + 2.0;
[branch] if (BrushRadius > 0.0 && dot(BrD, BrD) < BrOut * BrOut)
{
	float Dist = length(BrD);
	float RingW = max(RingWidthM, 1.5 * (max(abs(dPx.x), abs(dPx.y)) + max(abs(dPy.x), abs(dPy.y))));
	CgEmissive = float3(1.0, 0.72, 0.25) * (RingIntensity * saturate(1.0 - abs(Dist - BrushRadius) / RingW));
}

return Col;

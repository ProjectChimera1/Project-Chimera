// Project Chimera scatter materials (plan C scatter 3.6-3.7, task S3). Original Chimera code.
// Bodies of the Custom nodes of the scatter materials; Scripts/make_scatter_assets.py reads this file and puts one section (plus
// COMMON in front of it) into UMaterialExpressionCustom::Code (MaterialExpressionCustom.h). Each section starts with
//   //#SECTION <name>      //#INPUTS <input names>      //#OUTPUTS <name:float1|float3 ...>
// and Scripts/scatter/scatter_material_spec.py (pure Python, shared with the tests) says what feeds every input.
//
// Translator rules (the same as ChimeraGround.hlsl, HLSLMaterialTranslator.cpp CustomExpression): a texture-object input X arrives as
// `Texture2D X, SamplerState XSampler`; an LWC vector input (WP, OP) arrives demoted to float3; every additional output is an `inout`
// parameter; the main output (float3) is the base colour.
//
// Rules for every scatter material (plan 3.7, F24, F25):
//  * No per-instance random anywhere (the engine's value changes with remove-at-swap history, F25): all variation comes from the
//    record's custom data CD0..CD3 (fine grid: dryness, tint or flower colour index, terrain normal x and y; coarse grid: variation,
//    stand), which BuildInstance writes (TerrainScatter.cpp). A coarse-grid instance has only two custom floats, so CD2 and CD3 read
//    their default 0 there: a flat terrain normal, harmless.
//  * No derivative functions at all. Nanite shading and the Nanite raster do not give a Custom node derivatives (F24), so every fetch
//    here is an explicit SampleLevel with a mip taken from the pixel depth; the opacity mask of the masked materials is a separate
//    standard TextureSample expression, never HLSL.
//  * No world-position offset, no time term: grass keeps its VSM pages cached (plan 3.7).
//  * The grass colour follows the ground: the meadow patch field below is the ground's (ChimeraGround.hlsl round 4: the two noise fetches,
//    the long octave, the sun-facing-slope term Tie, PatchContrast, the bimodal soft edge, the dry/lush saturation-value-tint step) at the
//    instance origin (plan 3.7), with the ground's own scalars copied from make_ground_material.py SCALARS at build time. OP is
//    TransformPosition(Instance -> Absolute World) of (0,0,0), i.e. GetInstanceToWorld(Parameters) in the pixel shader (ISM and Nanite
//    alike). It is NOT ObjectPositionWS: in a pixel shader that is the primitive's (ISM component's) bounds origin (MaterialTemplate.ush
//    MakeMaterialLWCData(FMaterialPixelParameters)), which would give one patch value per (tile, mesh) component. Not copied until S6 moves the patch block into
//    a shared include: the clump dither (PatchDither x ClumpR, a third fetch) and the rock/dirt mask on Tie (~1 where grass grows).
//  * Normals leave as world-space vectors times the facing sign (CgNormalWS) and the build wires a World -> Tangent Transform node
//    after them, so a back face of a two-sided mesh keeps the same lighting as its front (the engine multiplies the tangent normal by
//    TwoSidedSign again, MaterialTemplate.ush "flip the normal for backfaces").

//#SECTION Common
#define CS_LUMA(C) dot((C), float3(0.2126, 0.7152, 0.0722))
#define CS_SV(C, S, V) (lerp(CS_LUMA(C).xxx, (C), (S)) * (V))
// The angle one pixel spans: 2 tan(vfov/2) / view height, from the view (ViewToClip[1][1] = 1 / tan(vfov/2); MaterialTemplate.ush reads
// ResolvedView.ViewToClip and ResolvedView.ViewSizeAndInvSize the same way). A positive PixelAngle parameter overrides it (diagnostic).
#define CS_PIXEL_ANGLE(Override) ((Override) > 0.0 ? (Override) : 2.0 / max(ResolvedView.ViewToClip[1][1] * ResolvedView.ViewSizeAndInvSize.y, 1e-3))

//#SECTION Blade
//#INPUTS OP PD VN VC CD0 CD1 CD2 CD3 NoiseTex Root Tip LushMul DryMul HeadYellow HeadWhite HeadViolet FlowerMode TerrainNormalMix Rough AOStrength VarAmt PatchAmount PixelAngle WarpNoiseM PatchM PatchWarpM PatchStrength PatchContrast PatchBias PatchLo PatchHi PatchLong PatchLongM DryR DryG DryB DryVal DrySat LushR LushG LushB LushVal LushSat SunDirX SunDirY SunDirZ TerrainDry TieLo TieHi
//#OUTPUTS CgNormalWS:float3 CgRough:float1 CgAO:float1
// Blades, tussocks, ferns and flowers. Vertex colour: R = ambient occlusion, G = per-part variation, B = part mask (flowers: 1 = head only),
// A = height fraction (root 0, tip 1). The VertexColor expression's first output is RGB only (the five outputs of the node are all named
// "", so the build can wire only the first), so the alpha comes from Parameters.VertexColor, whose interpolant the expression switches on.
// Base colour = lerp(Root, Tip, height fraction) x lerp(LushMul, DryMul, dryness) x a small tint variation, then the ground's meadow patch
// step at the instance origin. Shading normal = lerp(geometry normal, terrain normal from CD2/CD3, TerrainNormalMix), so the grass lights
// like the ground under it and does not glitter. The noise mips follow the pixel footprint (PD x the view's pixel angle against the texel size of each
// fetch, 1024 px textures), since a Custom node has no derivatives (F24): no mip-0 aliasing at rts80 distances.
float2 P = OP.xy * 0.01;
float2 PB = float2(0.7547 * P.x + 0.6561 * P.y, -0.6561 * P.x + 0.7547 * P.y);
float2 PR = float2(0.8910 * P.x - 0.4540 * P.y, 0.4540 * P.x + 0.8910 * P.y);
float Foot = PD * 0.01 * CS_PIXEL_ANGLE(PixelAngle) * 1024.0;
float MipB = max(0.0, log2(max(Foot / WarpNoiseM, 1e-4)));
float MipP = max(0.0, log2(max(Foot / PatchM, 1e-4)));
float3 NoiseB = Texture2DSampleLevel(NoiseTex, NoiseTexSampler, PB / WarpNoiseM + float2(0.31, 0.67), MipB).rgb;
float2 PRw = PR + PatchWarpM * (NoiseB.rb - 0.5) * 2.0;
float3 NoiseP = Texture2DSampleLevel(NoiseTex, NoiseTexSampler, PRw / PatchM + float2(0.73, 0.19), MipP).rgb;
float Patch = (0.62 * NoiseP.r + 0.30 * NoiseP.b + 0.08 * NoiseB.g - 0.5) * 2.0;
float2 PL = P / max(PatchLongM, 1.0);
float Long = (sin(dot(PL, float2(0.8660, 0.5000)) * 6.2832 + 1.3) + sin(dot(PL, float2(-0.3420, 0.9397)) * 5.0265 + 4.1)
	+ sin(dot(PL, float2(0.6428, -0.7660)) * 4.0841 + 2.2)) * (1.0 / 3.0);
// The terrain normal (fine grid CD2/CD3; a coarse-grid instance reads 0, i.e. flat) also drives the ground's sun-facing-slope dryness Tie.
float2 Nt = float2(CD2, CD3);
float3 Ng = float3(Nt, sqrt(saturate(1.0 - dot(Nt, Nt))));
float3 SunD = float3(SunDirX, SunDirY, SunDirZ);
float Tie = (dot(Ng, SunD) - SunD.z * Ng.z) * TerrainDry * smoothstep(TieLo, TieHi, 1.0 - Ng.z);
Patch = clamp((Patch + PatchLong * Long - PatchBias) * PatchContrast + Tie, -1.0, 1.0);
Patch = sign(Patch) * smoothstep(PatchLo, PatchHi, abs(Patch));

float4 Vc = float4(VC.rgb, Parameters.VertexColor.a);
const bool IsFlower = FlowerMode > 0.5;
float HeadMask = IsFlower ? step(0.5, Vc.b) : 0.0;
float3 Col = lerp(Root.rgb, Tip.rgb, saturate(Vc.a));
float Var = IsFlower ? 1.0 : 1.0 + VarAmt * (saturate(CD1) * 2.0 - 1.0);
Col *= lerp(LushMul.rgb, DryMul.rgb, saturate(CD0)) * Var;
const bool PDry = Patch > 0.0;
float3 PTint = PDry ? float3(DryR, DryG, DryB) : float3(LushR, LushG, LushB);
Col = lerp(Col, CS_SV(Col, PDry ? DrySat : LushSat, PDry ? DryVal : LushVal) * PTint, abs(Patch) * PatchStrength * PatchAmount * (1.0 - HeadMask));
// Flower colour index in CD1: 0 yellow, 1 white, 2 violet (the generator's colour classes).
float CI = CD1 + 0.5;
float3 HeadCol = CI < 1.0 ? HeadYellow.rgb : (CI < 2.0 ? HeadWhite.rgb : HeadViolet.rgb);
Col = lerp(Col, HeadCol, HeadMask);

float3 Nmix = normalize(lerp(normalize(VN), Ng, TerrainNormalMix));
CgNormalWS = Nmix * Parameters.TwoSidedSign;
CgRough = Rough;
CgAO = lerp(1.0, saturate(Vc.r), AOStrength);
return Col;

//#SECTION Triplanar
//#INPUTS WP VN PD VC CD0 CD1 TexC TexN Tint TexLumaMean TexAmt TileM MipShift PixelAngle NormalStrength Rough AOStrength VarAmt StandAmt PartVar HeightGain
//#OUTPUTS CgNormalWS:float3 CgRough:float1 CgAO:float1
// L0 canopies, needles, shrub leaf masses and rocks: a world-aligned triplanar texture (the mesh UVs of a displaced blob stretch),
// its luma normalised by TexLumaMean so Tint stays the species colour, a detail normal and the vertex colour AO. The mip comes from
// the pixel depth and the view's pixel angle (2 tan(vfov/2) / height: 0.00142 at rts80's 75 degrees, 0.00086 at oblique's 50, at 1080p;
// the textures are 1024 px), because a Custom node has no derivatives (F24). The projection is world-aligned, so the texture slides over a
// rock or tree whose instance moves with a sculpt (a known look item for S6; an instance-relative projection would repeat one pattern on
// every instance of a mesh seen from above).
float4 Vc = float4(VC.rgb, Parameters.VertexColor.a);
float3 Pm = WP.xyz * 0.01;
float3 Nw = normalize(VN);
float3 Wt = abs(Nw);
Wt *= Wt;
Wt *= Wt;
Wt /= max(dot(Wt, float3(1.0, 1.0, 1.0)), 1e-4);
float Mip = max(0.0, log2(max(PD * 0.01 * CS_PIXEL_ANGLE(PixelAngle) * 1024.0 / TileM, 1e-4))) + MipShift;
float2 UvX = Pm.yz / TileM;
float2 UvY = Pm.xz / TileM;
float2 UvZ = Pm.xy / TileM;
float3 TexRgb = Texture2DSampleLevel(TexC, TexCSampler, UvX, Mip).rgb * Wt.x
	+ Texture2DSampleLevel(TexC, TexCSampler, UvY, Mip).rgb * Wt.y
	+ Texture2DSampleLevel(TexC, TexCSampler, UvZ, Mip).rgb * Wt.z;
float3 Col = Tint.rgb * lerp(1.0, CS_LUMA(TexRgb) / max(TexLumaMean, 1e-3), TexAmt);
// Per-instance variation (CD0), per-stand variation (CD1: broadleaf vs conifer stands) and per-part variation (vertex colour G, one value per lobe, frond or tier), a lighter top (A).
Col *= (1.0 + VarAmt * (saturate(CD0) * 2.0 - 1.0)) * (1.0 + StandAmt * (saturate(CD1) * 2.0 - 1.0))
	* (1.0 + PartVar * (saturate(Vc.g) * 2.0 - 1.0))
	* lerp(1.0 - HeightGain, 1.0 + HeightGain, saturate(Vc.a));
float2 DnX = Texture2DSampleLevel(TexN, TexNSampler, UvX, Mip).rg * 2.0 - 1.0;
float2 DnY = Texture2DSampleLevel(TexN, TexNSampler, UvY, Mip).rg * 2.0 - 1.0;
float2 DnZ = Texture2DSampleLevel(TexN, TexNSampler, UvZ, Mip).rg * 2.0 - 1.0;
float3 Dw = Wt.z * float3(DnZ.x, DnZ.y, 0.0) + Wt.x * float3(0.0, DnX.x, DnX.y) + Wt.y * float3(DnY.x, 0.0, DnY.y);
CgNormalWS = normalize(Nw + NormalStrength * Dw) * Parameters.TwoSidedSign;
CgRough = Rough;
CgAO = lerp(1.0, saturate(Vc.r), AOStrength);
return Col;

//#SECTION Tex
//#INPUTS BaseRGB ARM CD0 Tint VarAmt RoughMul AOFromR
//#OUTPUTS CgRough:float1 CgAO:float1
// L1 (scanned) and L0 bark meshes: the albedo and the packed AO / roughness / metal textures come from standard TextureSample expressions
// (UV0, with the engine's own derivatives), the opacity mask and the normal map too. Here: the tint, the per-instance variation (CD0)
// and the AO / roughness split of the packed map (AOFromR = 0 when the packed map is a plain roughness map, so R is not AO). Metal stays 0.
float3 Col = BaseRGB.rgb * Tint.rgb * (1.0 + VarAmt * (saturate(CD0) * 2.0 - 1.0));
CgRough = saturate(ARM.g * RoughMul);
CgAO = lerp(1.0, saturate(ARM.r), AOFromR);
return Col;

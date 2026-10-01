// Project Chimera terrain trial (plan C 3.5). Original Chimera code.
// Body of the single Custom node of /Game/Terrain/M_ChimeraGround; make_ground_material.py reads this file into
// UMaterialExpressionCustom::Code (MaterialExpressionCustom.h). The translator wraps it in a function whose parameters are the
// inputs below (HLSLMaterialTranslator.cpp CustomExpression): a texture object input X arrives as `Texture2D X, SamplerState XSampler`
// (each with its asset's own sampler state), an LWC vector input (WP) arrives demoted to float3, and every additional output is an
// `inout` parameter. The main output (CMOT_Float3) is the base colour.
//
// Inputs:  WP (absolute world position, cm), VN (VertexNormalWS), SplatTex (RGBA8 weights R Grass, G Dirt, B Rock, A Snow; linear, clamp),
//          NoiseTex (macro variation), <Layer>C / <Layer>N / <Layer>ARH for Grass, Dirt, Rock, Snow (albedo sRGB; BC5 normal; AO, rough, height),
//          scalars HalfExtentM, TileGrass, TileDirt, TileRock, TileSnow, Scale2, BlendDepth, RockCos, RockBand, MacroM, MacroStrength,
//          NormalStrength, AOStrength, BrushX, BrushY, BrushRadius, RingWidthM, RingIntensity.
// Outputs: return = BaseColor, CgNormal (tangent space), CgRough, CgAO, CgEmissive (brush ring).
//
// Maths (plan C 3.5):
//  * splat UV from absolute world position: u = (X/100 + E) / (2E), v likewise (E = HalfExtentM), sampled at mip 0 (the splat has one).
//  * world-aligned top-projection UVs at two scales per layer (P / Tile and P * Scale2 / Tile), mixed by a macro noise.
//  * auto-rock where N.z < RockCos (cos 35 deg), ramped over +-RockBand; rock is projected on three axes so steep faces do not stretch.
//  * height blend  w'_i = max(0, w_i + h_i*depth - max_j(w_j + h_j*depth) + depth), normalised.
//  * only layers with weight are sampled: every fetch is SampleGrad with gradients taken outside the branches, so the branches are legal
//    and a pure-grass pixel pays for grass only (risk 5: the material's GPU cost).
//  * brush ring from BrushX/BrushY/BrushRadius (metres, XY distance, so it follows the surface); BrushRadius = 0 draws nothing.

float2 P = WP.xy * 0.01;
float PZ = WP.z * 0.01;
float2 dPx = ddx(P);
float2 dPy = ddy(P);

// Splat weights; auto-rock on steep faces.
float2 SplatUV = (P + HalfExtentM) / (2.0 * HalfExtentM);
float4 W = max(Texture2DSampleLevel(SplatTex, SplatTexSampler, SplatUV, 0.0), 0.0);
W /= max(dot(W, float4(1.0, 1.0, 1.0, 1.0)), 1e-4);
float3 Nw = normalize(VN);
float RockAuto = smoothstep(RockCos + RockBand, RockCos - RockBand, Nw.z);
W = lerp(W, float4(0.0, 0.0, 1.0, 0.0), RockAuto);

// Macro noise: a brightness variation and the mix between the two tiling scales.
float2 M1 = P / MacroM;
float Macro = Texture2DSampleGrad(NoiseTex, NoiseTexSampler, M1, dPx / MacroM, dPy / MacroM).r;
float MacroB = MacroM * 0.37;
float MixAB = lerp(0.2, 0.8, Texture2DSampleGrad(NoiseTex, NoiseTexSampler, P / MacroB + float2(0.31, 0.67), dPx / MacroB, dPy / MacroB).g);

// Rock side projections (faces looking along X use (Y, Z), along Y use (X, Z)).
float3 TW = pow(abs(Nw), 4.0);
TW /= max(dot(TW, float3(1.0, 1.0, 1.0)), 1e-4);
float2 SX = float2(P.y, PZ);
float2 SY = float2(P.x, PZ);
float2 dSXx = ddx(SX);
float2 dSXy = ddy(SX);
float2 dSYx = ddx(SY);
float2 dSYy = ddy(SY);

#define CG_TOP(Tex, T) lerp(Texture2DSampleGrad(Tex, Tex##Sampler, P / (T), dPx / (T), dPy / (T)), Texture2DSampleGrad(Tex, Tex##Sampler, P * (Scale2 / (T)), dPx * (Scale2 / (T)), dPy * (Scale2 / (T))), MixAB)
#define CG_SIDE(Tex, T, UV, DX, DY) Texture2DSampleGrad(Tex, Tex##Sampler, (UV) / (T), (DX) / (T), (DY) / (T))
#define CG_ROCK(Tex) (TW.z * CG_TOP(Tex, TileRock) + (TW.x > 0.01 ? TW.x * CG_SIDE(Tex, TileRock, SX, dSXx, dSXy) : 0.0) + (TW.y > 0.01 ? TW.y * CG_SIDE(Tex, TileRock, SY, dSYx, dSYy) : 0.0))

// Heights (ARH.b) of the layers present, for the height blend.
float3 ArhG = float3(1.0, 1.0, 0.0);
float3 ArhD = float3(1.0, 1.0, 0.0);
float3 ArhR = float3(1.0, 1.0, 0.0);
float3 ArhS = float3(1.0, 1.0, 0.0);
[branch] if (W.x > 0.001) { ArhG = CG_TOP(GrassARH, TileGrass).rgb; }
[branch] if (W.y > 0.001) { ArhD = CG_TOP(DirtARH, TileDirt).rgb; }
[branch] if (W.z > 0.001) { ArhR = CG_ROCK(RockARH).rgb; }
[branch] if (W.w > 0.001) { ArhS = CG_TOP(SnowARH, TileSnow).rgb; }

float4 H = float4(ArhG.b, ArhD.b, ArhR.b, ArhS.b);
float4 WH = W + H * BlendDepth;
float MaxWH = max(max(WH.x, WH.y), max(WH.z, WH.w));
float4 B = max(0.0, WH - MaxWH + BlendDepth) * step(0.001, W);
B /= max(dot(B, float4(1.0, 1.0, 1.0, 1.0)), 1e-5);

// Albedo and normals of the layers that survived the blend.
float3 Col = float3(0.0, 0.0, 0.0);
float2 Nxy = float2(0.0, 0.0);
[branch] if (B.x > 0.001) { Col += B.x * CG_TOP(GrassC, TileGrass).rgb; Nxy += B.x * (CG_TOP(GrassN, TileGrass).rg * 2.0 - 1.0); }
[branch] if (B.y > 0.001) { Col += B.y * CG_TOP(DirtC, TileDirt).rgb; Nxy += B.y * (CG_TOP(DirtN, TileDirt).rg * 2.0 - 1.0); }
[branch] if (B.z > 0.001) { Col += B.z * CG_ROCK(RockC).rgb; Nxy += B.z * (CG_ROCK(RockN).rg * 2.0 - 1.0); }
[branch] if (B.w > 0.001) { Col += B.w * CG_TOP(SnowC, TileSnow).rgb; Nxy += B.w * (CG_TOP(SnowN, TileSnow).rg * 2.0 - 1.0); }

#undef CG_ROCK
#undef CG_SIDE
#undef CG_TOP

Col *= lerp(1.0 - MacroStrength, 1.0 + MacroStrength, Macro);
Nxy *= NormalStrength;
CgNormal = float3(Nxy, sqrt(saturate(1.0 - dot(Nxy, Nxy))));
CgRough = saturate(dot(B, float4(ArhG.g, ArhD.g, ArhR.g, ArhS.g)));
CgAO = lerp(1.0, saturate(dot(B, float4(ArhG.r, ArhD.r, ArhR.r, ArhS.r))), AOStrength);

// Brush ring: XY distance to the brush centre, at least 1.5 px wide.
float Dist = length(P - float2(BrushX, BrushY));
float RingW = max(RingWidthM, 1.5 * fwidth(Dist));
float Ring = BrushRadius > 0.0 ? saturate(1.0 - abs(Dist - BrushRadius) / RingW) : 0.0;
CgEmissive = float3(1.0, 0.72, 0.25) * (RingIntensity * Ring);

return Col;

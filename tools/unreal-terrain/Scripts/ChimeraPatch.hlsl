//! Project Chimera terrain trial (plan C scatter 3.7, task S6). Original Chimera code.
//! The meadow patch field shared by M_ChimeraGround (Scripts/ChimeraGround.hlsl) and the grass blades (Scripts/scatter/ChimeraScatter.hlsl,
//! section Blade). Both Custom-node strings take this block verbatim through `//#INCLUDE ChimeraPatch.hlsl` (Scripts/hlsl_include.py drops
//! these `//!` lines), so the grass colour follows the ground with the same statements and the same scalars.
//! It expects, already defined by the includer: float2 P (map metres), float3 NoiseP and NoiseB (the 140 m patch fetch and the 26.6 m warp
//! fetch), float3 Nw (unit surface normal), float4 W (splat weights; only W.y + W.z, dirt and rock, are read), float ClumpR (the 5 m clump
//! noise in [-1, 1]), and the scalars PatchLongM, SunDirX/Y/Z, TieLo, TieHi, TerrainDry, PatchLong, PatchBias, PatchDither, PatchContrast,
//! PatchLo, PatchHi. It defines Patch (in [-1, 1]: > 0 dry, < 0 lush), PL, Long, SunD and Tie.
//! The ground's expanded string must stay byte-identical to its pre-include string (test_hlsl_include.py pins the sha256): edit with care.
// Patch field in [-1, 1] from three channels at two unrelated periods, plus the terrain: drier on sun-facing slopes, lusher on shaded ones.
// Round 3: the 26.6 m share 0.20 -> 0.08 (the bimodal smoothstep amplified it into a 26.6 m repeat in the far rows of rts80).
float Patch = (0.62 * NoiseP.r + 0.30 * NoiseP.b + 0.08 * NoiseB.g - 0.5) * 2.0;
// Round 2: the long octave (ALU only): three sines at unrelated directions and periods (PatchLongM x 1, 1.25, 1.54) in [-1, 1].
float2 PL = P / max(PatchLongM, 1.0);
float Long = (sin(dot(PL, float2(0.8660, 0.5000)) * 6.2832 + 1.3) + sin(dot(PL, float2(-0.3420, 0.9397)) * 5.0265 + 4.1)
	+ sin(dot(PL, float2(0.6428, -0.7660)) * 4.0841 + 2.2)) * (1.0 / 3.0);
// SunDirX/Y/Z are a unit vector (make_ground_material.py), so no per-pixel normalize (round 3, cost).
float3 SunD = float3(SunDirX, SunDirY, SunDirZ);
// Sun-facing slopes dry out, but not on rock, scree or path (round 1 lit a straw ring at the cone toes).
// Round 3: only real slopes (TieLo..TieHi on 1 - N.z), so a gentle mound does not read as a lit spot.
// Round 3 (cost): skipped (coherently) on flat ground.
float Tie = 0.0;
[branch] if (1.0 - Nw.z > TieLo)
{
	Tie = (dot(Nw, SunD) - SunD.z * Nw.z) * TerrainDry * saturate(1.0 - 4.0 * (W.y + W.z)) * smoothstep(TieLo, TieHi, 1.0 - Nw.z);
}
// Round 4: the clump noise dithers the patch field (PatchDither), so dry and lush intermix at tuft scale at their boundary.
Patch = clamp((Patch + PatchLong * Long - PatchBias + PatchDither * ClumpR) * PatchContrast + Tie, -1.0, 1.0);
// Round 3: bimodal fields: soft-edged plateaus of dry and lush with a neutral band between (PatchLo..PatchHi), not blobs.
// Round 4: a wider PatchLo..PatchHi (soft field edges; round 3's 0.10..0.55 read as crisp camouflage).
Patch = sign(Patch) * smoothstep(PatchLo, PatchHi, abs(Patch));

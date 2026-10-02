// Project Chimera terrain trial (plan C scatter 3.1, 3.3, task S2). Original Chimera code.
// Integer math of the scatter generator: the hash (lowbias32), the candidate key, floor division, Q16 ramps, value noise on a world lattice,
// the integer twin of FTerrainHeightfield::SampleSurface, the cell-triangle gradient, the bilinear splat read in Q8 and FNV-1a 64.
// Everything that decides acceptance, selection, position or Z is integer arithmetic here; the only floating point is the height
// quantisation and is fenced (SCATTER_FP) so a text test can see it.
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainScatterTypes.h"

namespace ChimeraTerrain
{
	/** 1.0 in Q16. */
	constexpr int64 ScatterOneQ16 = 65536;

	/** lowbias32 (C. Wellons, "Prospecting for hash functions"): the shared hash; Python twin in make_scatter_meshes.py, golden in Scripts/scatter/mix32_golden.json. */
	inline uint32 Mix32(uint32 X)
	{
		X ^= X >> 16;
		X *= 0x7FEB352Du;
		X ^= X >> 15;
		X *= 0x846CA68Bu;
		X ^= X >> 16;
		return X;
	}

	/** h0 of a candidate (plan C scatter 3.3): the chain Mix32(seed ^ Mix32(stream)), then ^ ix, ^ iy, ^ slot, each through Mix32. */
	inline uint32 ScatterKey(uint32 Seed, uint32 Stream, int32 IX, int32 IY, uint32 Slot)
	{
		uint32 H = Mix32(Seed ^ Mix32(Stream));
		H = Mix32(H ^ static_cast<uint32>(IX));
		H = Mix32(H ^ static_cast<uint32>(IY));
		H = Mix32(H ^ Slot);
		return H;
	}

	/** Floor division by a positive divisor. */
	inline int64 FloorDiv(int64 A, int64 B)
	{
		int64 Q = A / B;
		if ((A % B) < 0)
		{
			--Q;
		}
		return Q;
	}

	/** Q16 x Q16 -> Q16, rounded half up. */
	inline int64 MulQ16(int64 A, int64 B)
	{
		return (A * B + 32768) >> 16;
	}

	/** 0 below A, 65536 above B, linear between (V, A, B in the same units, A < B). */
	inline int64 RampQ16(int64 V, int64 A, int64 B)
	{
		if (V <= A)
		{
			return 0;
		}
		if (V >= B)
		{
			return ScatterOneQ16;
		}
		return ((V - A) << 16) / (B - A);
	}

	/** One minus RampQ16. */
	inline int64 RampInvQ16(int64 V, int64 A, int64 B)
	{
		return ScatterOneQ16 - RampQ16(V, A, B);
	}

	/** Triangle: 0 up to A, 65536 at P, 0 from B on, linear between. */
	inline int64 BandQ16(int64 V, int64 A, int64 P, int64 B)
	{
		if (V <= A || V >= B)
		{
			return 0;
		}
		if (V < P)
		{
			return ((V - A) << 16) / (P - A);
		}
		if (V > P)
		{
			return ((B - V) << 16) / (B - P);
		}
		return ScatterOneQ16;
	}

	// SCATTER_FP_BEGIN: the one place a float enters: heights are quantised once per snapshot vertex.
	/** HQ16 = floor(h * 65536 + 0.5), exact scale (plan C scatter 3.3). */
	inline int32 HeightToQ16(float H)
	{
		return static_cast<int32>(FMath::FloorToDouble(static_cast<double>(H) * 65536.0 + 0.5));
	}
	// SCATTER_FP_END

	/**
	 * Value noise on a world lattice: corner values are the high 16 bits of Mix32(Mix32(StreamSeed ^ ix) ^ iy), interpolated with the smoothstep
	 * fade in Q16. XQ and YQ are Q16 metres, PeriodQ16 the lattice period in Q16 metres. Returns 0..65535.
	 */
	int64 ValueNoiseQ16(uint32 StreamSeed, int64 XQ, int64 YQ, int64 PeriodQ16);

	/** The cell and the corner heights a world position falls in; the surface and the gradient come from the same triangle. */
	struct FScatterCell
	{
		int32 CX = 0;
		int32 CY = 0;
		/** Position inside the cell in Q16 (65536 only at the clamped far edge). */
		int64 FU = 0;
		int64 FV = 0;
		int64 HBL = 0;
		int64 HBR = 0;
		int64 HTL = 0;
		int64 HTR = 0;
		/** True when the position lies in the triangle (BL, TL, TR) (FV >= FU), as SampleSurface does. */
		bool bUpper = false;
	};

	/** Locate (XQ, YQ) (Q16 metres from the centre, clamped to the map) in the snapshot. */
	FScatterCell ScatterLocateCell(const FScatterSnapshot& S, int64 XQ, int64 YQ);
	/** Surface height in Q16 of a located cell: one rounded combination (error <= 2^-16 m before the float rounding of SampleSurface). */
	int64 ScatterCellSurfaceQ16(const FScatterCell& C);
	/** Gradient of the position's own triangle in Q16 per metre (the HQ16 height differences, exact integers). */
	void ScatterCellGradientQ16(const FScatterCell& C, int32& OutGX, int32& OutGY);

	/** Integer twin of FTerrainHeightfield::SampleSurface in Q16 metres (|difference| <= 2^-15 m, gated in the tests). */
	int64 SurfaceQ16(const FScatterSnapshot& S, int64 XQ, int64 YQ);
	/** The triangle gradient at (XQ, YQ) in Q16 per metre. */
	void CellGradientQ16(const FScatterSnapshot& S, int64 XQ, int64 YQ, int32& OutGX, int32& OutGY);
	/** gx^2 + gy^2 in Q32 (tan^2 of the slope times 2^32); fits int64 for any legal terrain. */
	inline int64 SlopeG2Q32(int64 GX, int64 GY)
	{
		return GX * GX + GY * GY;
	}

	/** Bilinear splat weight at texel centres (T + 0.5) * 0.5 - E, edges clamped, rounded to a byte 0..255. Exact at texel centres. Channel 0..3 = grass, dirt, rock, snow. */
	int32 SplatQ8Bilinear(const FScatterSnapshot& S, int64 XQ, int64 YQ, int32 Channel);
	/** All four channels in one read. */
	void SplatQ8Bilinear4(const FScatterSnapshot& S, int64 XQ, int64 YQ, int32 Out[4]);
	/** Max bilinear weight of one channel at 8 points on a ring of radius RadiusQ16 (offsets rounded to Q16). */
	int32 SplatQ8Ring(const FScatterSnapshot& S, int64 XQ, int64 YQ, int64 RadiusQ16, int32 Channel);

	// ---- FNV-1a 64 with fixed-width little-endian helpers (never the struct bytes) ---------------------------------------
	constexpr uint64 Fnv64OffsetBasis = 14695981039346656037ull;
	constexpr uint64 Fnv64Prime = 1099511628211ull;

	inline uint64 Fnv1a64U8(uint64 H, uint32 V)
	{
		H ^= (V & 0xFFu);
		H *= Fnv64Prime;
		return H;
	}
	inline uint64 Fnv1a64U16(uint64 H, uint32 V)
	{
		H = Fnv1a64U8(H, V);
		return Fnv1a64U8(H, V >> 8);
	}
	inline uint64 Fnv1a64U32(uint64 H, uint32 V)
	{
		H = Fnv1a64U16(H, V);
		return Fnv1a64U16(H, V >> 16);
	}
	inline uint64 Fnv1a64U64(uint64 H, uint64 V)
	{
		H = Fnv1a64U32(H, static_cast<uint32>(V));
		return Fnv1a64U32(H, static_cast<uint32>(V >> 32));
	}
	/** FNV-1a 64 over a byte string (config names). */
	inline uint64 Fnv1a64Chars(uint64 H, const char* Text)
	{
		for (; *Text; ++Text)
		{
			H = Fnv1a64U8(H, static_cast<uint8>(*Text));
		}
		return H;
	}
}

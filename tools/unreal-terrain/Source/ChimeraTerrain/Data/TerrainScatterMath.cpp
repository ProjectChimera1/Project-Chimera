// Project Chimera terrain trial (plan C scatter 3.1, 3.3, task S2). Original Chimera code.
// Integer math of the scatter generator (see TerrainScatterMath.h). No floating point anywhere in this file.

#include "Data/TerrainScatterMath.h"

namespace ChimeraTerrain
{
	// ---- snapshot reads --------------------------------------------------------------------------------------------------

	int32& ScatterSnapshotMisses()
	{
		static thread_local int32 Misses = 0;
		return Misses;
	}

	int32 FScatterSnapshot::HeightAt(int32 VX, int32 VY) const
	{
		VX = FMath::Clamp(VX, 0, Verts - 1);
		VY = FMath::Clamp(VY, 0, Verts - 1);
		if (!VRect.Contains(VX, VY))
		{
			++ScatterSnapshotMisses();
			VX = FMath::Clamp(VX, VRect.X0, VRect.X1 - 1);
			VY = FMath::Clamp(VY, VRect.Y0, VRect.Y1 - 1);
		}
		return HQ16[(VY - VRect.Y0) * VRect.Width() + (VX - VRect.X0)];
	}

	const uint8* FScatterSnapshot::SplatAt(int32 TX, int32 TY) const
	{
		TX = FMath::Clamp(TX, 0, SplatTexels - 1);
		TY = FMath::Clamp(TY, 0, SplatTexels - 1);
		if (!TRect.Contains(TX, TY))
		{
			++ScatterSnapshotMisses();
			TX = FMath::Clamp(TX, TRect.X0, TRect.X1 - 1);
			TY = FMath::Clamp(TY, TRect.Y0, TRect.Y1 - 1);
		}
		return &Splat[((TY - TRect.Y0) * TRect.Width() + (TX - TRect.X0)) * 4];
	}

	// ---- value noise -----------------------------------------------------------------------------------------------------

	namespace
	{
		/** Smoothstep 3t^2 - 2t^3 in Q16, t in 0..65535 (a = t^2 >> 16, result = a * (3 - 2t) >> 16). */
		inline int64 ScatterFadeQ16(int64 T)
		{
			const int64 A = (T * T) >> 16;
			return (A * (196608 - 2 * T)) >> 16;
		}

		inline int64 ScatterCorner(uint32 StreamSeed, int64 IX, int64 IY)
		{
			const uint32 H = Mix32(Mix32(StreamSeed ^ static_cast<uint32>(static_cast<int32>(IX))) ^ static_cast<uint32>(static_cast<int32>(IY)));
			return static_cast<int64>(H >> 16);
		}
	}

	int64 ValueNoiseQ16(uint32 StreamSeed, int64 XQ, int64 YQ, int64 PeriodQ16)
	{
		const int64 IX = FloorDiv(XQ, PeriodQ16);
		const int64 IY = FloorDiv(YQ, PeriodQ16);
		const int64 TX = ((XQ - IX * PeriodQ16) << 16) / PeriodQ16;
		const int64 TY = ((YQ - IY * PeriodQ16) << 16) / PeriodQ16;
		const int64 SX = ScatterFadeQ16(TX);
		const int64 SY = ScatterFadeQ16(TY);
		const int64 C00 = ScatterCorner(StreamSeed, IX, IY);
		const int64 C10 = ScatterCorner(StreamSeed, IX + 1, IY);
		const int64 C01 = ScatterCorner(StreamSeed, IX, IY + 1);
		const int64 C11 = ScatterCorner(StreamSeed, IX + 1, IY + 1);
		const int64 A = C00 + (((C10 - C00) * SX) >> 16);
		const int64 B = C01 + (((C11 - C01) * SX) >> 16);
		return A + (((B - A) * SY) >> 16);
	}

	// ---- surface, gradient -----------------------------------------------------------------------------------------------

	FScatterCell ScatterLocateCell(const FScatterSnapshot& S, int64 XQ, int64 YQ)
	{
		const int64 Quads = S.Verts - 1;
		const int64 EQ = static_cast<int64>(S.HalfExtentM) << 16;
		const int64 UMax = Quads << 16;
		const int64 U = FMath::Clamp<int64>(XQ + EQ, 0, UMax);
		const int64 V = FMath::Clamp<int64>(YQ + EQ, 0, UMax);
		FScatterCell C;
		C.CX = static_cast<int32>(FMath::Min<int64>(U >> 16, Quads - 1));
		C.CY = static_cast<int32>(FMath::Min<int64>(V >> 16, Quads - 1));
		C.FU = U - (static_cast<int64>(C.CX) << 16);
		C.FV = V - (static_cast<int64>(C.CY) << 16);
		C.HBL = S.HeightAt(C.CX, C.CY);
		C.HBR = S.HeightAt(C.CX + 1, C.CY);
		C.HTL = S.HeightAt(C.CX, C.CY + 1);
		C.HTR = S.HeightAt(C.CX + 1, C.CY + 1);
		C.bUpper = C.FV >= C.FU;
		return C;
	}

	int64 ScatterCellSurfaceQ16(const FScatterCell& C)
	{
		int64 Sum;
		if (C.bUpper)
		{
			// Triangle (BL, TL, TR).
			Sum = C.HBL * 65536 + C.FU * (C.HTR - C.HTL) + C.FV * (C.HTL - C.HBL);
		}
		else
		{
			// Triangle (BL, TR, BR).
			Sum = C.HBL * 65536 + C.FU * (C.HBR - C.HBL) + C.FV * (C.HTR - C.HBR);
		}
		return (Sum + 32768) >> 16;
	}

	void ScatterCellGradientQ16(const FScatterCell& C, int32& OutGX, int32& OutGY)
	{
		if (C.bUpper)
		{
			OutGX = static_cast<int32>(C.HTR - C.HTL);
			OutGY = static_cast<int32>(C.HTL - C.HBL);
		}
		else
		{
			OutGX = static_cast<int32>(C.HBR - C.HBL);
			OutGY = static_cast<int32>(C.HTR - C.HBR);
		}
	}

	int64 SurfaceQ16(const FScatterSnapshot& S, int64 XQ, int64 YQ)
	{
		return ScatterCellSurfaceQ16(ScatterLocateCell(S, XQ, YQ));
	}

	void CellGradientQ16(const FScatterSnapshot& S, int64 XQ, int64 YQ, int32& OutGX, int32& OutGY)
	{
		ScatterCellGradientQ16(ScatterLocateCell(S, XQ, YQ), OutGX, OutGY);
	}

	// ---- splat -----------------------------------------------------------------------------------------------------------

	void SplatQ8Bilinear4(const FScatterSnapshot& S, int64 XQ, int64 YQ, int32 Out[4])
	{
		const int64 EQ = static_cast<int64>(S.HalfExtentM) << 16;
		// Continuous texel coordinate (texel T's centre is at T): s = (x + E) * 2 - 0.5 texels, in Q16.
		const int64 SX = ((XQ + EQ) * 2) - 32768;
		const int64 SY = ((YQ + EQ) * 2) - 32768;
		const int64 TX0 = SX >> 16;
		const int64 TY0 = SY >> 16;
		const int64 FX = SX - TX0 * 65536;
		const int64 FY = SY - TY0 * 65536;
		const uint8* P00 = S.SplatAt(static_cast<int32>(TX0), static_cast<int32>(TY0));
		const uint8* P10 = S.SplatAt(static_cast<int32>(TX0 + 1), static_cast<int32>(TY0));
		const uint8* P01 = S.SplatAt(static_cast<int32>(TX0), static_cast<int32>(TY0 + 1));
		const uint8* P11 = S.SplatAt(static_cast<int32>(TX0 + 1), static_cast<int32>(TY0 + 1));
		for (int32 Ch = 0; Ch < 4; ++Ch)
		{
			const int64 Row0 = static_cast<int64>(P00[Ch]) * (65536 - FX) + static_cast<int64>(P10[Ch]) * FX;
			const int64 Row1 = static_cast<int64>(P01[Ch]) * (65536 - FX) + static_cast<int64>(P11[Ch]) * FX;
			const int64 V = (Row0 * (65536 - FY) + Row1 * FY) >> 16;
			Out[Ch] = static_cast<int32>((V + 32768) >> 16);
		}
	}

	int32 SplatQ8Bilinear(const FScatterSnapshot& S, int64 XQ, int64 YQ, int32 Channel)
	{
		int32 W[4];
		SplatQ8Bilinear4(S, XQ, YQ, W);
		return W[Channel];
	}

	int32 SplatQ8Ring(const FScatterSnapshot& S, int64 XQ, int64 YQ, int64 RadiusQ16, int32 Channel)
	{
		// cos(k * 45 deg) rounded to Q16: 1, 0.70710678 (46341), 0.
		const int64 D = (RadiusQ16 * 46341 + 32768) >> 16;
		const int64 Off[8][2] = {{RadiusQ16, 0}, {D, D}, {0, RadiusQ16}, {-D, D}, {-RadiusQ16, 0}, {-D, -D}, {0, -RadiusQ16}, {D, -D}};
		int32 Best = 0;
		for (int32 K = 0; K < 8; ++K)
		{
			Best = FMath::Max(Best, SplatQ8Bilinear(S, XQ + Off[K][0], YQ + Off[K][1], Channel));
		}
		return Best;
	}
}

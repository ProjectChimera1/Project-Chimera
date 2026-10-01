// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Data/TerrainSimExport.h"

namespace ChimeraTerrain
{
	int32 TerrainSimExport::FixedRawFromFloat(float H)
	{
		// (int)(value * ONE): float multiply by 65536 (exact), then C-style truncation toward zero, never floor.
		return static_cast<int32>(H * 65536.0f);
	}

	int32 TerrainSimExport::CellToTexel(int32 CellIndex, int32 CellCount, int32 RegionSize)
	{
		if (CellCount <= 0 || RegionSize <= 0)
		{
			return 0;
		}
		const int64 Texel = ((2ll * CellIndex + 1ll) * RegionSize) / (2ll * CellCount);
		if (Texel < 0)
		{
			return 0;
		}
		if (Texel > RegionSize - 1)
		{
			return RegionSize - 1;
		}
		return static_cast<int32>(Texel);
	}

	int32 TerrainSimExport::VertexOffset(const FTerrainHeightfield& HF)
	{
		check(HF.HalfExtentM() >= SimGridHalfM);
		return HF.HalfExtentM() - SimGridHalfM;
	}

	int32 TerrainSimExport::CellToVertex(const FTerrainHeightfield& HF, int32 CellIndex)
	{
		return CellToTexel(CellIndex, SimGridCells, SimGridCells) + VertexOffset(HF);
	}

	void TerrainSimExport::BuildSimGrid(const FTerrainHeightfield& HF, TArray<int32>& OutRaw)
	{
		OutRaw.SetNumUninitialized(SimGridCells * SimGridCells);
		for (int32 Row = 0; Row < SimGridCells; ++Row)
		{
			const int32 VY = CellToVertex(HF, Row);
			for (int32 Col = 0; Col < SimGridCells; ++Col)
			{
				const int32 VX = CellToVertex(HF, Col);
				OutRaw[Row * SimGridCells + Col] = FixedRawFromFloat(HF.GetHeight(VX, VY));
			}
		}
	}

	uint32 TerrainSimExport::SimGridFnv(const TArray<int32>& Raw)
	{
		uint32 Hash = FnvOffsetBasis;
		for (const int32 V : Raw)
		{
			Hash = Fnv1aU32LE(Hash, static_cast<uint32>(V));
		}
		return Hash;
	}

	int32 TerrainSimExport::SampleSimGridRaw(const TArray<int32>& Raw, int32 WorldXRaw, int32 WorldZRaw)
	{
		if (Raw.Num() < SimGridCells * SimGridCells)
		{
			return 0;
		}
		constexpr int32 One = 1 << 16;
		constexpr int32 MinRaw = -SimGridHalfM * One;
		// Fixed.operator/: (int)(((long)a.Raw << 16) / b.Raw); Fixed.operator-: wrapping int subtraction; ToInt: Raw >> 16.
		const auto Axis = [&](int32 WorldRaw) -> int32
		{
			const int32 Diff = static_cast<int32>(static_cast<uint32>(WorldRaw) - static_cast<uint32>(MinRaw));
			const int64 Quot = (static_cast<int64>(Diff) * 65536ll) / One;
			int32 Index = static_cast<int32>(Quot) >> 16;
			return FMath::Clamp(Index, 0, SimGridCells - 1);
		};
		return Raw[Axis(WorldZRaw) * SimGridCells + Axis(WorldXRaw)];
	}

	void TerrainSimExport::GetProbePoints(TArray<FIntPoint>& Out)
	{
		constexpr int32 One = 1 << 16;
		Out.Reset();
		// Exact metres, cell boundaries (+-1 LSB), the map edge and beyond it (clamped), negatives, fractions, the sculpt area.
		Out.Add(FIntPoint(0, 0));
		Out.Add(FIntPoint(-128 * One, -128 * One));
		Out.Add(FIntPoint(128 * One - 1, 128 * One - 1));
		Out.Add(FIntPoint(128 * One, 128 * One));
		Out.Add(FIntPoint(-129 * One, -129 * One));
		Out.Add(FIntPoint(-One / 2, One / 2));
		Out.Add(FIntPoint(One, -One));
		Out.Add(FIntPoint(-One, -One));
		Out.Add(FIntPoint(-One - 1, -One - 1));
		Out.Add(FIntPoint(10 * One + One / 2, -3 * One - One / 4));
		Out.Add(FIntPoint(-77 * One - 3 * One / 4, 42 * One + One / 8));
		Out.Add(FIntPoint(32 * One - 1, 32 * One - 1));
		Out.Add(FIntPoint(32 * One, 32 * One));
		Out.Add(FIntPoint(64 * One, -64 * One));
		Out.Add(FIntPoint(100 * One + One / 2, 100 * One + One / 2));
		Out.Add(FIntPoint(-100 * One - One / 2, -100 * One - One / 2));
	}

	FVector TerrainSimExport::SimToUe(double SimX, double SimZ, double H)
	{
		return FVector(100.0 * SimX, 100.0 * SimZ, 100.0 * H);
	}

	const TCHAR* TerrainSimExport::AxesString()
	{
		return TEXT("sim(x,z,h)->ue_cm(100x,100z,100h)");
	}
}

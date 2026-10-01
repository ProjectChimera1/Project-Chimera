// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	uint32 Fnv1aBytes(uint32 Hash, const uint8* Data, int64 Num)
	{
		for (int64 I = 0; I < Num; ++I)
		{
			Hash ^= Data[I];
			Hash *= FnvPrime;
		}
		return Hash;
	}

	uint32 Fnv1aU32LE(uint32 Hash, uint32 Value)
	{
		for (int32 B = 0; B < 4; ++B)
		{
			Hash ^= (Value >> (8 * B)) & 0xFFu;
			Hash *= FnvPrime;
		}
		return Hash;
	}

	// ---- FTerrainRect ------------------------------------------------------------------------------------------

	void FTerrainRect::Include(int32 X, int32 Y)
	{
		if (IsEmpty())
		{
			X0 = X;
			Y0 = Y;
			X1 = X + 1;
			Y1 = Y + 1;
			return;
		}
		X0 = FMath::Min(X0, X);
		Y0 = FMath::Min(Y0, Y);
		X1 = FMath::Max(X1, X + 1);
		Y1 = FMath::Max(Y1, Y + 1);
	}

	void FTerrainRect::Union(const FTerrainRect& Other)
	{
		if (Other.IsEmpty())
		{
			return;
		}
		if (IsEmpty())
		{
			*this = Other;
			return;
		}
		X0 = FMath::Min(X0, Other.X0);
		Y0 = FMath::Min(Y0, Other.Y0);
		X1 = FMath::Max(X1, Other.X1);
		Y1 = FMath::Max(Y1, Other.Y1);
	}

	FTerrainRect FTerrainRect::Intersect(const FTerrainRect& Other) const
	{
		FTerrainRect R(FMath::Max(X0, Other.X0), FMath::Max(Y0, Other.Y0), FMath::Min(X1, Other.X1), FMath::Min(Y1, Other.Y1));
		if (R.IsEmpty())
		{
			return FTerrainRect();
		}
		return R;
	}

	// ---- FTerrainHeightfield -----------------------------------------------------------------------------------

	void FTerrainHeightfield::Init(int32 InHalfExtentM, int32 InChunkQuads)
	{
		check(InHalfExtentM >= 1);
		check(InChunkQuads >= 1);
		HalfExtent = InHalfExtentM;
		VertsPerSide = 2 * InHalfExtentM + 1;
		SplatTexels = SplatTexelsPerMeter * 2 * InHalfExtentM;
		ChunkSize = InChunkQuads;
		const int32 QuadCount = VertsPerSide - 1;
		NumChunksSide = (QuadCount + ChunkSize - 1) / ChunkSize;
		Reset();
	}

	void FTerrainHeightfield::Reset()
	{
		Heights.Init(0.0f, VertsPerSide * VertsPerSide);
		Splat.SetNumUninitialized(SplatTexels * SplatTexels * SplatLayerCount);
		const int32 TexelCount = SplatTexels * SplatTexels;
		for (int32 I = 0; I < TexelCount; ++I)
		{
			uint8* T = &Splat[I * SplatLayerCount];
			T[0] = 255;
			T[1] = 0;
			T[2] = 0;
			T[3] = 0;
		}
	}

	float FTerrainHeightfield::GetHeightClamped(int32 X, int32 Y) const
	{
		X = FMath::Clamp(X, 0, VertsPerSide - 1);
		Y = FMath::Clamp(Y, 0, VertsPerSide - 1);
		return Heights[Y * VertsPerSide + X];
	}

	void FTerrainHeightfield::SetHeight(int32 X, int32 Y, float H)
	{
		if (!(H == H))
		{
			H = 0.0f;
		}
		Heights[Y * VertsPerSide + X] = FMath::Clamp(H, -MaxHeightM, MaxHeightM);
	}

	FVector3f FTerrainHeightfield::GetNormal(int32 X, int32 Y) const
	{
		const int32 XM = FMath::Max(X - 1, 0);
		const int32 XP = FMath::Min(X + 1, VertsPerSide - 1);
		const int32 YM = FMath::Max(Y - 1, 0);
		const int32 YP = FMath::Min(Y + 1, VertsPerSide - 1);
		const float DhDx = (GetHeight(XP, Y) - GetHeight(XM, Y)) / static_cast<float>(XP - XM);
		const float DhDy = (GetHeight(X, YP) - GetHeight(X, YM)) / static_cast<float>(YP - YM);
		return FVector3f(-DhDx, -DhDy, 1.0f).GetSafeNormal();
	}

	FVector3f FTerrainHeightfield::GetTangent(int32 X, int32 Y) const
	{
		const int32 XM = FMath::Max(X - 1, 0);
		const int32 XP = FMath::Min(X + 1, VertsPerSide - 1);
		const float DhDx = (GetHeight(XP, Y) - GetHeight(XM, Y)) / static_cast<float>(XP - XM);
		return FVector3f(1.0f, 0.0f, DhDx).GetSafeNormal();
	}

	float FTerrainHeightfield::SampleSurface(double WorldX, double WorldY) const
	{
		const int32 Q = VertsPerSide - 1;
		const double U = FMath::Clamp(WorldX + HalfExtent, 0.0, static_cast<double>(Q));
		const double V = FMath::Clamp(WorldY + HalfExtent, 0.0, static_cast<double>(Q));
		const int32 CellX = FMath::Min(static_cast<int32>(FMath::FloorToDouble(U)), Q - 1);
		const int32 CellY = FMath::Min(static_cast<int32>(FMath::FloorToDouble(V)), Q - 1);
		const double FU = U - CellX;
		const double FV = V - CellY;
		const double HBL = GetHeight(CellX, CellY);
		const double HBR = GetHeight(CellX + 1, CellY);
		const double HTL = GetHeight(CellX, CellY + 1);
		const double HTR = GetHeight(CellX + 1, CellY + 1);
		double H;
		if (FV >= FU)
		{
			// Triangle (BL, TL, TR): above the BL-TR diagonal.
			H = HBL + FU * (HTR - HTL) + FV * (HTL - HBL);
		}
		else
		{
			// Triangle (BL, TR, BR): below the diagonal.
			H = HBL + FU * (HBR - HBL) + FV * (HTR - HBR);
		}
		return static_cast<float>(H);
	}

	FTerrainRect FTerrainHeightfield::ChunkOwnedRect(int32 Id) const
	{
		const int32 CX = Id % NumChunksSide;
		const int32 CY = Id / NumChunksSide;
		const int32 X0 = CX * ChunkSize;
		const int32 Y0 = CY * ChunkSize;
		const int32 X1 = (CX == NumChunksSide - 1) ? VertsPerSide : (CX + 1) * ChunkSize;
		const int32 Y1 = (CY == NumChunksSide - 1) ? VertsPerSide : (CY + 1) * ChunkSize;
		return FTerrainRect(X0, Y0, X1, Y1);
	}

	FTerrainRect FTerrainHeightfield::ChunkRenderRect(int32 Id) const
	{
		const int32 CX = Id % NumChunksSide;
		const int32 CY = Id / NumChunksSide;
		const int32 X0 = CX * ChunkSize;
		const int32 Y0 = CY * ChunkSize;
		return FTerrainRect(X0, Y0, FMath::Min(X0 + ChunkSize + 1, VertsPerSide), FMath::Min(Y0 + ChunkSize + 1, VertsPerSide));
	}

	FTerrainRect FTerrainHeightfield::VertexRectToSplatRect(const FTerrainRect& VertexRect) const
	{
		if (VertexRect.IsEmpty())
		{
			return FTerrainRect();
		}
		return FTerrainRect(
			VertexRect.X0 * SplatTexelsPerMeter, VertexRect.Y0 * SplatTexelsPerMeter,
			VertexRect.X1 * SplatTexelsPerMeter, VertexRect.Y1 * SplatTexelsPerMeter).Intersect(FTerrainRect(0, 0, SplatTexels, SplatTexels));
	}

	FTerrainRect FTerrainHeightfield::ChunkOwnedSplatRect(int32 Id) const
	{
		return VertexRectToSplatRect(ChunkOwnedRect(Id));
	}

	int32 FTerrainHeightfield::OwnerChunkOfVertex(int32 X, int32 Y) const
	{
		const int32 CX = FMath::Min(X / ChunkSize, NumChunksSide - 1);
		const int32 CY = FMath::Min(Y / ChunkSize, NumChunksSide - 1);
		return ChunkId(CX, CY);
	}

	void FTerrainHeightfield::ChunksOwningRect(const FTerrainRect& VertexRect, TArray<int32>& Out) const
	{
		Out.Reset();
		const FTerrainRect R = VertexRect.Intersect(FTerrainRect(0, 0, VertsPerSide, VertsPerSide));
		if (R.IsEmpty())
		{
			return;
		}
		const int32 CX0 = FMath::Min(R.X0 / ChunkSize, NumChunksSide - 1);
		const int32 CX1 = FMath::Min((R.X1 - 1) / ChunkSize, NumChunksSide - 1);
		const int32 CY0 = FMath::Min(R.Y0 / ChunkSize, NumChunksSide - 1);
		const int32 CY1 = FMath::Min((R.Y1 - 1) / ChunkSize, NumChunksSide - 1);
		for (int32 CY = CY0; CY <= CY1; ++CY)
		{
			for (int32 CX = CX0; CX <= CX1; ++CX)
			{
				Out.Add(ChunkId(CX, CY));
			}
		}
	}

	void FTerrainHeightfield::ChunksOwningSplatRect(const FTerrainRect& TexelRect, TArray<int32>& Out) const
	{
		Out.Reset();
		const FTerrainRect R = TexelRect.Intersect(FTerrainRect(0, 0, SplatTexels, SplatTexels));
		if (R.IsEmpty())
		{
			return;
		}
		// Texel T belongs to vertex T / 2, so the vertex rect is [X0/2, (X1-1)/2 + 1).
		const FTerrainRect VertexRect(R.X0 / SplatTexelsPerMeter, R.Y0 / SplatTexelsPerMeter, (R.X1 - 1) / SplatTexelsPerMeter + 1, (R.Y1 - 1) / SplatTexelsPerMeter + 1);
		ChunksOwningRect(VertexRect, Out);
	}

	void FTerrainHeightfield::ChunksRenderOverlappingRect(const FTerrainRect& VertexRect, TArray<int32>& Out) const
	{
		Out.Reset();
		const FTerrainRect R = VertexRect.Intersect(FTerrainRect(0, 0, VertsPerSide, VertsPerSide));
		if (R.IsEmpty())
		{
			return;
		}
		// Chunk cx renders vertices [cx*C, cx*C + C + 1): it overlaps [X0, X1) when cx*C < X1 and cx*C + C + 1 > X0.
		const int32 CX0 = R.X0 > 0 ? (R.X0 - 1) / ChunkSize : 0;
		const int32 CX1 = FMath::Min((R.X1 - 1) / ChunkSize, NumChunksSide - 1);
		const int32 CY0 = R.Y0 > 0 ? (R.Y0 - 1) / ChunkSize : 0;
		const int32 CY1 = FMath::Min((R.Y1 - 1) / ChunkSize, NumChunksSide - 1);
		for (int32 CY = CY0; CY <= CY1; ++CY)
		{
			for (int32 CX = CX0; CX <= CX1; ++CX)
			{
				Out.Add(ChunkId(CX, CY));
			}
		}
	}

	void FTerrainHeightfield::GetChunkZRange(int32 Id, float& OutMin, float& OutMax) const
	{
		const FTerrainRect R = ChunkRenderRect(Id);
		OutMin = TNumericLimits<float>::Max();
		OutMax = -TNumericLimits<float>::Max();
		for (int32 Y = R.Y0; Y < R.Y1; ++Y)
		{
			for (int32 X = R.X0; X < R.X1; ++X)
			{
				const float H = GetHeight(X, Y);
				OutMin = FMath::Min(OutMin, H);
				OutMax = FMath::Max(OutMax, H);
			}
		}
	}

	uint32 FTerrainHeightfield::HeightFnv() const
	{
		uint32 Hash = FnvOffsetBasis;
		for (const float H : Heights)
		{
			uint32 Bits;
			FMemory::Memcpy(&Bits, &H, sizeof(Bits));
			Hash = Fnv1aU32LE(Hash, Bits);
		}
		return Hash;
	}

	uint32 FTerrainHeightfield::SplatFnv() const
	{
		return Fnv1aBytes(FnvOffsetBasis, Splat.GetData(), Splat.Num());
	}
}

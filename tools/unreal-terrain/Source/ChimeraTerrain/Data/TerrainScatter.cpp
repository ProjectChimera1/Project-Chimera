// Project Chimera terrain trial (plan C scatter 3.1-3.5, task S2). Original Chimera code.
// The scatter generator. Every accept, select, position and Z decision below is integer arithmetic on the world-anchored lattice (plan C
// scatter 3.3); the only floating point is BuildInstance (record -> transform and custom data), fenced with SCATTER_FP markers.

#include "Data/TerrainScatter.h"
#include "Data/TerrainScatterMath.h"

#include <algorithm>
#include <cmath>

#include "Async/ParallelFor.h"

namespace ChimeraTerrain
{
	// ---- names, grids, layers ---------------------------------------------------------------------------------------------

	EScatterGrid ScatterGridOfClass(EScatterClass Class)
	{
		switch (Class)
		{
		case EScatterClass::Grass:
		case EScatterClass::Tussock:
		case EScatterClass::Flower:
		case EScatterClass::NearCard:
			return EScatterGrid::Fine;
		default:
			return EScatterGrid::Coarse;
		}
	}

	EScatterGrid ScatterGridOfMesh(EScatterMesh Mesh)
	{
		return static_cast<int32>(Mesh) <= static_cast<int32>(EScatterMesh::NearCard) ? EScatterGrid::Fine : EScatterGrid::Coarse;
	}

	EScatterLayer ScatterLayerOfClass(EScatterClass Class)
	{
		switch (Class)
		{
		case EScatterClass::Grass:
		case EScatterClass::NearCard:
			return EScatterLayer::Grass;
		case EScatterClass::Tussock:
		case EScatterClass::Flower:
		case EScatterClass::Fern:
			return EScatterLayer::Groundcover;
		case EScatterClass::Shrub:
			return EScatterLayer::Shrubs;
		case EScatterClass::Tree:
		case EScatterClass::Sapling:
			return EScatterLayer::Trees;
		default:
			return EScatterLayer::Rocks;
		}
	}

	const char* ScatterClassName(EScatterClass Class)
	{
		static const char* const Names[] = {"Grass", "Tussock", "Flower", "NearCard", "Tree", "Sapling", "Shrub", "Fern", "Rock"};
		return Names[static_cast<int32>(Class)];
	}

	const char* ScatterMeshName(EScatterMesh Mesh)
	{
		static const char* const Names[] = {"GrassT0", "GrassT1", "Tussock", "Flower", "NearCard", "TreeBroadA", "TreeBroadB", "TreeConiferA", "TreeConiferB", "ShrubA", "ShrubB", "Fern", "RockA", "RockB"};
		return Names[static_cast<int32>(Mesh)];
	}

	const char* ScatterLayerName(EScatterLayer Layer)
	{
		static const char* const Names[] = {"grass", "groundcover", "shrubs", "trees", "rocks"};
		return Names[static_cast<int32>(Layer)];
	}

	void ScatterMeshesOfGrid(EScatterGrid Grid, TArray<EScatterMesh>& Out)
	{
		Out.Reset();
		for (int32 M = 0; M < ScatterMeshCount; ++M)
		{
			if (ScatterGridOfMesh(static_cast<EScatterMesh>(M)) == Grid)
			{
				Out.Add(static_cast<EScatterMesh>(M));
			}
		}
	}

	int32 ScatterMeshIndexInGrid(EScatterMesh Mesh)
	{
		const int32 M = static_cast<int32>(Mesh);
		return M <= static_cast<int32>(EScatterMesh::NearCard) ? M : M - (static_cast<int32>(EScatterMesh::NearCard) + 1);
	}

	int32 ScatterMeshCountOfGrid(EScatterGrid Grid)
	{
		return Grid == EScatterGrid::Fine ? static_cast<int32>(EScatterMesh::NearCard) + 1 : ScatterMeshCount - (static_cast<int32>(EScatterMesh::NearCard) + 1);
	}

	bool ScatterMeshExistsAtLevel(EScatterMesh Mesh, EScatterLevel Level)
	{
		return Mesh != EScatterMesh::NearCard || Level == EScatterLevel::L1;
	}

	// ---- unit state, hash sets ----------------------------------------------------------------------------------------------

	void FScatterUnitState::RebuildSorted()
	{
		Sorted.Reset();
		Sorted.Reserve(Records.Num());
		for (int32 I = 0; I < Records.Num(); ++I)
		{
			FScatterKeyIndex E;
			E.Key = Records[I].Key();
			E.Index = I;
			Sorted.Add(E);
		}
		std::sort(Sorted.GetData(), Sorted.GetData() + Sorted.Num(), [](const FScatterKeyIndex& A, const FScatterKeyIndex& B) { return A.Key < B.Key; });
	}

	int32 FScatterUnitState::Find(uint64 Key) const
	{
		int32 Lo = 0;
		int32 Hi = Sorted.Num();
		while (Lo < Hi)
		{
			const int32 Mid = (Lo + Hi) >> 1;
			if (Sorted[Mid].Key < Key)
			{
				Lo = Mid + 1;
			}
			else
			{
				Hi = Mid;
			}
		}
		return (Lo < Sorted.Num() && Sorted[Lo].Key == Key) ? Sorted[Lo].Index : INDEX_NONE;
	}

	int64 FScatterHash::TotalCount() const
	{
		int64 N = 0;
		for (int32 C = 0; C < ScatterClassCount; ++C)
		{
			N += Classes[C].Count;
		}
		return N;
	}

	bool FScatterHash::operator==(const FScatterHash& O) const
	{
		for (int32 C = 0; C < ScatterClassCount; ++C)
		{
			if (Classes[C].Count != O.Classes[C].Count || Classes[C].Sum != O.Classes[C].Sum || Classes[C].Xor != O.Classes[C].Xor)
			{
				return false;
			}
		}
		return true;
	}

	uint64 HashRecord(const FScatterRecord& R)
	{
		uint64 H = Fnv64OffsetBasis;
		H = Fnv1a64U8(H, R.Class);
		H = Fnv1a64U8(H, R.Mesh);
		H = Fnv1a64U16(H, R.Stream);
		H = Fnv1a64U32(H, static_cast<uint32>(R.IX));
		H = Fnv1a64U32(H, static_cast<uint32>(R.IY));
		H = Fnv1a64U8(H, R.Slot);
		H = Fnv1a64U32(H, static_cast<uint32>(R.XQ));
		H = Fnv1a64U32(H, static_cast<uint32>(R.YQ));
		H = Fnv1a64U32(H, static_cast<uint32>(R.ZQ));
		H = Fnv1a64U32(H, static_cast<uint32>(R.GXQ));
		H = Fnv1a64U32(H, static_cast<uint32>(R.GYQ));
		H = Fnv1a64U16(H, R.Yaw);
		H = Fnv1a64U16(H, R.Scale);
		H = Fnv1a64U16(H, R.ZScale);
		H = Fnv1a64U16(H, R.Tilt);
		H = Fnv1a64U16(H, R.CD0);
		H = Fnv1a64U16(H, R.CD1);
		return H;
	}

	void AppendScatterRecordBytes(TArray<uint8>& Out, const FScatterRecord& R)
	{
		auto Put = [&Out](uint32 V, int32 Bytes)
		{
			for (int32 B = 0; B < Bytes; ++B)
			{
				Out.Add(static_cast<uint8>((V >> (8 * B)) & 0xFFu));
			}
		};
		Put(R.Class, 1);
		Put(R.Mesh, 1);
		Put(R.Stream, 2);
		Put(static_cast<uint32>(R.IX), 4);
		Put(static_cast<uint32>(R.IY), 4);
		Put(R.Slot, 1);
		Put(static_cast<uint32>(R.XQ), 4);
		Put(static_cast<uint32>(R.YQ), 4);
		Put(static_cast<uint32>(R.ZQ), 4);
		Put(static_cast<uint32>(R.GXQ), 4);
		Put(static_cast<uint32>(R.GYQ), 4);
		Put(R.Yaw, 2);
		Put(R.Scale, 2);
		Put(R.ZScale, 2);
		Put(R.Tilt, 2);
		Put(R.CD0, 2);
		Put(R.CD1, 2);
	}

	void ScatterFoldRecord(FScatterHash& H, const FScatterRecord& R)
	{
		const uint64 Hr = HashRecord(R);
		FScatterClassHash& C = H.Classes[R.Class];
		C.Count += 1;
		C.Sum += Hr;
		C.Xor ^= Hr;
	}

	void ScatterFoldState(FScatterHash& H, const FScatterUnitState& State)
	{
		for (const FScatterRecord& R : State.Records)
		{
			ScatterFoldRecord(H, R);
		}
	}

	void ScatterCombine(FScatterHash& Into, const FScatterHash& Add)
	{
		for (int32 C = 0; C < ScatterClassCount; ++C)
		{
			Into.Classes[C].Count += Add.Classes[C].Count;
			Into.Classes[C].Sum += Add.Classes[C].Sum;
			Into.Classes[C].Xor ^= Add.Classes[C].Xor;
		}
	}

	uint64 ScatterFnv(uint64 ConfigFnv, int32 HalfExtentM, EScatterLevel Level, const FScatterHash& H)
	{
		uint64 F = Fnv64OffsetBasis;
		F = Fnv1a64U64(F, ConfigFnv);
		F = Fnv1a64U32(F, static_cast<uint32>(HalfExtentM));
		F = Fnv1a64U8(F, static_cast<uint32>(Level));
		for (int32 C = 0; C < ScatterClassCount; ++C)
		{
			F = Fnv1a64U64(F, static_cast<uint64>(H.Classes[C].Count));
			F = Fnv1a64U64(F, H.Classes[C].Sum);
			F = Fnv1a64U64(F, H.Classes[C].Xor);
		}
		return F;
	}

	// ---- tiles --------------------------------------------------------------------------------------------------------------

	FScatterTileGrid MakeScatterTileGrid(int32 HalfExtentM, int32 SizeM)
	{
		FScatterTileGrid G;
		G.SizeM = SizeM;
		G.Min = static_cast<int32>(FloorDiv(-HalfExtentM, SizeM));
		const int32 Max = static_cast<int32>(FloorDiv(HalfExtentM - 1, SizeM));
		G.Count = Max - G.Min + 1;
		return G;
	}

	FScatterTileKey ScatterTileKeyAt(EScatterGrid Grid, const FScatterTileGrid& TileGrid, int32 Index)
	{
		FScatterTileKey K;
		K.Grid = Grid;
		K.TX = TileGrid.TXOf(Index);
		K.TY = TileGrid.TYOf(Index);
		K.SizeM = TileGrid.SizeM;
		return K;
	}

	int64 ScatterApronQ16(EScatterGrid Grid)
	{
		// 2.5 m fine (1.5 m verge ring plus bilinear reach), 5 m coarse (4 m probe ring plus bilinear reach).
		return Grid == EScatterGrid::Fine ? 163840 : 327680;
	}

	namespace
	{
		FScatterTileRange RangeFromWorldQ16(const FScatterTileGrid& TG, int64 LoXQ, int64 LoYQ, int64 HiXQ, int64 HiYQ)
		{
			const int64 M = static_cast<int64>(TG.SizeM) << 16;
			FScatterTileRange R;
			R.MinX = static_cast<int32>(FMath::Max<int64>(FloorDiv(LoXQ, M), TG.Min));
			R.MinY = static_cast<int32>(FMath::Max<int64>(FloorDiv(LoYQ, M), TG.Min));
			R.MaxX = static_cast<int32>(FMath::Min<int64>(FloorDiv(HiXQ, M), TG.Min + TG.Count - 1));
			R.MaxY = static_cast<int32>(FMath::Min<int64>(FloorDiv(HiYQ, M), TG.Min + TG.Count - 1));
			return R;
		}
	}

	FScatterTileRange ScatterTileRangeForVertexRect(const FScatterTileGrid& TG, int32 E, const FTerrainRect& V, int64 ApronQ16)
	{
		if (V.IsEmpty())
		{
			return FScatterTileRange();
		}
		const int64 EQ = static_cast<int64>(E) * 65536;
		return RangeFromWorldQ16(TG, static_cast<int64>(V.X0 - 1) * 65536 - EQ - ApronQ16, static_cast<int64>(V.Y0 - 1) * 65536 - EQ - ApronQ16,
			static_cast<int64>(V.X1) * 65536 - EQ + ApronQ16, static_cast<int64>(V.Y1) * 65536 - EQ + ApronQ16);
	}

	FScatterTileRange ScatterTileRangeForSplatRect(const FScatterTileGrid& TG, int32 E, const FTerrainRect& T, int64 ApronQ16)
	{
		if (T.IsEmpty())
		{
			return FScatterTileRange();
		}
		const int64 EQ = static_cast<int64>(E) * 65536;
		return RangeFromWorldQ16(TG, static_cast<int64>(T.X0) * 32768 - EQ - 32768 - ApronQ16, static_cast<int64>(T.Y0) * 32768 - EQ - 32768 - ApronQ16,
			static_cast<int64>(T.X1) * 32768 - EQ + 32768 + ApronQ16, static_cast<int64>(T.Y1) * 32768 - EQ + 32768 + ApronQ16);
	}

	void ScatterDirtyTiles(const FScatterTileGrid& TG, EScatterGrid Grid, int32 E, const FTerrainRect& VertexRect, const FTerrainRect& SplatRect, TArray<int32>& Out)
	{
		Out.Reset();
		const int64 Apron = ScatterApronQ16(Grid);
		const FScatterTileRange A = ScatterTileRangeForVertexRect(TG, E, VertexRect, Apron);
		const FScatterTileRange B = ScatterTileRangeForSplatRect(TG, E, SplatRect, Apron);
		for (int32 TY = TG.Min; TY < TG.Min + TG.Count; ++TY)
		{
			for (int32 TX = TG.Min; TX < TG.Min + TG.Count; ++TX)
			{
				const bool bInA = !A.IsEmpty() && TX >= A.MinX && TX <= A.MaxX && TY >= A.MinY && TY <= A.MaxY;
				const bool bInB = !B.IsEmpty() && TX >= B.MinX && TX <= B.MaxX && TY >= B.MinY && TY <= B.MaxY;
				if (bInA || bInB)
				{
					Out.Add(TG.IndexOf(TX, TY));
				}
			}
		}
	}

	// ---- snapshots ----------------------------------------------------------------------------------------------------------

	namespace
	{
		/** Snapshot apron in metres: larger than any read (rings 3 m, probes 4 m, bilinear reach 1 m). */
		int32 SnapshotApronM(EScatterGrid Grid)
		{
			return Grid == EScatterGrid::Fine ? 4 : 6;
		}

		void FillSnapshot(const FTerrainHeightfield& HF, const FTerrainRect& VRect, const FTerrainRect& TRect, FScatterSnapshot& S)
		{
			S.HalfExtentM = HF.HalfExtentM();
			S.Verts = HF.Width();
			S.SplatTexels = HF.SplatSize();
			S.VRect = VRect;
			S.TRect = TRect;
			const int32 VW = VRect.Width();
			const int32 VH = VRect.Height();
			S.HQ16.SetNumUninitialized(VW * VH);
			for (int32 Y = 0; Y < VH; ++Y)
			{
				for (int32 X = 0; X < VW; ++X)
				{
					S.HQ16[Y * VW + X] = HeightToQ16(HF.GetHeight(VRect.X0 + X, VRect.Y0 + Y));
				}
			}
			const int32 TW = TRect.Width();
			const int32 TH = TRect.Height();
			S.Splat.SetNumUninitialized(TW * TH * 4);
			for (int32 Y = 0; Y < TH; ++Y)
			{
				FMemory::Memcpy(&S.Splat[Y * TW * 4], HF.SplatTexel(TRect.X0, TRect.Y0 + Y), static_cast<SIZE_T>(TW) * 4);
			}
		}
	}

	FScatterSnapshot MakeSnapshot(const FTerrainHeightfield& HF, const FScatterTileKey& Tile)
	{
		const int32 E = HF.HalfExtentM();
		const int32 A = SnapshotApronM(Tile.Grid);
		const int32 X0 = Tile.TX * Tile.SizeM;
		const int32 Y0 = Tile.TY * Tile.SizeM;
		const int32 X1 = X0 + Tile.SizeM;
		const int32 Y1 = Y0 + Tile.SizeM;
		FTerrainRect V(FMath::Max(0, X0 + E - A - 1), FMath::Max(0, Y0 + E - A - 1), FMath::Min(HF.Width(), X1 + E + A + 2), FMath::Min(HF.Width(), Y1 + E + A + 2));
		FTerrainRect T(FMath::Max(0, (X0 + E - A) * 2 - 1), FMath::Max(0, (Y0 + E - A) * 2 - 1), FMath::Min(HF.SplatSize(), (X1 + E + A) * 2 + 2),
			FMath::Min(HF.SplatSize(), (Y1 + E + A) * 2 + 2));
		if (V.IsEmpty() || T.IsEmpty())
		{
			// A tile wholly outside the map: keep one vertex and texel so reads clamp safely.
			V = FTerrainRect(0, 0, 1, 1);
			T = FTerrainRect(0, 0, 1, 1);
		}
		FScatterSnapshot S;
		FillSnapshot(HF, V, T, S);
		return S;
	}

	FScatterSnapshot MakeFullSnapshot(const FTerrainHeightfield& HF)
	{
		FScatterSnapshot S;
		FillSnapshot(HF, FTerrainRect(0, 0, HF.Width(), HF.Width()), FTerrainRect(0, 0, HF.SplatSize(), HF.SplatSize()), S);
		return S;
	}

	// ---- fields -------------------------------------------------------------------------------------------------------------

	namespace
	{
		// Fixed integer stream ids of the classes and fields (never enum order: adding a class reshuffles nothing).
		constexpr uint32 ClassStream[ScatterClassCount] = {11, 12, 13, 14, 15, 16, 17, 18, 19};
		constexpr uint32 StreamW1 = 101, StreamW2 = 102, StreamWx = 103, StreamWy = 104;
		constexpr uint32 StreamM1 = 111, StreamM2 = 112;
		constexpr uint32 StreamT = 121, StreamTx = 122, StreamTy = 123;
		constexpr uint32 StreamF1 = 131, StreamF2 = 132;
		constexpr uint32 StreamFc = 141, StreamC = 151, StreamK = 161, StreamFs = 171, StreamFh = 181, StreamFr = 191;

		// Sub-draw constants: sub = Mix32(h0 ^ k).
		constexpr uint32 KRoll = 0xA001u, KYaw = 0xA002u, KScale = 0xA003u, KScaleZ = 0xA004u, KTilt = 0xA005u, KCd = 0xA006u, KVar = 0xA007u, KRank = 0xA008u,
			KType = 0xA009u, KColour = 0xA00Au, KGrove = 0x51u, KGroveR = 0x52u;

		struct FFieldSeeds
		{
			uint32 W1, W2, Wx, Wy, M1, M2, T, Tx, Ty, F1, F2, Fc, C, K, Fs, Fh, Fr;
		};

		FFieldSeeds MakeFieldSeeds(uint32 Seed)
		{
			auto S = [Seed](uint32 Stream) { return Mix32(Seed ^ Mix32(Stream)); };
			FFieldSeeds F;
			F.W1 = S(StreamW1); F.W2 = S(StreamW2); F.Wx = S(StreamWx); F.Wy = S(StreamWy);
			F.M1 = S(StreamM1); F.M2 = S(StreamM2);
			F.T = S(StreamT); F.Tx = S(StreamTx); F.Ty = S(StreamTy);
			F.F1 = S(StreamF1); F.F2 = S(StreamF2);
			F.Fc = S(StreamFc); F.C = S(StreamC); F.K = S(StreamK); F.Fs = S(StreamFs); F.Fh = S(StreamFh); F.Fr = S(StreamFr);
			return F;
		}

		/** Two octaves: the second on exactly rotated integer coordinates (floor((4x - 3y) / 5), floor((3x + 4y) / 5)) so patterns stay off the axes. */
		int64 TwoOctave(uint32 S1, uint32 S2, int64 X, int64 Y, int64 Period1, int64 Period2, int64 Weight1)
		{
			const int64 N1 = ValueNoiseQ16(S1, X, Y, Period1);
			const int64 RX = FloorDiv(4 * X - 3 * Y, 5);
			const int64 RY = FloorDiv(3 * X + 4 * Y, 5);
			const int64 N2 = ValueNoiseQ16(S2, RX, RY, Period2);
			return (Weight1 * N1 + (65536 - Weight1) * N2 + 32768) >> 16;
		}

		/** Warp offset in Q16 metres: +-Amp from a noise of the given period. */
		int64 WarpOffset(uint32 Seed, int64 X, int64 Y, int64 PeriodQ16, int64 AmpQ16)
		{
			const int64 N = ValueNoiseQ16(Seed, X, Y, PeriodQ16);
			return (((N - 32768) * 2) * AmpQ16) >> 16;
		}

		int64 FieldValue(const FScatterPalette& P, const FFieldSeeds& FS, EScatterField Field, int64 X, int64 Y)
		{
			switch (Field)
			{
			case EScatterField::Woodland:
			case EScatterField::WoodlandBiased:
			{
				const int64 Amp = P.Get(EScatterParam::WWarpM);
				// The warp noise's period is a palette row (WWarpPeriodM, 64 m), like its amplitude.
				const int64 Period = P.Get(EScatterParam::WWarpPeriodM);
				const int64 WX = X + WarpOffset(FS.Wx, X, Y, Period, Amp);
				const int64 WY = Y + WarpOffset(FS.Wy, X, Y, Period, Amp);
				int64 W = TwoOctave(FS.W1, FS.W2, WX, WY, P.Get(EScatterParam::WPeriod1M), P.Get(EScatterParam::WPeriod2M), P.Get(EScatterParam::WWeight1));
				if (Field == EScatterField::WoodlandBiased)
				{
					const int64 R = FMath::Max(FMath::Abs(X), FMath::Abs(Y));
					const int64 Bias = MulQ16(P.Get(EScatterParam::BorderBias), RampQ16(R, P.Get(EScatterParam::BorderLoM), P.Get(EScatterParam::BorderHiM)));
					W = FMath::Min<int64>(65536, W + Bias);
				}
				return W;
			}
			case EScatterField::Dryness:
				return TwoOctave(FS.M1, FS.M2, X, Y, P.Get(EScatterParam::MPeriod1M), P.Get(EScatterParam::MPeriod2M), P.Get(EScatterParam::MWeight1));
			case EScatterField::Tussock:
			{
				const int64 Amp = P.Get(EScatterParam::TWarpM);
				const int64 Period = P.Get(EScatterParam::TWarpPeriodM);
				const int64 WX = X + WarpOffset(FS.Tx, X, Y, Period, Amp);
				const int64 WY = Y + WarpOffset(FS.Ty, X, Y, Period, Amp);
				return ValueNoiseQ16(FS.T, WX, WY, P.Get(EScatterParam::TPeriodM));
			}
			case EScatterField::Drift:
				return TwoOctave(FS.F1, FS.F2, X, Y, P.Get(EScatterParam::FPeriod1M), P.Get(EScatterParam::FPeriod2M), P.Get(EScatterParam::FWeight1));
			case EScatterField::DriftColour:
				return ValueNoiseQ16(FS.Fc, X, Y, P.Get(EScatterParam::FcPeriodM));
			case EScatterField::Stand:
				return ValueNoiseQ16(FS.C, X, Y, P.Get(EScatterParam::CPeriodM));
			case EScatterField::ShrubClump:
				return ValueNoiseQ16(FS.Fs, X, Y, P.Get(EScatterParam::FsPeriodM));
			case EScatterField::Hedge:
				return ValueNoiseQ16(FS.Fh, X, Y, P.Get(EScatterParam::FhPeriodM));
			case EScatterField::Boulder:
				return ValueNoiseQ16(FS.Fr, X, Y, P.Get(EScatterParam::FrPeriodM));
			default:
				return 0;
			}
		}

		bool GroveAt(const FScatterPalette& P, const FFieldSeeds&, int64 X, int64 Y, bool* bOutFringe)
		{
			if (bOutFringe)
			{
				*bOutFringe = false;
			}
			const int64 Cell = P.Get(EScatterParam::KCellM);
			const int64 KX = FloorDiv(X, Cell);
			const int64 KY = FloorDiv(Y, Cell);
			const int64 RLo = P.Get(EScatterParam::KRadiusLoM);
			const int64 RHi = P.Get(EScatterParam::KRadiusHiM);
			const int64 FringeIn = P.Get(EScatterParam::SaplingFringeIn);
			const int64 FringeOut = P.Get(EScatterParam::SaplingFringeOut);
			bool bIn = false;
			for (int64 DY = -1; DY <= 1; ++DY)
			{
				for (int64 DX = -1; DX <= 1; ++DX)
				{
					const int32 CX = static_cast<int32>(KX + DX);
					const int32 CY = static_cast<int32>(KY + DY);
					const uint32 H = ScatterKey(P.Seed, StreamK, CX, CY, 0);
					if (static_cast<int64>(Mix32(H ^ KGrove) & 0xFFFFu) >= P.Get(EScatterParam::KP))
					{
						continue;
					}
					const int64 CXQ = static_cast<int64>(CX) * Cell + (((H & 0xFFFFu) * Cell) >> 16);
					const int64 CYQ = static_cast<int64>(CY) * Cell + (((H >> 16) * Cell) >> 16);
					const int64 R = RLo + (((RHi - RLo) * static_cast<int64>(Mix32(H ^ KGroveR) & 0xFFFFu)) >> 16);
					const int64 DXQ = X - CXQ;
					const int64 DYQ = Y - CYQ;
					const int64 D2 = DXQ * DXQ + DYQ * DYQ;
					if (D2 <= R * R)
					{
						bIn = true;
					}
					if (bOutFringe)
					{
						const int64 RI = (R * FringeIn) >> 16;
						const int64 RO = (R * FringeOut) >> 16;
						if (D2 >= RI * RI && D2 <= RO * RO)
						{
							*bOutFringe = true;
						}
					}
				}
			}
			return bIn;
		}
	}

	int64 ScatterFieldQ16(const FScatterPalette& P, EScatterField Field, int64 XQ, int64 YQ)
	{
		return FieldValue(P, MakeFieldSeeds(P.Seed), Field, XQ, YQ);
	}

	bool ScatterInGrove(const FScatterPalette& P, int64 XQ, int64 YQ, bool* bOutFringe)
	{
		return GroveAt(P, MakeFieldSeeds(P.Seed), XQ, YQ, bOutFringe);
	}

	namespace
	{
		struct FZone
		{
			EScatterTreeZone Zone;
			int64 P;
		};

		FZone TreeZone(const FScatterPalette& P, const FFieldSeeds& FS, int64 X, int64 Y, int64 Wp)
		{
			FZone Best{EScatterTreeZone::Lone, P.Get(EScatterParam::TreeLoneP)};
			if (Wp >= P.Get(EScatterParam::TreeCoreW) && P.Get(EScatterParam::TreeCoreP) > Best.P)
			{
				Best = {EScatterTreeZone::Core, P.Get(EScatterParam::TreeCoreP)};
			}
			if (GroveAt(P, FS, X, Y, nullptr) && P.Get(EScatterParam::TreeGroveP) > Best.P)
			{
				Best = {EScatterTreeZone::Grove, P.Get(EScatterParam::TreeGroveP)};
			}
			if (Wp >= P.Get(EScatterParam::TreeEdgeW) && P.Get(EScatterParam::TreeEdgeP) > Best.P)
			{
				Best = {EScatterTreeZone::Edge, P.Get(EScatterParam::TreeEdgeP)};
			}
			return Best;
		}
	}

	EScatterTreeZone ScatterTreeZoneAt(const FScatterPalette& P, int64 XQ, int64 YQ)
	{
		const FFieldSeeds FS = MakeFieldSeeds(P.Seed);
		return TreeZone(P, FS, XQ, YQ, FieldValue(P, FS, EScatterField::WoodlandBiased, XQ, YQ)).Zone;
	}

	// ---- candidate evaluation -----------------------------------------------------------------------------------------------

	namespace
	{
		struct FGen
		{
			const FScatterSnapshot& S;
			const FScatterPalette& P;
			FFieldSeeds FS;
			int64 EQ;

			FGen(const FScatterSnapshot& InS, const FScatterPalette& InP) : S(InS), P(InP), FS(MakeFieldSeeds(InP.Seed)), EQ(static_cast<int64>(InS.HalfExtentM) << 16) {}

			int64 Par(EScatterParam Param) const { return P.Get(Param); }
			int64 Field(EScatterField F, int64 X, int64 Y) const { return FieldValue(P, FS, F, X, Y); }
		};

		struct FSite
		{
			int64 XQ = 0;
			int64 YQ = 0;
			int32 IX = 0;
			int32 IY = 0;
			uint32 H0 = 0;
			int64 Roll = 0;
			bool bTer = false;
			bool bSpl = false;
			int64 ZQ = 0;
			int32 GX = 0;
			int32 GY = 0;
			int64 G2 = 0;
			int32 W[4] = {0, 0, 0, 0};
		};

		inline void NeedTerrain(const FGen& G, FSite& Si)
		{
			if (!Si.bTer)
			{
				const FScatterCell C = ScatterLocateCell(G.S, Si.XQ, Si.YQ);
				Si.ZQ = ScatterCellSurfaceQ16(C);
				ScatterCellGradientQ16(C, Si.GX, Si.GY);
				Si.G2 = SlopeG2Q32(Si.GX, Si.GY);
				Si.bTer = true;
			}
		}

		inline void NeedSplat(const FGen& G, FSite& Si)
		{
			if (!Si.bSpl)
			{
				SplatQ8Bilinear4(G.S, Si.XQ, Si.YQ, Si.W);
				Si.bSpl = true;
			}
		}

		/** The candidate of cell (IX, IY): hash, jittered position (dropped outside the map). */
		bool MakeSite(const FGen& G, EScatterClass Class, int64 CellQ, int64 JitterQ, int32 IX, int32 IY, FSite& Si)
		{
			Si = FSite();
			Si.IX = IX;
			Si.IY = IY;
			Si.H0 = ScatterKey(G.P.Seed, ClassStream[static_cast<int32>(Class)], IX, IY, 0);
			const int64 Off = (CellQ * (65536 - JitterQ)) >> 17;
			const int64 CJ = (CellQ * JitterQ) >> 16;
			Si.XQ = static_cast<int64>(IX) * CellQ + Off + ((static_cast<int64>(Si.H0 & 0xFFFFu) * CJ) >> 16);
			Si.YQ = static_cast<int64>(IY) * CellQ + Off + ((static_cast<int64>(Si.H0 >> 16) * CJ) >> 16);
			if (FMath::Abs(Si.XQ) >= G.EQ || FMath::Abs(Si.YQ) >= G.EQ)
			{
				return false;
			}
			Si.Roll = static_cast<int64>(Mix32(Si.H0 ^ KRoll) & 0xFFFFu);
			return true;
		}

		inline int64 Sub(const FSite& Si, uint32 K)
		{
			return static_cast<int64>(Mix32(Si.H0 ^ K) & 0xFFFFu);
		}

		inline int64 Lerp16(int64 Lo, int64 Hi, int64 U16)
		{
			return Lo + (((Hi - Lo) * U16) >> 16);
		}

		/** Q16 scale value to the record's Q12. */
		inline uint16 ToQ12(int64 Q16Value)
		{
			return static_cast<uint16>(FMath::Clamp<int64>((Q16Value + 8) >> 4, 0, 65535));
		}

		inline uint16 ToU16(int64 V)
		{
			return static_cast<uint16>(FMath::Clamp<int64>(V, 0, 65535));
		}

		// Common factors of plan C scatter 3.4.
		inline int64 FactorG(const FGen& G, const FSite& Si)
		{
			return RampQ16(Si.W[0], G.Par(EScatterParam::GLo), G.Par(EScatterParam::GHi));
		}
		inline int64 FactorE3(const FGen& G, const FSite& Si)
		{
			int64 E = RampInvQ16(Si.W[1], G.Par(EScatterParam::E3DirtLo), G.Par(EScatterParam::E3DirtHi));
			if (E == 0)
			{
				return 0;
			}
			E = MulQ16(E, RampInvQ16(Si.W[2], G.Par(EScatterParam::E3RockLo), G.Par(EScatterParam::E3RockHi)));
			return MulQ16(E, RampInvQ16(Si.W[3], G.Par(EScatterParam::E3SnowLo), G.Par(EScatterParam::E3SnowHi)));
		}
		inline int64 FactorSg(const FGen& G, const FSite& Si)
		{
			return RampInvQ16(Si.G2, G.Par(EScatterParam::SgLo), G.Par(EScatterParam::SgHi));
		}
		inline int64 FactorHg(const FGen& G, const FSite& Si)
		{
			return RampInvQ16(Si.ZQ, G.Par(EScatterParam::HgLo), G.Par(EScatterParam::HgHi));
		}
		inline int64 FactorSt(const FGen& G, const FSite& Si)
		{
			return RampInvQ16(Si.G2, G.Par(EScatterParam::StLo), G.Par(EScatterParam::StHi));
		}
		inline int64 FactorHt(const FGen& G, const FSite& Si)
		{
			return RampInvQ16(Si.ZQ, G.Par(EScatterParam::HtLo), G.Par(EScatterParam::HtHi));
		}

		/** Common fields of every record; the class code sets the rest. */
		void BaseRecord(const FSite& Si, EScatterClass Class, EScatterMesh Mesh, int64 ZQ, FScatterRecord& R)
		{
			R = FScatterRecord();
			R.Class = static_cast<uint8>(Class);
			R.Mesh = static_cast<uint8>(Mesh);
			R.Stream = static_cast<uint16>(ClassStream[static_cast<int32>(Class)]);
			R.IX = Si.IX;
			R.IY = Si.IY;
			R.Slot = 0;
			R.XQ = static_cast<int32>(Si.XQ);
			R.YQ = static_cast<int32>(Si.YQ);
			R.ZQ = static_cast<int32>(ZQ);
			R.GXQ = Si.GX;
			R.GYQ = Si.GY;
			R.Yaw = static_cast<uint16>(Sub(Si, KYaw));
			R.Tilt = static_cast<uint16>(Sub(Si, KTilt));
			R.Scale = 4096;
			R.ZScale = 4096;
		}

		bool EvalGrass(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Grass, G.Par(EScatterParam::GrassCellM), G.Par(EScatterParam::GrassJitter), IX, IY, Si))
			{
				return false;
			}
			int64 P = MulQ16(G.Par(EScatterParam::GrassBaseP), G.Par(EScatterParam::Density));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedSplat(G, Si);
			P = MulQ16(P, FactorG(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			P = MulQ16(P, FactorE3(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedTerrain(G, Si);
			P = MulQ16(P, FactorSg(G, Si));
			P = MulQ16(P, FactorHg(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			const int64 M = G.Field(EScatterField::Dryness, Si.XQ, Si.YQ);
			const int64 DryMin = G.Par(EScatterParam::GrassDryMin);
			P = MulQ16(P, DryMin + MulQ16(65536 - DryMin, 65536 - M));
			if (Si.Roll >= P)
			{
				return false;
			}
			const int64 Wp = G.Field(EScatterField::WoodlandBiased, Si.XQ, Si.YQ);
			P = MulQ16(P, 65536 - MulQ16(G.Par(EScatterParam::GrassForestCut), RampQ16(Wp, G.Par(EScatterParam::GrassForestLo), G.Par(EScatterParam::GrassForestHi))));
			if (Si.Roll >= P)
			{
				return false;
			}
			const bool bT0 = Sub(Si, KRank) < G.Par(EScatterParam::GrassTierSplit);
			BaseRecord(Si, EScatterClass::Grass, bT0 ? EScatterMesh::GrassT0 : EScatterMesh::GrassT1, Si.ZQ - G.Par(EScatterParam::GrassSinkM), R);
			R.Scale = ToQ12(Lerp16(G.Par(EScatterParam::GrassScaleLo), G.Par(EScatterParam::GrassScaleHi), Sub(Si, KScale)));
			int64 ZS = Lerp16(G.Par(EScatterParam::GrassZLo), G.Par(EScatterParam::GrassZHi), Sub(Si, KScaleZ));
			// Verges (beside a path): grass keeps low.
			if (Si.W[1] < G.Par(EScatterParam::GrassVergeOwn) && SplatQ8Ring(G.S, Si.XQ, Si.YQ, G.Par(EScatterParam::GrassVergeRingM), 1) >= G.Par(EScatterParam::GrassVergeRing))
			{
				ZS = MulQ16(ZS, G.Par(EScatterParam::GrassVergeZ));
			}
			R.ZScale = ToQ12(ZS);
			R.CD0 = ToU16(M);
			R.CD1 = static_cast<uint16>(Sub(Si, KCd));
			return true;
		}

		bool EvalTussock(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Tussock, G.Par(EScatterParam::TussockCellM), G.Par(EScatterParam::TussockJitter), IX, IY, Si))
			{
				return false;
			}
			const int64 BaseIsland = FMath::Max(G.Par(EScatterParam::TussockIslandP), G.Par(EScatterParam::TussockBgP));
			const int64 Bound = FMath::Min<int64>(65536, BaseIsland + G.Par(EScatterParam::TussockForestP));
			if (Si.Roll >= Bound)
			{
				return false;
			}
			NeedSplat(G, Si);
			const int64 E3 = FactorE3(G, Si);
			if (E3 == 0)
			{
				return false;
			}
			NeedTerrain(G, Si);
			const int64 Common = MulQ16(E3, FactorSg(G, Si));
			if (Common == 0)
			{
				return false;
			}
			const int64 M = G.Field(EScatterField::Dryness, Si.XQ, Si.YQ);
			const int64 T = G.Field(EScatterField::Tussock, Si.XQ, Si.YQ);
			const int64 Wp = G.Field(EScatterField::WoodlandBiased, Si.XQ, Si.YQ);
			const int64 DryMin = G.Par(EScatterParam::TussockDryMin);
			const int64 Base = T >= G.Par(EScatterParam::TussockIslandMin) ? G.Par(EScatterParam::TussockIslandP) : G.Par(EScatterParam::TussockBgP);
			int64 Term1 = MulQ16(Base, Common);
			Term1 = MulQ16(Term1, FactorHg(G, Si));
			Term1 = MulQ16(Term1, DryMin + MulQ16(65536 - DryMin, 65536 - M));
			const int64 Term2 = MulQ16(MulQ16(G.Par(EScatterParam::TussockForestP), BandQ16(Wp, G.Par(EScatterParam::TussockForestLo), G.Par(EScatterParam::TussockForestPk), G.Par(EScatterParam::TussockForestHi))), Common);
			const int64 P = FMath::Min<int64>(65536, Term1 + Term2);
			if (Si.Roll >= P)
			{
				return false;
			}
			BaseRecord(Si, EScatterClass::Tussock, EScatterMesh::Tussock, Si.ZQ - G.Par(EScatterParam::TussockSinkM), R);
			R.Scale = ToQ12(Lerp16(G.Par(EScatterParam::TussockScaleLo), G.Par(EScatterParam::TussockScaleHi), Sub(Si, KScale)));
			R.ZScale = R.Scale;
			R.CD0 = ToU16(M);
			R.CD1 = static_cast<uint16>(Sub(Si, KCd));
			return true;
		}

		bool EvalFlower(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Flower, G.Par(EScatterParam::FlowerCellM), G.Par(EScatterParam::FlowerJitter), IX, IY, Si))
			{
				return false;
			}
			int64 P = MulQ16(G.Par(EScatterParam::FlowerBaseP), G.Par(EScatterParam::Density));
			if (Si.Roll >= P)
			{
				return false;
			}
			const int64 F = G.Field(EScatterField::Drift, Si.XQ, Si.YQ);
			P = MulQ16(P, RampQ16(F, G.Par(EScatterParam::FlowerDriftLo), G.Par(EScatterParam::FlowerDriftHi)));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedSplat(G, Si);
			P = MulQ16(P, RampQ16(Si.W[0], G.Par(EScatterParam::FlowerGLo), G.Par(EScatterParam::FlowerGHi)));
			if (Si.Roll >= P)
			{
				return false;
			}
			P = MulQ16(P, FactorE3(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedTerrain(G, Si);
			P = MulQ16(P, RampInvQ16(Si.G2, G.Par(EScatterParam::FlowerSlopeLo), G.Par(EScatterParam::FlowerSlopeHi)));
			if (Si.Roll >= P)
			{
				return false;
			}
			const int64 Wp = G.Field(EScatterField::WoodlandBiased, Si.XQ, Si.YQ);
			P = MulQ16(P, RampInvQ16(Wp, G.Par(EScatterParam::FlowerForestLo), G.Par(EScatterParam::FlowerForestHi)));
			if (Si.Roll >= P)
			{
				return false;
			}
			const int64 Fc = G.Field(EScatterField::DriftColour, Si.XQ, Si.YQ);
			int32 Colour = Fc < G.Par(EScatterParam::FlowerYellowTo) ? 0 : (Fc < G.Par(EScatterParam::FlowerWhiteTo) ? 1 : 2);
			if (Sub(Si, KColour) < G.Par(EScatterParam::FlowerNextP))
			{
				Colour = (Colour + 1) % 3;
			}
			BaseRecord(Si, EScatterClass::Flower, EScatterMesh::Flower, Si.ZQ - G.Par(EScatterParam::FlowerSinkM), R);
			R.Scale = ToQ12(Lerp16(G.Par(EScatterParam::FlowerScaleLo), G.Par(EScatterParam::FlowerScaleHi), Sub(Si, KScale)));
			R.ZScale = R.Scale;
			// Fine-grid cd0 is dryness for every class (plan C scatter 3.5), so flower stems take the same lush/dry tint as the grass beside them.
			R.CD0 = ToU16(G.Field(EScatterField::Dryness, Si.XQ, Si.YQ));
			R.CD1 = static_cast<uint16>(Colour);
			return true;
		}

		bool EvalNearCard(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::NearCard, G.Par(EScatterParam::NearCardCellM), G.Par(EScatterParam::NearCardJitter), IX, IY, Si))
			{
				return false;
			}
			int64 P = G.Par(EScatterParam::NearCardBaseP);
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedSplat(G, Si);
			P = MulQ16(P, FactorG(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			P = MulQ16(P, FactorE3(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedTerrain(G, Si);
			P = MulQ16(P, FactorSg(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			BaseRecord(Si, EScatterClass::NearCard, EScatterMesh::NearCard, Si.ZQ - G.Par(EScatterParam::NearCardSinkM), R);
			R.Scale = ToQ12(Lerp16(G.Par(EScatterParam::NearCardScaleLo), G.Par(EScatterParam::NearCardScaleHi), Sub(Si, KScale)));
			R.ZScale = R.Scale;
			R.CD0 = ToU16(G.Field(EScatterField::Dryness, Si.XQ, Si.YQ));
			R.CD1 = static_cast<uint16>(Sub(Si, KCd));
			return true;
		}

		/** The trunk rules shared by trees and saplings: trunk wD and wR, and four probes at 4 m that keep trees off paths. */
		bool TrunkOk(const FGen& G, FSite& Si)
		{
			NeedSplat(G, Si);
			if (Si.W[1] > G.Par(EScatterParam::TrunkDirt) || Si.W[2] > G.Par(EScatterParam::TrunkRock))
			{
				return false;
			}
			const int64 D = G.Par(EScatterParam::TreeProbeM);
			const int64 Off[4][2] = {{D, 0}, {-D, 0}, {0, D}, {0, -D}};
			for (int32 K = 0; K < 4; ++K)
			{
				if (SplatQ8Bilinear(G.S, Si.XQ + Off[K][0], Si.YQ + Off[K][1], 1) > G.Par(EScatterParam::TreeProbeDirt))
				{
					return false;
				}
			}
			return true;
		}

		/** Lowest of the surface at the centre and 4 probes at the base probe distance. */
		int64 TreeBaseZ(const FGen& G, const FSite& Si)
		{
			const int64 D = G.Par(EScatterParam::TreeBaseProbeM);
			int64 Z = Si.ZQ;
			Z = FMath::Min(Z, SurfaceQ16(G.S, Si.XQ + D, Si.YQ));
			Z = FMath::Min(Z, SurfaceQ16(G.S, Si.XQ - D, Si.YQ));
			Z = FMath::Min(Z, SurfaceQ16(G.S, Si.XQ, Si.YQ + D));
			Z = FMath::Min(Z, SurfaceQ16(G.S, Si.XQ, Si.YQ - D));
			return Z;
		}

		bool EvalTree(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Tree, G.Par(EScatterParam::TreeCellM), G.Par(EScatterParam::TreeJitter), IX, IY, Si))
			{
				return false;
			}
			const int64 Bound = FMath::Max(FMath::Max(G.Par(EScatterParam::TreeCoreP), G.Par(EScatterParam::TreeGroveP)), FMath::Max(G.Par(EScatterParam::TreeEdgeP), G.Par(EScatterParam::TreeLoneP)));
			if (Si.Roll >= Bound)
			{
				return false;
			}
			const int64 Wp = G.Field(EScatterField::WoodlandBiased, Si.XQ, Si.YQ);
			const FZone Zone = TreeZone(G.P, G.FS, Si.XQ, Si.YQ, Wp);
			int64 P = Zone.P;
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedTerrain(G, Si);
			P = MulQ16(MulQ16(P, FactorSt(G, Si)), FactorHt(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			if (!TrunkOk(G, Si))
			{
				return false;
			}
			bool bConifer = false;
			if (Zone.Zone != EScatterTreeZone::Lone)
			{
				const int64 C = G.Field(EScatterField::Stand, Si.XQ, Si.YQ);
				const int64 CP = RampQ16(C, G.Par(EScatterParam::TreeConiferCLo), G.Par(EScatterParam::TreeConiferCHi))
					+ MulQ16(G.Par(EScatterParam::TreeConiferZWeight), RampQ16(Si.ZQ, G.Par(EScatterParam::TreeConiferZLo), G.Par(EScatterParam::TreeConiferZHi)));
				bConifer = Sub(Si, KType) < CP;
			}
			if (bConifer)
			{
				if (Si.W[3] > G.Par(EScatterParam::TreeConiferSnow))
				{
					return false;
				}
				if (Si.W[3] >= G.Par(EScatterParam::TreeConiferHalfSnow))
				{
					P >>= 1;
				}
			}
			else if (Si.W[3] > G.Par(EScatterParam::TreeBroadSnow))
			{
				return false;
			}
			if (Si.Roll >= P)
			{
				return false;
			}
			const int32 Var = static_cast<int32>(Sub(Si, KVar) & 1);
			const EScatterMesh Mesh = bConifer ? (Var ? EScatterMesh::TreeConiferB : EScatterMesh::TreeConiferA) : (Var ? EScatterMesh::TreeBroadB : EScatterMesh::TreeBroadA);
			BaseRecord(Si, EScatterClass::Tree, Mesh, TreeBaseZ(G, Si) - G.Par(EScatterParam::TreeSinkM), R);
			int64 Scale = bConifer ? Lerp16(G.Par(EScatterParam::TreeConiferScaleLo), G.Par(EScatterParam::TreeConiferScaleHi), Sub(Si, KScale))
								   : Lerp16(G.Par(EScatterParam::TreeBroadScaleLo), G.Par(EScatterParam::TreeBroadScaleHi), Sub(Si, KScale));
			if (Zone.Zone == EScatterTreeZone::Lone)
			{
				Scale = MulQ16(Scale, G.Par(EScatterParam::TreeLoneScale));
			}
			R.Scale = ToQ12(Scale);
			R.ZScale = R.Scale;
			R.CD0 = static_cast<uint16>(Sub(Si, KCd));
			R.CD1 = ToU16(G.Field(EScatterField::Stand, Si.XQ, Si.YQ));
			return true;
		}

		bool EvalSapling(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Sapling, G.Par(EScatterParam::SaplingCellM), G.Par(EScatterParam::SaplingJitter), IX, IY, Si))
			{
				return false;
			}
			const int64 Bound = G.Par(EScatterParam::SaplingP);
			if (Si.Roll >= Bound)
			{
				return false;
			}
			const int64 Wp = G.Field(EScatterField::WoodlandBiased, Si.XQ, Si.YQ);
			bool bFringe = false;
			GroveAt(G.P, G.FS, Si.XQ, Si.YQ, &bFringe);
			int64 Term = BandQ16(Wp, G.Par(EScatterParam::SaplingLo), G.Par(EScatterParam::SaplingPk), G.Par(EScatterParam::SaplingHi));
			if (bFringe)
			{
				Term += G.Par(EScatterParam::SaplingFringeP);
			}
			int64 P = MulQ16(Bound, FMath::Min<int64>(65536, Term));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedTerrain(G, Si);
			P = MulQ16(MulQ16(P, FactorSt(G, Si)), FactorHt(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			if (!TrunkOk(G, Si))
			{
				return false;
			}
			if (Si.W[3] > G.Par(EScatterParam::TreeBroadSnow))
			{
				return false;
			}
			BaseRecord(Si, EScatterClass::Sapling, EScatterMesh::TreeBroadA, TreeBaseZ(G, Si) - G.Par(EScatterParam::TreeSinkM), R);
			R.Scale = ToQ12(Lerp16(G.Par(EScatterParam::SaplingScaleLo), G.Par(EScatterParam::SaplingScaleHi), Sub(Si, KScale)));
			R.ZScale = R.Scale;
			R.CD0 = static_cast<uint16>(Sub(Si, KCd));
			R.CD1 = ToU16(G.Field(EScatterField::Stand, Si.XQ, Si.YQ));
			return true;
		}

		bool EvalShrub(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Shrub, G.Par(EScatterParam::ShrubCellM), G.Par(EScatterParam::ShrubJitter), IX, IY, Si))
			{
				return false;
			}
			const int64 Bound = FMath::Min<int64>(65536, G.Par(EScatterParam::ShrubWoodP) + G.Par(EScatterParam::ShrubFsP) + G.Par(EScatterParam::ShrubHedgeP));
			if (Si.Roll >= Bound)
			{
				return false;
			}
			NeedSplat(G, Si);
			int64 Cut = MulQ16(RampInvQ16(Si.W[2], G.Par(EScatterParam::ShrubRockLo), G.Par(EScatterParam::ShrubRockHi)),
				RampInvQ16(Si.W[3], G.Par(EScatterParam::ShrubSnowLo), G.Par(EScatterParam::ShrubSnowHi)));
			if (Cut == 0)
			{
				return false;
			}
			NeedTerrain(G, Si);
			Cut = MulQ16(Cut, FactorSg(G, Si));
			Cut = MulQ16(Cut, RampInvQ16(Si.ZQ, G.Par(EScatterParam::ShrubZLo), G.Par(EScatterParam::ShrubZHi)));
			if (Cut == 0)
			{
				return false;
			}
			const int64 Wp = G.Field(EScatterField::WoodlandBiased, Si.XQ, Si.YQ);
			const int64 Fs = G.Field(EScatterField::ShrubClump, Si.XQ, Si.YQ);
			const int64 Fh = G.Field(EScatterField::Hedge, Si.XQ, Si.YQ);
			int64 T1 = MulQ16(G.Par(EScatterParam::ShrubWoodP), BandQ16(Wp, G.Par(EScatterParam::ShrubWoodLo), G.Par(EScatterParam::ShrubWoodPk), G.Par(EScatterParam::ShrubWoodHi)));
			int64 T2 = MulQ16(MulQ16(G.Par(EScatterParam::ShrubFsP), RampQ16(Fs, G.Par(EScatterParam::ShrubFsLo), G.Par(EScatterParam::ShrubFsHi))), MulQ16(FactorG(G, Si), FactorE3(G, Si)));
			int64 T3 = 0;
			const int64 HedgeFh = RampQ16(Fh, G.Par(EScatterParam::ShrubFhLo), G.Par(EScatterParam::ShrubFhHi));
			const int64 HedgeOwn = RampInvQ16(Si.W[1], G.Par(EScatterParam::ShrubHedgeOwnLo), G.Par(EScatterParam::ShrubHedgeOwnHi));
			if (HedgeFh > 0 && HedgeOwn > 0)
			{
				const int32 Ring = SplatQ8Ring(G.S, Si.XQ, Si.YQ, G.Par(EScatterParam::ShrubHedgeRingM), 1);
				T3 = MulQ16(MulQ16(G.Par(EScatterParam::ShrubHedgeP), BandQ16(Ring, G.Par(EScatterParam::ShrubHedgeLo), G.Par(EScatterParam::ShrubHedgePk), G.Par(EScatterParam::ShrubHedgeHi))), MulQ16(HedgeOwn, HedgeFh));
			}
			const int64 P = MulQ16(FMath::Min<int64>(65536, T1 + T2 + T3), Cut);
			if (Si.Roll >= P)
			{
				return false;
			}
			const EScatterMesh Mesh = (Sub(Si, KVar) & 1) ? EScatterMesh::ShrubB : EScatterMesh::ShrubA;
			BaseRecord(Si, EScatterClass::Shrub, Mesh, Si.ZQ - G.Par(EScatterParam::ShrubSinkM), R);
			R.Scale = ToQ12(Lerp16(G.Par(EScatterParam::ShrubScaleLo), G.Par(EScatterParam::ShrubScaleHi), Sub(Si, KScale)));
			R.ZScale = R.Scale;
			R.CD0 = static_cast<uint16>(Sub(Si, KCd));
			R.CD1 = ToU16(G.Field(EScatterField::Stand, Si.XQ, Si.YQ));
			return true;
		}

		bool EvalFern(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Fern, G.Par(EScatterParam::FernCellM), G.Par(EScatterParam::FernJitter), IX, IY, Si))
			{
				return false;
			}
			int64 P = G.Par(EScatterParam::FernP);
			if (Si.Roll >= P)
			{
				return false;
			}
			const int64 Wp = G.Field(EScatterField::WoodlandBiased, Si.XQ, Si.YQ);
			P = MulQ16(P, RampQ16(Wp, G.Par(EScatterParam::FernWLo), G.Par(EScatterParam::FernWHi)));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedSplat(G, Si);
			P = MulQ16(P, FactorE3(G, Si));
			if (Si.Roll >= P)
			{
				return false;
			}
			NeedTerrain(G, Si);
			P = MulQ16(P, RampInvQ16(Si.G2, G.Par(EScatterParam::FernSlopeLo), G.Par(EScatterParam::FernSlopeHi)));
			if (Si.Roll >= P)
			{
				return false;
			}
			BaseRecord(Si, EScatterClass::Fern, EScatterMesh::Fern, Si.ZQ - G.Par(EScatterParam::FernSinkM), R);
			R.Scale = ToQ12(Lerp16(G.Par(EScatterParam::FernScaleLo), G.Par(EScatterParam::FernScaleHi), Sub(Si, KScale)));
			R.ZScale = R.Scale;
			R.CD0 = static_cast<uint16>(Sub(Si, KCd));
			R.CD1 = ToU16(G.Field(EScatterField::Stand, Si.XQ, Si.YQ));
			return true;
		}

		bool EvalRock(const FGen& G, int32 IX, int32 IY, FScatterRecord& R)
		{
			FSite Si;
			if (!MakeSite(G, EScatterClass::Rock, G.Par(EScatterParam::RockCellM), G.Par(EScatterParam::RockJitter), IX, IY, Si))
			{
				return false;
			}
			const int64 Bound = FMath::Min<int64>(65536, G.Par(EScatterParam::RockPaintP) + G.Par(EScatterParam::RockSlopeP) + G.Par(EScatterParam::RockBoulderP));
			if (Si.Roll >= Bound)
			{
				return false;
			}
			NeedSplat(G, Si);
			NeedTerrain(G, Si);
			const int64 T1 = MulQ16(MulQ16(G.Par(EScatterParam::RockPaintP), BandQ16(Si.W[2], G.Par(EScatterParam::RockBandLo), G.Par(EScatterParam::RockBandPk), G.Par(EScatterParam::RockBandHi))),
				RampInvQ16(Si.W[3], G.Par(EScatterParam::RockSnowLo), G.Par(EScatterParam::RockSnowHi)));
			const int64 T2 = MulQ16(G.Par(EScatterParam::RockSlopeP), RampQ16(Si.G2, G.Par(EScatterParam::RockSlopeLo), G.Par(EScatterParam::RockSlopeHi)));
			const int64 T3 = MulQ16(G.Par(EScatterParam::RockBoulderP), RampQ16(G.Field(EScatterField::Boulder, Si.XQ, Si.YQ), G.Par(EScatterParam::RockFrLo), G.Par(EScatterParam::RockFrHi)));
			const int64 P = FMath::Min<int64>(65536, T1 + T2 + T3);
			if (Si.Roll >= P)
			{
				return false;
			}
			// A tree in the same cell wins: rocks never stand where the tree candidate of the cell is accepted.
			FScatterRecord Tree;
			if (EvalTree(G, IX, IY, Tree))
			{
				return false;
			}
			const int64 U = Sub(Si, KScale);
			const int64 U3 = (((U * U) >> 16) * U) >> 16;
			const int64 Scale = Lerp16(G.Par(EScatterParam::RockScaleLo), G.Par(EScatterParam::RockScaleHi), U3);
			const int64 Sink = MulQ16(MulQ16(G.Par(EScatterParam::RockSinkFrac), G.Par(EScatterParam::RockNominalHM)), static_cast<int64>(ToQ12(Scale)) << 4);
			const EScatterMesh Mesh = (Sub(Si, KVar) & 1) ? EScatterMesh::RockB : EScatterMesh::RockA;
			BaseRecord(Si, EScatterClass::Rock, Mesh, Si.ZQ - Sink, R);
			R.Scale = ToQ12(Scale);
			R.ZScale = R.Scale;
			R.CD0 = static_cast<uint16>(Sub(Si, KCd));
			R.CD1 = ToU16(G.Field(EScatterField::Stand, Si.XQ, Si.YQ));
			return true;
		}

		int64 ClassCellQ16(const FScatterPalette& P, EScatterClass Class)
		{
			switch (Class)
			{
			case EScatterClass::Grass: return P.Get(EScatterParam::GrassCellM);
			case EScatterClass::Tussock: return P.Get(EScatterParam::TussockCellM);
			case EScatterClass::Flower: return P.Get(EScatterParam::FlowerCellM);
			case EScatterClass::NearCard: return P.Get(EScatterParam::NearCardCellM);
			case EScatterClass::Tree: return P.Get(EScatterParam::TreeCellM);
			case EScatterClass::Sapling: return P.Get(EScatterParam::SaplingCellM);
			case EScatterClass::Shrub: return P.Get(EScatterParam::ShrubCellM);
			case EScatterClass::Fern: return P.Get(EScatterParam::FernCellM);
			default: return P.Get(EScatterParam::RockCellM);
			}
		}

		bool EvalClass(const FGen& G, EScatterClass Class, int32 IX, int32 IY, FScatterRecord& R)
		{
			switch (Class)
			{
			case EScatterClass::Grass: return EvalGrass(G, IX, IY, R);
			case EScatterClass::Tussock: return EvalTussock(G, IX, IY, R);
			case EScatterClass::Flower: return EvalFlower(G, IX, IY, R);
			case EScatterClass::NearCard: return EvalNearCard(G, IX, IY, R);
			case EScatterClass::Tree: return EvalTree(G, IX, IY, R);
			case EScatterClass::Sapling: return EvalSapling(G, IX, IY, R);
			case EScatterClass::Shrub: return EvalShrub(G, IX, IY, R);
			case EScatterClass::Fern: return EvalFern(G, IX, IY, R);
			default: return EvalRock(G, IX, IY, R);
			}
		}
	}

	void EvaluateTile(const FScatterSnapshot& S, const FScatterPalette& P, const FScatterTileKey& Tile, FScatterTileRecords& Out)
	{
		for (int32 M = 0; M < ScatterMeshCount; ++M)
		{
			Out.PerMesh[M].Reset();
		}
		const FGen G(S, P);
		const int64 TileQ = static_cast<int64>(Tile.SizeM) << 16;
		const int64 X0 = static_cast<int64>(Tile.TX) * TileQ;
		const int64 Y0 = static_cast<int64>(Tile.TY) * TileQ;
		for (int32 C = 0; C < ScatterClassCount; ++C)
		{
			const EScatterClass Class = static_cast<EScatterClass>(C);
			if (ScatterGridOfClass(Class) != Tile.Grid)
			{
				continue;
			}
			if (Class == EScatterClass::NearCard && P.Level != EScatterLevel::L1)
			{
				continue;
			}
			const int64 Cell = ClassCellQ16(P, Class);
			const int64 IX0 = FMath::Max(FloorDiv(X0, Cell), FloorDiv(-G.EQ, Cell));
			const int64 IY0 = FMath::Max(FloorDiv(Y0, Cell), FloorDiv(-G.EQ, Cell));
			const int64 IX1 = FMath::Min(FloorDiv(X0 + TileQ - 1, Cell), FloorDiv(G.EQ - 1, Cell));
			const int64 IY1 = FMath::Min(FloorDiv(Y0 + TileQ - 1, Cell), FloorDiv(G.EQ - 1, Cell));
			for (int64 IY = IY0; IY <= IY1; ++IY)
			{
				for (int64 IX = IX0; IX <= IX1; ++IX)
				{
					FScatterRecord R;
					if (EvalClass(G, Class, static_cast<int32>(IX), static_cast<int32>(IY), R))
					{
						Out.PerMesh[R.Mesh].Add(R);
					}
				}
			}
		}
		for (int32 M = 0; M < ScatterMeshCount; ++M)
		{
			TArray<FScatterRecord>& A = Out.PerMesh[M];
			std::sort(A.GetData(), A.GetData() + A.Num(), [](const FScatterRecord& L, const FScatterRecord& Rr) { return L.Key() < Rr.Key(); });
		}
	}

	int64 ScatterRecordZRuleQ16(const FScatterSnapshot& S, const FScatterPalette& P, const FScatterRecord& R)
	{
		const int64 Surf = SurfaceQ16(S, R.XQ, R.YQ);
		switch (static_cast<EScatterClass>(R.Class))
		{
		case EScatterClass::Grass: return Surf - P.Get(EScatterParam::GrassSinkM);
		case EScatterClass::Tussock: return Surf - P.Get(EScatterParam::TussockSinkM);
		case EScatterClass::Flower: return Surf - P.Get(EScatterParam::FlowerSinkM);
		case EScatterClass::NearCard: return Surf - P.Get(EScatterParam::NearCardSinkM);
		case EScatterClass::Tree:
		case EScatterClass::Sapling:
		{
			const int64 D = P.Get(EScatterParam::TreeBaseProbeM);
			int64 Z = Surf;
			Z = FMath::Min(Z, SurfaceQ16(S, static_cast<int64>(R.XQ) + D, R.YQ));
			Z = FMath::Min(Z, SurfaceQ16(S, static_cast<int64>(R.XQ) - D, R.YQ));
			Z = FMath::Min(Z, SurfaceQ16(S, R.XQ, static_cast<int64>(R.YQ) + D));
			Z = FMath::Min(Z, SurfaceQ16(S, R.XQ, static_cast<int64>(R.YQ) - D));
			return Z - P.Get(EScatterParam::TreeSinkM);
		}
		case EScatterClass::Shrub: return Surf - P.Get(EScatterParam::ShrubSinkM);
		case EScatterClass::Fern: return Surf - P.Get(EScatterParam::FernSinkM);
		default:
			return Surf - MulQ16(MulQ16(P.Get(EScatterParam::RockSinkFrac), P.Get(EScatterParam::RockNominalHM)), static_cast<int64>(R.Scale) << 4);
		}
	}

	// SCATTER_FP_BEGIN: BuildInstance is the only record -> transform and custom-data function; doubles live here and nowhere else in the generator.
	namespace
	{
		double ClassAlign(const FScatterPalette& P, EScatterClass C)
		{
			int64 A = 0;
			switch (C)
			{
			case EScatterClass::Grass: A = P.Get(EScatterParam::GrassAlign); break;
			case EScatterClass::Tussock: A = P.Get(EScatterParam::TussockAlign); break;
			case EScatterClass::Flower: A = P.Get(EScatterParam::FlowerAlign); break;
			case EScatterClass::NearCard: A = P.Get(EScatterParam::NearCardAlign); break;
			case EScatterClass::Shrub: A = P.Get(EScatterParam::ShrubAlign); break;
			case EScatterClass::Fern: A = P.Get(EScatterParam::FernAlign); break;
			case EScatterClass::Rock: A = P.Get(EScatterParam::RockAlign); break;
			default: A = 0; break;
			}
			return static_cast<double>(A) / 65536.0;
		}

		void Normalize3(double V[3])
		{
			const double L = std::sqrt(V[0] * V[0] + V[1] * V[1] + V[2] * V[2]);
			if (L > 1e-12)
			{
				V[0] /= L;
				V[1] /= L;
				V[2] /= L;
			}
		}
	}

	FScatterInstance BuildInstance(const FScatterRecord& R, const FScatterPalette& P)
	{
		FScatterInstance I;
		I.Pos[0] = static_cast<double>(R.XQ) / 65536.0;
		I.Pos[1] = static_cast<double>(R.YQ) / 65536.0;
		I.Pos[2] = static_cast<double>(R.ZQ) / 65536.0;
		const double SXY = static_cast<double>(R.Scale) / 4096.0;
		const double SZ = static_cast<double>(R.ZScale) / 4096.0;
		I.Scale[0] = SXY;
		I.Scale[1] = SXY;
		I.Scale[2] = SZ;

		const EScatterClass Class = static_cast<EScatterClass>(R.Class);
		const double GX = static_cast<double>(R.GXQ) / 65536.0;
		const double GY = static_cast<double>(R.GYQ) / 65536.0;
		const double NL = std::sqrt(1.0 + GX * GX + GY * GY);
		const double N[3] = {-GX / NL, -GY / NL, 1.0 / NL};
		const double A = ClassAlign(P, Class);
		double Up[3] = {N[0] * A, N[1] * A, N[2] * A + (1.0 - A)};
		Normalize3(Up);
		// Hashed tilt (rocks only): a small lean in a hashed direction.
		if (Class == EScatterClass::Rock)
		{
			const double MaxDeg = static_cast<double>(P.Get(EScatterParam::RockTiltDeg)) / 65536.0;
			const double Ang = (static_cast<double>(R.Tilt) / 65536.0) * MaxDeg * 3.14159265358979323846 / 180.0;
			const double Dir = 2.0 * 3.14159265358979323846 * (static_cast<double>((static_cast<uint32>(R.Tilt) * 3u + R.Yaw) & 0xFFFFu) / 65536.0);
			// The tilt frame: world X projected onto the plane perpendicular to Up (world Y if Up were within 1e-3 of X, which no rock slope reaches).
			// Well conditioned near vertical, so a one-unit gradient change on near-flat ground moves the lean by about that much, never by a swing of
			// its direction (the earlier frame (-Up.y, Up.x, 0) turned with the gradient's direction there). Records and hashes do not depend on it.
			double E1[3] = {1.0 - Up[0] * Up[0], -Up[0] * Up[1], -Up[0] * Up[2]};
			if (E1[0] * E1[0] + E1[1] * E1[1] + E1[2] * E1[2] < 1e-6)
			{
				E1[0] = -Up[1] * Up[0];
				E1[1] = 1.0 - Up[1] * Up[1];
				E1[2] = -Up[1] * Up[2];
			}
			Normalize3(E1);
			const double E2[3] = {Up[1] * E1[2] - Up[2] * E1[1], Up[2] * E1[0] - Up[0] * E1[2], Up[0] * E1[1] - Up[1] * E1[0]};
			const double T = std::tan(Ang);
			const double C = std::cos(Dir) * T;
			const double S = std::sin(Dir) * T;
			Up[0] += C * E1[0] + S * E2[0];
			Up[1] += C * E1[1] + S * E2[1];
			Up[2] += C * E1[2] + S * E2[2];
			Normalize3(Up);
		}
		I.Up[0] = Up[0];
		I.Up[1] = Up[1];
		I.Up[2] = Up[2];

		// Rotation: yaw about Z first, then the shortest arc from Z to the up axis (q = align * yaw).
		const double Yaw = 2.0 * 3.14159265358979323846 * (static_cast<double>(R.Yaw) / 65536.0);
		const double YQz = std::sin(Yaw * 0.5);
		const double YQw = std::cos(Yaw * 0.5);
		double AQ[4] = {-Up[1], Up[0], 0.0, 1.0 + Up[2]};
		const double AL = std::sqrt(AQ[0] * AQ[0] + AQ[1] * AQ[1] + AQ[2] * AQ[2] + AQ[3] * AQ[3]);
		for (double& V : AQ)
		{
			V /= AL;
		}
		// a (x) b with b = (0, 0, YQz, YQw).
		I.Quat[0] = AQ[3] * 0.0 + AQ[0] * YQw + AQ[1] * YQz - AQ[2] * 0.0;
		I.Quat[1] = AQ[3] * 0.0 - AQ[0] * YQz + AQ[1] * YQw + AQ[2] * 0.0;
		I.Quat[2] = AQ[3] * YQz + AQ[0] * 0.0 - AQ[1] * 0.0 + AQ[2] * YQw;
		I.Quat[3] = AQ[3] * YQw - AQ[0] * 0.0 - AQ[1] * 0.0 - AQ[2] * YQz;

		if (ScatterGridOfClass(Class) == EScatterGrid::Fine)
		{
			I.NumCustom = 4;
			I.Custom[0] = static_cast<float>(static_cast<double>(R.CD0) / 65535.0);
			I.Custom[1] = (Class == EScatterClass::Flower) ? static_cast<float>(R.CD1) : static_cast<float>(static_cast<double>(R.CD1) / 65535.0);
			I.Custom[2] = static_cast<float>(N[0]);
			I.Custom[3] = static_cast<float>(N[1]);
		}
		else
		{
			I.NumCustom = 2;
			I.Custom[0] = static_cast<float>(static_cast<double>(R.CD0) / 65535.0);
			I.Custom[1] = static_cast<float>(static_cast<double>(R.CD1) / 65535.0);
		}
		return I;
	}
	// SCATTER_FP_END

	// ---- reference ----------------------------------------------------------------------------------------------------------

	FScatterHash ReferenceHash(const FTerrainHeightfield& HF, const FScatterPalette& P, int32 FineTileM, int32 CoarseTileM, bool bParallel)
	{
		FScatterHash Total;
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			const EScatterGrid Grid = static_cast<EScatterGrid>(G);
			const FScatterTileGrid TG = MakeScatterTileGrid(HF.HalfExtentM(), Grid == EScatterGrid::Fine ? FineTileM : CoarseTileM);
			TArray<FScatterHash> PerTile;
			PerTile.SetNum(TG.NumTiles());
			auto Work = [&](int32 Index)
			{
				const FScatterTileKey Key = ScatterTileKeyAt(Grid, TG, Index);
				const FScatterSnapshot S = MakeSnapshot(HF, Key);
				FScatterTileRecords Recs;
				EvaluateTile(S, P, Key, Recs);
				FScatterHash H;
				for (int32 M = 0; M < ScatterMeshCount; ++M)
				{
					for (const FScatterRecord& R : Recs.PerMesh[M])
					{
						ScatterFoldRecord(H, R);
					}
				}
				PerTile[Index] = H;
			};
			if (bParallel)
			{
				ParallelFor(TG.NumTiles(), Work);
			}
			else
			{
				for (int32 I = 0; I < TG.NumTiles(); ++I)
				{
					Work(I);
				}
			}
			for (const FScatterHash& H : PerTile)
			{
				ScatterCombine(Total, H);
			}
		}
		return Total;
	}

	void ReferenceRecords(const FTerrainHeightfield& HF, const FScatterPalette& P, int32 FineTileM, int32 CoarseTileM, TArray<FScatterRecord>& Out)
	{
		Out.Reset();
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			const EScatterGrid Grid = static_cast<EScatterGrid>(G);
			const FScatterTileGrid TG = MakeScatterTileGrid(HF.HalfExtentM(), Grid == EScatterGrid::Fine ? FineTileM : CoarseTileM);
			for (int32 I = 0; I < TG.NumTiles(); ++I)
			{
				const FScatterTileKey Key = ScatterTileKeyAt(Grid, TG, I);
				const FScatterSnapshot S = MakeSnapshot(HF, Key);
				FScatterTileRecords Recs;
				EvaluateTile(S, P, Key, Recs);
				for (int32 M = 0; M < ScatterMeshCount; ++M)
				{
					Out.Append(Recs.PerMesh[M]);
				}
			}
		}
		std::sort(Out.GetData(), Out.GetData() + Out.Num(), [](const FScatterRecord& L, const FScatterRecord& R) { return L.Key() < R.Key(); });
	}

	// ---- edit script --------------------------------------------------------------------------------------------------------

	void ApplyScatterEditScript(TArray<FScatterRecord>& Items, const FScatterEditScript& Script)
	{
		for (const FScatterUpdate& U : Script.Updates)
		{
			Items[U.Index] = U.Record;
		}
		for (const int32 Index : Script.Removes)
		{
			// Remove-at-swap: the last element takes the slot (it is a kept element with a higher index: removals run descending).
			Items[Index] = Items.Last();
			Items.Pop(EAllowShrinking::No);
		}
		for (const FScatterRecord& A : Script.Appends)
		{
			Items.Add(A);
		}
	}

	FScatterEditScript BuildEditScript(const FScatterUnitState& Old, const TArray<FScatterRecord>& NewSorted, TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe>& OutNext)
	{
		FScatterEditScript Script;
		int32 I = 0;
		int32 J = 0;
		while (I < Old.Sorted.Num() || J < NewSorted.Num())
		{
			const uint64 OK = I < Old.Sorted.Num() ? Old.Sorted[I].Key : ~0ull;
			const uint64 NK = J < NewSorted.Num() ? NewSorted[J].Key() : ~0ull;
			if (I < Old.Sorted.Num() && J < NewSorted.Num() && OK == NK)
			{
				const int32 OldIndex = Old.Sorted[I].Index;
				if (Old.Records[OldIndex] != NewSorted[J])
				{
					FScatterUpdate U;
					U.Index = OldIndex;
					U.Record = NewSorted[J];
					Script.Updates.Add(U);
				}
				++I;
				++J;
			}
			else if (J >= NewSorted.Num() || (I < Old.Sorted.Num() && OK < NK))
			{
				Script.Removes.Add(Old.Sorted[I].Index);
				++I;
			}
			else
			{
				Script.Appends.Add(NewSorted[J]);
				++J;
			}
		}
		std::sort(Script.Removes.GetData(), Script.Removes.GetData() + Script.Removes.Num(), [](int32 A, int32 B) { return A > B; });
		std::sort(Script.Updates.GetData(), Script.Updates.GetData() + Script.Updates.Num(), [](const FScatterUpdate& A, const FScatterUpdate& B) { return A.Index < B.Index; });

		TSharedPtr<FScatterUnitState, ESPMode::ThreadSafe> Next = MakeShared<FScatterUnitState, ESPMode::ThreadSafe>();
		Next->Records = Old.Records;
		ApplyScatterEditScript(Next->Records, Script);
		Next->RebuildSorted();
		OutNext = Next;
		return Script;
	}
}

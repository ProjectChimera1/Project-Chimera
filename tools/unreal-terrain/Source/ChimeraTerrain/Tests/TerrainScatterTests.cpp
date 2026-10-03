// Project Chimera terrain trial (plan C scatter 3.1, 4 S2). Original Chimera code.
// Automation tests of the scatter generator (Chimera.Terrain.Scatter.*): pure data, no rendering, so they run under -nullrhi. They cover the integer
// math (goldens against the Python reference), the generator's invariances (tile size, chunk size, map extent, thread count), incremental =
// full regeneration, undo/redo, the keyed edit script, the scheduler (fake executor and clock), the placement rules and the text rules.
// Run: Tools/run_tests.ps1 -Filter Chimera.Terrain.Scatter
// The same file also compiles against a tiny stand-in for the engine headers (CHIMERA_SCATTER_STANDALONE): Tools/scatter_harness/build_and_run.bat
// builds the pure tests with plain cl.exe and runs them in seconds (the engine-only tests at the end are skipped there).

#include "CoreMinimal.h"

#if WITH_DEV_AUTOMATION_TESTS || defined(CHIMERA_SCATTER_STANDALONE)

#if !defined(CHIMERA_SCATTER_STANDALONE)
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/CString.h"
#include "Engine/World.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "UObject/Package.h"
#endif

#include "Data/TerrainHeightfield.h"
#include "Data/TerrainBrush.h"
#include "Data/TerrainUndo.h"
#include "Data/TerrainScatter.h"
#include "Data/TerrainScatterMath.h"
#include "Data/TerrainScatterPalette.h"
#include "Data/TerrainLookShared.h"

#include <algorithm>
#include <cmath>

using namespace ChimeraTerrain;

#define SC_FLAGS (EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)
#define SC_TEST(Name) \
	IMPLEMENT_SIMPLE_AUTOMATION_TEST(FChimeraTerrainScatter##Name##Test, "Chimera.Terrain.Scatter." #Name, SC_FLAGS) \
	bool FChimeraTerrainScatter##Name##Test::RunTest(const FString& Parameters)

namespace
{
	// ---- portability helpers --------------------------------------------------------------------------------------------------

	/** Deterministic test random numbers (no engine random: the tests themselves must repeat). */
	struct FScRng
	{
		uint32 S;
		explicit FScRng(uint32 Seed) : S(Seed) {}
		uint32 Next()
		{
			S = Mix32(S + 0x9E3779B9u);
			return S;
		}
		/** Inclusive range. */
		int32 Range(int32 Lo, int32 Hi) { return Lo + static_cast<int32>(Next() % static_cast<uint32>(Hi - Lo + 1)); }
		float Frac() { return static_cast<float>(Next() >> 8) / 16777216.0f; }
		float Between(float Lo, float Hi) { return Lo + (Hi - Lo) * Frac(); }
	};

	bool ScLoadText(const FString& Relative, FString& Out)
	{
		return FFileHelper::LoadFileToString(Out, *(FPaths::ProjectDir() + Relative));
	}

	/** All integers inside the first bracketed array that follows Marker (nested arrays included), in order. */
	void ScParseInts(const FString& Text, const TCHAR* Marker, TArray<int64>& Out)
	{
		Out.Reset();
		const int32 Start = Text.Find(Marker);
		if (Start == INDEX_NONE)
		{
			return;
		}
		int32 I = Start;
		const int32 Len = Text.Len();
		while (I < Len && Text[I] != TEXT('['))
		{
			++I;
		}
		int32 Depth = 0;
		for (; I < Len; ++I)
		{
			const TCHAR C = Text[I];
			if (C == TEXT('['))
			{
				++Depth;
			}
			else if (C == TEXT(']'))
			{
				--Depth;
				if (Depth == 0)
				{
					break;
				}
			}
			else if (C == TEXT('-') || (C >= TEXT('0') && C <= TEXT('9')))
			{
				bool bNeg = false;
				if (C == TEXT('-'))
				{
					bNeg = true;
					++I;
				}
				int64 V = 0;
				while (I < Len && Text[I] >= TEXT('0') && Text[I] <= TEXT('9'))
				{
					V = V * 10 + static_cast<int64>(Text[I] - TEXT('0'));
					++I;
				}
				--I;
				Out.Add(bNeg ? -V : V);
			}
		}
	}

	/** A deterministic height function of world metres (so two maps of different extent agree where they overlap). */
	float ScHeightAt(float WX, float WY, int32 Variant)
	{
		const float V = static_cast<float>(Variant);
		return 18.0f * std::sin(WX * 0.045f + V) * std::cos(WY * 0.037f - V) + 4.0f * std::sin(WX * 0.2f + WY * 0.13f) + 1.5f * std::sin(WX * 0.9f - WY * 0.7f);
	}

	void ScFillHeights(FTerrainHeightfield& HF, int32 Variant)
	{
		const int32 E = HF.HalfExtentM();
		for (int32 Y = 0; Y < HF.Width(); ++Y)
		{
			for (int32 X = 0; X < HF.Width(); ++X)
			{
				HF.SetHeight(X, Y, ScHeightAt(static_cast<float>(X - E), static_cast<float>(Y - E), Variant));
			}
		}
	}

	/** Paint discs (world metres) of one layer straight into the splat bytes. */
	void ScPaintDisc(FTerrainHeightfield& HF, float CX, float CY, float R, int32 Layer)
	{
		const int32 E = HF.HalfExtentM();
		for (int32 TY = 0; TY < HF.SplatSize(); ++TY)
		{
			for (int32 TX = 0; TX < HF.SplatSize(); ++TX)
			{
				const float WX = (static_cast<float>(TX) + 0.5f) * 0.5f - static_cast<float>(E);
				const float WY = (static_cast<float>(TY) + 0.5f) * 0.5f - static_cast<float>(E);
				if ((WX - CX) * (WX - CX) + (WY - CY) * (WY - CY) < R * R)
				{
					uint8* T = HF.SplatTexel(TX, TY);
					T[0] = T[1] = T[2] = T[3] = 0;
					T[Layer] = 255;
				}
			}
		}
	}

	/** A sculpted map with a dirt patch, a rock patch and a snow patch at fixed world places (deterministic, extent independent). */
	void ScMakeMap(FTerrainHeightfield& HF, int32 E, int32 Chunk, int32 Variant, bool bPaint)
	{
		HF.Init(E, Chunk);
		ScFillHeights(HF, Variant);
		if (bPaint)
		{
			ScPaintDisc(HF, 20.0f, 10.0f, 15.0f, 1);
			ScPaintDisc(HF, -30.0f, -22.0f, 12.0f, 2);
			ScPaintDisc(HF, 10.0f, -35.0f, 9.0f, 3);
			ScPaintDisc(HF, -15.0f, 30.0f, 5.0f, 1);
		}
	}

	FScatterHash ScRef(const FTerrainHeightfield& HF, const FScatterPalette& P, int32 Fine = 32, int32 Coarse = 80)
	{
		return ReferenceHash(HF, P, Fine, Coarse, false);
	}

	uint64 ScFnv(const FTerrainHeightfield& HF, const FScatterPalette& P, int32 Fine = 32, int32 Coarse = 80)
	{
		return ScatterFnv(P.ConfigFnv(), HF.HalfExtentM(), P.Level, ScRef(HF, P, Fine, Coarse));
	}

	bool ScRecordsEqual(const TArray<FScatterRecord>& A, const TArray<FScatterRecord>& B)
	{
		if (A.Num() != B.Num())
		{
			return false;
		}
		for (int32 I = 0; I < A.Num(); ++I)
		{
			if (A[I] != B[I])
			{
				return false;
			}
		}
		return true;
	}

	/** Terrain space metres of a Q16 value (tests only). */
	double ScM(int64 Q16)
	{
		return static_cast<double>(Q16) / 65536.0;
	}

	FTerrainBrushParams ScParams(ETerrainBrushMode Mode, float D, float Strength, int32 Layer, float Target)
	{
		FTerrainBrushParams P;
		P.Mode = Mode;
		P.DiameterM = D;
		P.Strength = Strength;
		P.PaintLayer = Layer;
		P.FlattenTarget = Target;
		return P;
	}

	/** The lattice cell (Q16 metres) of a record's class. */
	int64 ScCellOf(const FScatterPalette& P, uint8 Class)
	{
		switch (static_cast<EScatterClass>(Class))
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

	/** Tile index of a record in its own grid at the given tile sizes (cell -> tile, the lattice rule of plan C scatter 3.3). */
	int32 ScTileOfRecord(const FScatterRecord& R, const FScatterPalette& P, int32 E, int32 Fine, int32 Coarse, EScatterGrid& OutGrid)
	{
		const EScatterClass C = static_cast<EScatterClass>(R.Class);
		OutGrid = ScatterGridOfClass(C);
		const int32 Size = OutGrid == EScatterGrid::Fine ? Fine : Coarse;
		const FScatterTileGrid TG = MakeScatterTileGrid(E, Size);
		const int64 Cell = ScCellOf(P, R.Class);
		const int64 TileQ = static_cast<int64>(Size) << 16;
		const int32 TX = static_cast<int32>(FloorDiv(static_cast<int64>(R.IX) * Cell, TileQ));
		const int32 TY = static_cast<int32>(FloorDiv(static_cast<int64>(R.IY) * Cell, TileQ));
		return TG.IndexOf(TX, TY);
	}

	// ---- the live-state driver: the scheduler with a fake executor and clock, the ISM model per unit ----------------------------

	/** Drives FScatterScheduler the way ATerrainScatter will: snapshots at dispatch, jobs complete in random order, results apply under a budget to FFakeIsm units. */
	struct FScLive
	{
		const FTerrainHeightfield* HF = nullptr;
		FScatterPalette P;
		FScatterScheduler Sch;
		TArray<FFakeIsm> Isms[ScatterGridCount];

		struct FJob
		{
			FScatterDispatch D;
			FScatterSnapshot Snap;
			TArray<TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe>> Old;
		};
		TArray<FJob> Pending;
		TArray<FString> Errors;
		double Clock = 0.0;
		double ApplyCostMs = 1.0;
		TArray<uint8> BusyBefore[ScatterGridCount];
		int64 Launched = 0;
		int64 InlineJobs = 0;

		void Init(const FTerrainHeightfield& InHF, const FScatterPalette& InP, const FScatterSchedulerConfig& Cfg)
		{
			HF = &InHF;
			P = InP;
			Sch.Init(Cfg);
			for (int32 G = 0; G < ScatterGridCount; ++G)
			{
				const EScatterGrid Grid = static_cast<EScatterGrid>(G);
				Isms[G].Reset();
				Isms[G].SetNum(Sch.GetTileGrid(Grid).NumTiles() * ScatterMeshCountOfGrid(Grid));
			}
			Pending.Reset();
			Errors.Reset();
			Clock = 0.0;
			Launched = 0;
			InlineJobs = 0;
		}

		void MarkTick(const FTerrainTickResult& R)
		{
			Sch.MarkRects(R.HeightRect, R.SplatRect);
		}

		void MarkDelta(const FTerrainEditDelta& D)
		{
			for (const int32 Chunk : D.Chunks)
			{
				Sch.MarkRects(HF->ChunkOwnedRect(Chunk), HF->ChunkOwnedSplatRect(Chunk));
			}
		}

		void Launch(const FScatterDispatch& D)
		{
			if (BusyBefore[static_cast<int32>(D.Grid)][D.TileIndex])
			{
				Errors.Add(FString::Printf(TEXT("tile %d of grid %d dispatched while Busy"), D.TileIndex, static_cast<int32>(D.Grid)));
			}
			FJob J;
			J.D = D;
			J.Snap = MakeSnapshot(*HF, ScatterTileKeyAt(D.Grid, Sch.GetTileGrid(D.Grid), D.TileIndex));
			const int32 NM = ScatterMeshCountOfGrid(D.Grid);
			for (int32 M = 0; M < NM; ++M)
			{
				J.Old.Add(Sch.GetUnitState(D.Grid, D.TileIndex, M));
			}
			Pending.Add(MoveTemp(J));
			++Launched;
			if (D.bGameThread)
			{
				// ScatterThreads=0: the owner generates the tile inline, before the next poll.
				++InlineJobs;
				Complete(Pending.Num() - 1);
			}
		}

		/** The owner's disable: the scheduler's Disable, then the unit states and the ISMs are cleared. */
		void DisableAndClear()
		{
			Sch.Disable();
			Sch.ClearUnitStates();
			for (int32 G = 0; G < ScatterGridCount; ++G)
			{
				for (FFakeIsm& Ism : Isms[G])
				{
					Ism.Items.Reset();
				}
			}
		}

		int32 DispatchFrame(double BrushX, double BrushY)
		{
			for (int32 G = 0; G < ScatterGridCount; ++G)
			{
				const EScatterGrid Grid = static_cast<EScatterGrid>(G);
				BusyBefore[G].SetNum(Sch.GetTileGrid(Grid).NumTiles());
				for (int32 I = 0; I < BusyBefore[G].Num(); ++I)
				{
					BusyBefore[G][I] = Sch.IsBusy(Grid, I) ? 1 : 0;
				}
			}
			return Sch.Dispatch(Clock, BrushX, BrushY, [this](const FScatterDispatch& D) { Launch(D); });
		}

		/** The worker: evaluate the snapshot and build one edit script per unit against the state captured at dispatch; post the result. */
		void Complete(int32 Index)
		{
			FJob J = MoveTemp(Pending[Index]);
			Pending.RemoveAt(Index);
			const FScatterTileKey Key = ScatterTileKeyAt(J.D.Grid, Sch.GetTileGrid(J.D.Grid), J.D.TileIndex);
			FScatterTileRecords Recs;
			EvaluateTile(J.Snap, P, Key, Recs);
			FScatterTileResult R;
			R.Grid = J.D.Grid;
			R.TileIndex = J.D.TileIndex;
			R.Gen = J.D.Gen;
			R.Epoch = J.D.Epoch;
			TArray<EScatterMesh> Meshes;
			ScatterMeshesOfGrid(J.D.Grid, Meshes);
			for (int32 M = 0; M < Meshes.Num(); ++M)
			{
				FScatterUnitState Empty;
				const FScatterUnitState& Old = J.Old[M].IsValid() ? *J.Old[M] : Empty;
				FScatterUnitWork W;
				W.Mesh = static_cast<uint8>(Meshes[M]);
				W.InstancesBefore = Old.Records.Num();
				W.Script = BuildEditScript(Old, Recs.PerMesh[static_cast<int32>(Meshes[M])], W.Next);
				R.Units.Add(MoveTemp(W));
			}
			Sch.PostResult(MoveTemp(R));
		}

		void CompleteRandom(FScRng& Rng, int32 MaxCount)
		{
			const int32 N = Rng.Range(0, MaxCount);
			for (int32 K = 0; K < N && Pending.Num() > 0; ++K)
			{
				Complete(Rng.Range(0, Pending.Num() - 1));
			}
		}

		void ApplyUnit(EScatterGrid G, int32 Tile, const FScatterUnitWork& U)
		{
			const int32 MI = ScatterMeshIndexInGrid(static_cast<EScatterMesh>(U.Mesh));
			const int32 NM = ScatterMeshCountOfGrid(G);
			FFakeIsm& Ism = Isms[static_cast<int32>(G)][Tile * NM + MI];
			const TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> Old = Sch.GetUnitState(G, Tile, MI);
			const TArray<FScatterRecord> None;
			if (!ScRecordsEqual(Ism.Items, Old.IsValid() ? Old->Records : None))
			{
				Errors.Add(FString::Printf(TEXT("unit (grid %d tile %d mesh %d): the ISM model differs from the state the script was built against"), static_cast<int32>(G), Tile, U.Mesh));
			}
			for (int32 I = 1; I < U.Script.Removes.Num(); ++I)
			{
				if (U.Script.Removes[I] >= U.Script.Removes[I - 1])
				{
					Errors.Add(TEXT("removals are not strictly descending"));
				}
			}
			Ism.Apply(U.Script);
			if (!U.Next.IsValid() || !ScRecordsEqual(Ism.Items, U.Next->Records))
			{
				Errors.Add(FString::Printf(TEXT("unit (grid %d tile %d mesh %d): the ISM model after the script differs from the worker's next state"), static_cast<int32>(G), Tile, U.Mesh));
			}
			Clock += ApplyCostMs;
		}

		/** One frame: some jobs finish, results are polled, applied under the budget. */
		void Frame(FScRng& Rng, double BudgetMs, int32 MaxComplete)
		{
			CompleteRandom(Rng, MaxComplete);
			Sch.PollResults();
			Sch.ApplyReady(BudgetMs, [this]() { return Clock; }, [this](EScatterGrid G, int32 Tile, const FScatterUnitWork& U) { ApplyUnit(G, Tile, U); });
			Clock += 16.7;
		}

		/** Run until nothing is pending. Returns false when it did not settle. */
		bool Settle(FScRng& Rng, double BudgetMs, int32 MaxFrames = 20000)
		{
			for (int32 F = 0; F < MaxFrames; ++F)
			{
				Sch.SetStrokeOpen(false);
				Frame(Rng, BudgetMs, 3);
				DispatchFrame(0.0, 0.0);
				if (!Sch.HasPendingWork() && Pending.Num() == 0)
				{
					return true;
				}
			}
			return false;
		}

		FScatterHash FoldIsms() const
		{
			FScatterHash H;
			for (int32 G = 0; G < ScatterGridCount; ++G)
			{
				for (const FFakeIsm& Ism : Isms[G])
				{
					for (const FScatterRecord& R : Ism.Items)
					{
						ScatterFoldRecord(H, R);
					}
				}
			}
			return H;
		}
	};

	FScatterSchedulerConfig ScConfig(int32 E, int32 Fine, int32 Coarse, int32 MaxInFlight)
	{
		FScatterSchedulerConfig C;
		C.HalfExtentM = E;
		C.FineTileM = Fine;
		C.CoarseTileM = Coarse;
		C.MaxInFlight = MaxInFlight;
		return C;
	}
}

// ---- integer math ---------------------------------------------------------------------------------------------------------

SC_TEST(Mix32Golden)
{
	FString Text;
	if (!ScLoadText(TEXT("Scripts/scatter/mix32_golden.json"), Text))
	{
		AddError(TEXT("Scripts/scatter/mix32_golden.json missing"));
		return false;
	}
	TArray<int64> V;
	ScParseInts(Text, TEXT("\"pairs\""), V);
	if (V.Num() < 40 || (V.Num() & 1))
	{
		AddError(FString::Printf(TEXT("golden pairs parsed wrong: %d numbers"), V.Num()));
		return false;
	}
	for (int32 I = 0; I < V.Num(); I += 2)
	{
		if (Mix32(static_cast<uint32>(V[I])) != static_cast<uint32>(V[I + 1]))
		{
			AddError(FString::Printf(TEXT("Mix32(%lld) = %lld, golden %lld"), V[I], static_cast<int64>(Mix32(static_cast<uint32>(V[I]))), V[I + 1]));
			return false;
		}
	}
	AddInfo(FString::Printf(TEXT("Mix32 golden: %d pairs"), V.Num() / 2));
	return true;
}

SC_TEST(MathGolden)
{
	FString Text;
	if (!ScLoadText(TEXT("Scripts/scatter/scatter_math_golden.json"), Text))
	{
		AddError(TEXT("Scripts/scatter/scatter_math_golden.json missing (python Tools/scatter_math_ref.py)"));
		return false;
	}
	TArray<int64> Keys;
	TArray<int64> Noise;
	TArray<int64> Floors;
	ScParseInts(Text, TEXT("\"key\":"), Keys);
	ScParseInts(Text, TEXT("\"noise\":"), Noise);
	ScParseInts(Text, TEXT("\"floor_div\":"), Floors);
	if (Keys.Num() < 6 * 20 || Noise.Num() < 5 * 20 || Floors.Num() < 3 * 5 || (Keys.Num() % 6) || (Noise.Num() % 5) || (Floors.Num() % 3))
	{
		AddError(FString::Printf(TEXT("golden parse: key %d noise %d floor %d numbers"), Keys.Num(), Noise.Num(), Floors.Num()));
		return false;
	}
	bool bOk = true;
	for (int32 I = 0; I + 5 < Keys.Num(); I += 6)
	{
		const uint32 H = ScatterKey(static_cast<uint32>(Keys[I]), static_cast<uint32>(Keys[I + 1]), static_cast<int32>(Keys[I + 2]), static_cast<int32>(Keys[I + 3]), static_cast<uint32>(Keys[I + 4]));
		if (H != static_cast<uint32>(Keys[I + 5]))
		{
			AddError(FString::Printf(TEXT("ScatterKey golden %d: got %lld, want %lld"), I / 6, static_cast<int64>(H), Keys[I + 5]));
			bOk = false;
		}
	}
	for (int32 I = 0; I + 4 < Noise.Num(); I += 5)
	{
		const int64 V = ValueNoiseQ16(static_cast<uint32>(Noise[I]), Noise[I + 1], Noise[I + 2], Noise[I + 3]);
		if (V != Noise[I + 4])
		{
			AddError(FString::Printf(TEXT("ValueNoiseQ16 golden %d: got %lld, want %lld"), I / 5, V, Noise[I + 4]));
			bOk = false;
		}
	}
	for (int32 I = 0; I + 2 < Floors.Num(); I += 3)
	{
		if (FloorDiv(Floors[I], Floors[I + 1]) != Floors[I + 2])
		{
			AddError(FString::Printf(TEXT("FloorDiv(%lld, %lld) = %lld, want %lld"), Floors[I], Floors[I + 1], FloorDiv(Floors[I], Floors[I + 1]), Floors[I + 2]));
			bOk = false;
		}
	}
	AddInfo(FString::Printf(TEXT("math golden: %d keys, %d noise values, %d floor divisions"), Keys.Num() / 6, Noise.Num() / 5, Floors.Num() / 3));
	return bOk;
}

SC_TEST(MathBasics)
{
	bool bOk = true;
	auto Expect = [&](const TCHAR* What, int64 Got, int64 Want)
	{
		if (Got != Want)
		{
			AddError(FString::Printf(TEXT("%s: got %lld, want %lld"), What, Got, Want));
			bOk = false;
		}
	};
	Expect(TEXT("RampQ16 low"), RampQ16(5, 10, 20), 0);
	Expect(TEXT("RampQ16 high"), RampQ16(25, 10, 20), 65536);
	Expect(TEXT("RampQ16 mid"), RampQ16(15, 10, 20), 32768);
	Expect(TEXT("RampInvQ16 mid"), RampInvQ16(15, 10, 20), 32768);
	Expect(TEXT("BandQ16 peak"), BandQ16(15, 10, 15, 20), 65536);
	Expect(TEXT("BandQ16 left"), BandQ16(12, 10, 15, 20), 26214);
	Expect(TEXT("BandQ16 out"), BandQ16(20, 10, 15, 20), 0);
	Expect(TEXT("MulQ16 half"), MulQ16(32768, 32768), 16384);
	Expect(TEXT("MulQ16 one"), MulQ16(65536, 12345), 12345);
	Expect(TEXT("FloorDiv neg"), FloorDiv(-1, 5), -1);
	Expect(TEXT("FloorDiv neg exact"), FloorDiv(-10, 5), -2);
	Expect(TEXT("FloorDiv pos"), FloorDiv(11, 5), 2);
	Expect(TEXT("SlopeG2Q32"), SlopeG2Q32(65536, 0), 4294967296ll);
	Expect(TEXT("HeightToQ16 +"), HeightToQ16(1.5f), 98304);
	Expect(TEXT("HeightToQ16 -"), HeightToQ16(-1.5f), -98304);
	// FNV-1a 64 known vectors (the offset basis for "", "a" and "foobar").
	Expect(TEXT("fnv64 empty"), static_cast<int64>(Fnv1a64Chars(Fnv64OffsetBasis, "")), static_cast<int64>(14695981039346656037ull));
	Expect(TEXT("fnv64 a"), static_cast<int64>(Fnv1a64Chars(Fnv64OffsetBasis, "a")), static_cast<int64>(0xaf63dc4c8601ec8cull));
	Expect(TEXT("fnv64 foobar"), static_cast<int64>(Fnv1a64Chars(Fnv64OffsetBasis, "foobar")), static_cast<int64>(0x85944171f73967e8ull));
	// The fixed-width helpers equal byte-wise FNV over the little-endian bytes.
	uint64 A = Fnv64OffsetBasis;
	A = Fnv1a64U32(A, 0x04030201u);
	uint64 B = Fnv64OffsetBasis;
	for (uint32 Byte = 1; Byte <= 4; ++Byte)
	{
		B = Fnv1a64U8(B, Byte);
	}
	Expect(TEXT("Fnv1a64U32 little endian"), static_cast<int64>(A), static_cast<int64>(B));
	return bOk;
}

SC_TEST(SurfaceQ16VsSampleSurface)
{
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 64, 3, false);
	// Steep and high corners so the float rounding of SampleSurface is exercised near 128 m.
	for (int32 Y = 0; Y < 12; ++Y)
	{
		for (int32 X = 0; X < 12; ++X)
		{
			HF.SetHeight(X, Y, 100.0f + 20.0f * std::sin(static_cast<float>(X * 7 + Y * 3)));
			HF.SetHeight(HF.Width() - 1 - X, HF.Width() - 1 - Y, -110.0f + 15.0f * std::cos(static_cast<float>(X * 5 + Y)));
		}
	}
	const FScatterSnapshot S = MakeFullSnapshot(HF);
	FScRng Rng(777);
	double MaxDiff = 0.0;
	const int32 E = HF.HalfExtentM();
	const int32 N = 10000;
	for (int32 I = 0; I < N; ++I)
	{
		int64 XQ = static_cast<int64>(Rng.Between(-static_cast<float>(E), static_cast<float>(E)) * 65536.0f);
		int64 YQ = static_cast<int64>(Rng.Between(-static_cast<float>(E), static_cast<float>(E)) * 65536.0f);
		if (I < 16)
		{
			// The four corners, the four edge midpoints and some exact vertices: edges included.
			const int64 Edge = static_cast<int64>(E) * 65536;
			XQ = (I & 1) ? Edge : -Edge;
			YQ = (I & 2) ? Edge : -Edge;
			if (I >= 8)
			{
				XQ = (I & 1) ? 0 : XQ;
				YQ = (I & 2) ? 0 : YQ;
			}
		}
		const double Want = static_cast<double>(HF.SampleSurface(ScM(XQ), ScM(YQ)));
		const double Got = ScM(SurfaceQ16(S, XQ, YQ));
		MaxDiff = FMath::Max(MaxDiff, static_cast<double>(std::fabs(Got - Want)));
	}
	AddInfo(FString::Printf(TEXT("SurfaceQ16 vs SampleSurface over %d points: max |difference| = %.3g m (bound 2^-15 = 3.05e-05 m)"), N, MaxDiff));
	if (MaxDiff > 3.0517578125e-5)
	{
		AddError(FString::Printf(TEXT("SurfaceQ16 differs from SampleSurface by %.3g m"), MaxDiff));
		return false;
	}
	return true;
}

SC_TEST(PositionsLargeExtent)
{
	// E = 512 with 4 m cells: 65,535 x 4 m in Q16 does not fit int32, so every product must be int64. Corner tiles of both grids.
	FTerrainHeightfield HF;
	HF.Init(512, 64);
	ScFillHeights(HF, 5);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	const int64 EQ = 512ll * 65536;
	int32 Records = 0;
	bool bOk = true;
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const FScatterTileGrid TG = MakeScatterTileGrid(512, Grid == EScatterGrid::Fine ? 32 : 80);
		const int32 Last = TG.Min + TG.Count - 1;
		const int32 Corners[4][2] = {{TG.Min, TG.Min}, {Last, TG.Min}, {TG.Min, Last}, {Last, Last}};
		for (int32 C = 0; C < 4; ++C)
		{
			FScatterTileKey Key;
			Key.Grid = Grid;
			Key.TX = Corners[C][0];
			Key.TY = Corners[C][1];
			Key.SizeM = TG.SizeM;
			const FScatterSnapshot S = MakeSnapshot(HF, Key);
			FScatterTileRecords Recs;
			EvaluateTile(S, P, Key, Recs);
			for (int32 M = 0; M < ScatterMeshCount; ++M)
			{
				for (const FScatterRecord& R : Recs.PerMesh[M])
				{
					++Records;
					const int64 Cell = ScCellOf(P, R.Class);
					if (FMath::Abs(static_cast<int64>(R.XQ)) >= EQ || FMath::Abs(static_cast<int64>(R.YQ)) >= EQ)
					{
						AddError(FString::Printf(TEXT("record outside the map: (%d, %d)"), R.XQ, R.YQ));
						bOk = false;
					}
					const int64 CX = static_cast<int64>(R.IX) * Cell;
					const int64 CY = static_cast<int64>(R.IY) * Cell;
					if (R.XQ < CX || R.XQ >= CX + Cell || R.YQ < CY || R.YQ >= CY + Cell)
					{
						AddError(FString::Printf(TEXT("class %d record (%d, %d) leaves its cell (%d, %d)"), R.Class, R.XQ, R.YQ, R.IX, R.IY));
						bOk = false;
					}
				}
			}
		}
	}
	if (Records < 500)
	{
		AddError(FString::Printf(TEXT("only %d records on the E = 512 corner tiles"), Records));
		bOk = false;
	}
	AddInfo(FString::Printf(TEXT("E = 512 corner tiles: %d records, all inside the map and inside their cells"), Records));
	return bOk;
}

SC_TEST(SplatBilinear)
{
	FTerrainHeightfield HF;
	HF.Init(16, 16);
	FScRng Rng(31);
	for (int32 I = 0; I < HF.SplatSize() * HF.SplatSize() * 4; ++I)
	{
		HF.Splat[I] = static_cast<uint8>(Rng.Range(0, 255));
	}
	const FScatterSnapshot S = MakeFullSnapshot(HF);
	const int64 EQ = 16ll * 65536;
	bool bOk = true;
	// Exact at texel centres.
	for (int32 K = 0; K < 400 && bOk; ++K)
	{
		const int32 TX = Rng.Range(0, HF.SplatSize() - 1);
		const int32 TY = Rng.Range(0, HF.SplatSize() - 1);
		const int64 XQ = static_cast<int64>(TX) * 32768 + 16384 - EQ;
		const int64 YQ = static_cast<int64>(TY) * 32768 + 16384 - EQ;
		for (int32 Ch = 0; Ch < 4; ++Ch)
		{
			if (SplatQ8Bilinear(S, XQ, YQ, Ch) != HF.SplatTexel(TX, TY)[Ch])
			{
				AddError(FString::Printf(TEXT("texel (%d, %d) channel %d not exact at its centre"), TX, TY, Ch));
				bOk = false;
			}
		}
	}
	// Between centres: inside the min..max of the four neighbours and close to the double-precision bilinear value.
	for (int32 K = 0; K < 2000 && bOk; ++K)
	{
		const int64 XQ = static_cast<int64>(Rng.Between(-15.5f, 15.5f) * 65536.0f);
		const int64 YQ = static_cast<int64>(Rng.Between(-15.5f, 15.5f) * 65536.0f);
		const double SX = (ScM(XQ) + 16.0) * 2.0 - 0.5;
		const double SY = (ScM(YQ) + 16.0) * 2.0 - 0.5;
		const int32 T0X = static_cast<int32>(std::floor(SX));
		const int32 T0Y = static_cast<int32>(std::floor(SY));
		const double FX = SX - T0X;
		const double FY = SY - T0Y;
		for (int32 Ch = 0; Ch < 4; ++Ch)
		{
			const double V00 = HF.SplatTexel(T0X, T0Y)[Ch];
			const double V10 = HF.SplatTexel(T0X + 1, T0Y)[Ch];
			const double V01 = HF.SplatTexel(T0X, T0Y + 1)[Ch];
			const double V11 = HF.SplatTexel(T0X + 1, T0Y + 1)[Ch];
			const double Want = (V00 * (1 - FX) + V10 * FX) * (1 - FY) + (V01 * (1 - FX) + V11 * FX) * FY;
			const double Lo = FMath::Min(FMath::Min(V00, V10), FMath::Min(V01, V11));
			const double Hi = FMath::Max(FMath::Max(V00, V10), FMath::Max(V01, V11));
			const int32 Got = SplatQ8Bilinear(S, XQ, YQ, Ch);
			if (Got < Lo - 0.001 || Got > Hi + 0.001 || std::fabs(Got - Want) > 0.51)
			{
				AddError(FString::Printf(TEXT("bilinear at (%lld, %lld) channel %d: got %d, double %.3f in [%.0f, %.0f]"), XQ, YQ, Ch, Got, Want, Lo, Hi));
				bOk = false;
			}
		}
	}
	// Edges clamp: at the map edge the weight equals the edge texel's.
	{
		const int64 XQ = -EQ;
		const int64 YQ = -EQ;
		for (int32 Ch = 0; Ch < 4; ++Ch)
		{
			if (SplatQ8Bilinear(S, XQ, YQ, Ch) != HF.SplatTexel(0, 0)[Ch])
			{
				AddError(TEXT("the map corner does not clamp to texel (0, 0)"));
				bOk = false;
			}
		}
	}
	return bOk;
}

// ---- generator invariances ------------------------------------------------------------------------------------------------

SC_TEST(SameInputTwice)
{
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 64, 1, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	TArray<FScatterRecord> A;
	TArray<FScatterRecord> B;
	ReferenceRecords(HF, P, 32, 80, A);
	ReferenceRecords(HF, P, 32, 80, B);
	if (A.Num() < 1000 || !ScRecordsEqual(A, B))
	{
		AddError(FString::Printf(TEXT("same input twice gave %d and %d records, or they differ"), A.Num(), B.Num()));
		return false;
	}
	AddInfo(FString::Printf(TEXT("sculpted E = 64 L1 (map 1, painted): %d records, scatter_fnv %016llx"), A.Num(), static_cast<unsigned long long>(ScFnv(HF, P))));
	// Keys are unique and the state's key view finds every record.
	FScatterUnitState State;
	State.Records = A;
	State.RebuildSorted();
	for (int32 I = 0; I < A.Num(); I += 7)
	{
		if (State.Find(A[I].Key()) != I)
		{
			AddError(FString::Printf(TEXT("record %d: Find(key) did not return its index"), I));
			return false;
		}
	}
	for (int32 I = 1; I < State.Sorted.Num(); ++I)
	{
		if (State.Sorted[I].Key == State.Sorted[I - 1].Key)
		{
			AddError(TEXT("duplicate candidate key"));
			return false;
		}
	}
	if (State.Find(~0ull) != INDEX_NONE)
	{
		AddError(TEXT("Find of an absent key returned an index"));
		return false;
	}
	return true;
}

SC_TEST(TileSizeInvariance)
{
	FTerrainHeightfield HF;
	ScMakeMap(HF, 96, 64, 2, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	const uint64 Base = ScFnv(HF, P, 32, 80);
	bool bOk = true;
	const int32 Fines[3] = {16, 32, 64};
	const int32 Coarses[3] = {40, 80, 160};
	for (const int32 F : Fines)
	{
		for (const int32 C : Coarses)
		{
			const uint64 H = ScFnv(HF, P, F, C);
			if (H != Base)
			{
				AddError(FString::Printf(TEXT("scatter_fnv changes with tile sizes %d / %d"), F, C));
				bOk = false;
			}
		}
	}
	return bOk;
}

SC_TEST(ChunkSizeInvariance)
{
	FTerrainHeightfield A;
	FTerrainHeightfield B;
	ScMakeMap(A, 64, 32, 4, true);
	ScMakeMap(B, 64, 64, 4, true);
	FScatterPalette P;
	const uint64 HA = ScFnv(A, P);
	const uint64 HB = ScFnv(B, P);
	if (HA != HB || A.HeightFnv() != B.HeightFnv())
	{
		AddError(TEXT("ChunkQuads 32 and 64 give different scatter_fnv for the same heights and splat"));
		return false;
	}
	return true;
}

SC_TEST(ExtentOverlap)
{
	// The same world (heights and paint as functions of world metres) at E = 160 and E = 320: identical records over the overlap, except cells
	// within 5 m of the smaller map's edge (reads there clamp to the smaller map).
	FTerrainHeightfield Small;
	FTerrainHeightfield Big;
	ScMakeMap(Small, 160, 64, 6, true);
	ScMakeMap(Big, 320, 64, 6, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	TArray<FScatterRecord> RS;
	TArray<FScatterRecord> RB;
	ReferenceRecords(Small, P, 32, 80, RS);
	ReferenceRecords(Big, P, 32, 80, RB);
	const int64 Limit = (160 - 5) * 65536ll;
	auto Inside = [&](const FScatterRecord& R) { return FMath::Abs(static_cast<int64>(R.XQ)) < Limit && FMath::Abs(static_cast<int64>(R.YQ)) < Limit; };
	FScatterUnitState SB;
	SB.Records = RB;
	SB.RebuildSorted();
	FScatterUnitState SS;
	SS.Records = RS;
	SS.RebuildSorted();
	int32 Compared = 0;
	for (const FScatterRecord& R : RS)
	{
		if (!Inside(R))
		{
			continue;
		}
		const int32 Index = SB.Find(R.Key());
		++Compared;
		if (Index == INDEX_NONE || RB[Index] != R)
		{
			AddError(FString::Printf(TEXT("record of the E = 160 map (class %d, cell %d, %d) missing or different in the E = 320 map"), R.Class, R.IX, R.IY));
			return false;
		}
	}
	for (const FScatterRecord& R : RB)
	{
		if (!Inside(R))
		{
			continue;
		}
		const int32 Index = SS.Find(R.Key());
		if (Index == INDEX_NONE || RS[Index] != R)
		{
			AddError(FString::Printf(TEXT("record of the E = 320 map (class %d, cell %d, %d) missing or different in the E = 160 map"), R.Class, R.IX, R.IY));
			return false;
		}
	}
	AddInfo(FString::Printf(TEXT("E = 160 vs 320: %d records compared over the overlap, identical (small %d, big %d in total)"), Compared, RS.Num(), RB.Num()));
	return Compared > 10000;
}

SC_TEST(ParallelEqualsSerial)
{
	FTerrainHeightfield HF;
	ScMakeMap(HF, 96, 64, 7, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	const FScatterHash Serial = ReferenceHash(HF, P, 32, 80, false);
	const FScatterHash Parallel = ReferenceHash(HF, P, 32, 80, true);
	if (!(Serial == Parallel) || Serial.TotalCount() < 1000)
	{
		AddError(TEXT("the ParallelFor reference differs from the serial one"));
		return false;
	}
	return true;
}

SC_TEST(SnapshotEqualsLive)
{
	// A tile snapshot (with its apron) generates exactly what a snapshot of the whole map generates, and never reads outside its region.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 48, 64, 8, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	const FScatterSnapshot Full = MakeFullSnapshot(HF);
	bool bOk = true;
	const int32 Sizes[2][2] = {{16, 40}, {64, 160}};
	for (int32 Variant = 0; Variant < 2 && bOk; ++Variant)
	{
		for (int32 G = 0; G < ScatterGridCount && bOk; ++G)
		{
			const EScatterGrid Grid = static_cast<EScatterGrid>(G);
			const FScatterTileGrid TG = MakeScatterTileGrid(48, Sizes[Variant][G]);
			for (int32 I = 0; I < TG.NumTiles() && bOk; ++I)
			{
				const FScatterTileKey Key = ScatterTileKeyAt(Grid, TG, I);
				ScatterSnapshotMisses() = 0;
				FScatterTileRecords FromTile;
				EvaluateTile(MakeSnapshot(HF, Key), P, Key, FromTile);
				const int32 Misses = ScatterSnapshotMisses();
				FScatterTileRecords FromFull;
				EvaluateTile(Full, P, Key, FromFull);
				if (Misses != 0)
				{
					AddError(FString::Printf(TEXT("tile %d of grid %d read %d times outside its snapshot"), I, G, Misses));
					bOk = false;
				}
				for (int32 M = 0; M < ScatterMeshCount; ++M)
				{
					if (!ScRecordsEqual(FromTile.PerMesh[M], FromFull.PerMesh[M]))
					{
						AddError(FString::Printf(TEXT("tile %d of grid %d mesh %d: tile snapshot and full snapshot differ"), I, G, M));
						bOk = false;
						break;
					}
				}
			}
		}
	}
	return bOk;
}

SC_TEST(HeightfieldHashUnchanged)
{
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 64, 9, true);
	const uint32 H0 = HF.HeightFnv();
	const uint32 S0 = HF.SplatFnv();
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	ReferenceHash(HF, P, 32, 80, true);
	ReferenceHash(HF, P, 16, 40, false);
	if (HF.HeightFnv() != H0 || HF.SplatFnv() != S0)
	{
		AddError(TEXT("generation changed the heightfield's FNV"));
		return false;
	}
	return true;
}

SC_TEST(RecordHashIgnoresPadding)
{
	// Hash a record built in memory first filled with 0xCD, then 0x00, then a normally constructed one: field-wise little-endian hashing.
	FScatterRecord Plain;
	auto Assign = [](FScatterRecord* R)
	{
		R->Class = 4;
		R->Mesh = 6;
		R->Stream = 15;
		R->IX = -123;
		R->IY = 4567;
		R->Slot = 2;
		R->XQ = -1234567;
		R->YQ = 7654321;
		R->ZQ = -98765;
		R->GXQ = 4321;
		R->GYQ = -1234;
		R->Yaw = 65000;
		R->Scale = 4100;
		R->ZScale = 3999;
		R->Tilt = 1;
		R->CD0 = 65535;
		R->CD1 = 17;
	};
	Assign(&Plain);
	alignas(FScatterRecord) uint8 Buf[sizeof(FScatterRecord)];
	uint64 Hashes[2] = {0, 0};
	const uint8 Fills[2] = {0xCD, 0x00};
	for (int32 K = 0; K < 2; ++K)
	{
		FMemory::Memset(Buf, Fills[K], sizeof(Buf));
		FScatterRecord* R = reinterpret_cast<FScatterRecord*>(Buf);
		Assign(R);
		Hashes[K] = HashRecord(*R);
	}
	const uint64 HP = HashRecord(Plain);
	if (Hashes[0] != HP || Hashes[1] != HP)
	{
		AddError(TEXT("HashRecord depends on the record's padding bytes"));
		return false;
	}
	// The canonical byte form (scatter_dump) is 45 bytes and FNV-1a 64 over it is the record hash.
	TArray<uint8> Bytes;
	AppendScatterRecordBytes(Bytes, Plain);
	uint64 FromBytes = Fnv64OffsetBasis;
	for (const uint8 B : Bytes)
	{
		FromBytes = Fnv1a64U8(FromBytes, B);
	}
	if (Bytes.Num() != ScatterRecordBytes || FromBytes != HP)
	{
		AddError(FString::Printf(TEXT("canonical record bytes: %d bytes (want %d), hash over them differs from HashRecord"), Bytes.Num(), ScatterRecordBytes));
		return false;
	}
	// Every field moves the hash.
	FScatterRecord M = Plain;
	M.CD1 = 18;
	if (HashRecord(M) == HP)
	{
		AddError(TEXT("HashRecord ignores CD1"));
		return false;
	}
	M = Plain;
	M.Slot = 3;
	if (HashRecord(M) == HP)
	{
		AddError(TEXT("HashRecord ignores Slot"));
		return false;
	}
	return true;
}

// ---- incremental = full, undo / redo ----------------------------------------------------------------------------------------

SC_TEST(IncrementalEqualsFull)
{
	// 60 seeded strokes of every mode (raise, lower, smooth, flatten, paint on each layer), diameters 5-100, with per-tick dirty marking, the
	// scheduler dispatching, the edit scripts applied to the ISM model: after every stroke the live fold equals a full regeneration.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 32, 10, true);
	FTerrainUndo Undo;
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	FScLive Live;
	Live.Init(HF, P, ScConfig(64, 32, 80, 8));
	FScRng Rng(2026);
	Live.Sch.BumpEpochAndDirtyAll();
	if (!Live.Settle(Rng, 0.0))
	{
		AddError(TEXT("the first fill did not settle"));
		return false;
	}
	const FScatterHash Ref0 = ScRef(HF, P);
	if (!(Live.FoldIsms() == Ref0) || !(Live.Sch.FoldApplied() == Ref0))
	{
		AddError(TEXT("after the first fill the live state differs from the reference"));
		return false;
	}
	bool bOk = true;
	const int32 Strokes = 60;
	int32 UndoCount = 0;
	for (int32 S = 0; S < Strokes && bOk; ++S)
	{
		FTerrainBrushParams BP;
		const int32 Mode = S % 6;
		const float X = Rng.Between(-60.0f, 60.0f);
		const float Y = Rng.Between(-60.0f, 60.0f);
		const float D = Rng.Between(5.0f, 100.0f);
		if (Mode < 4)
		{
			const ETerrainBrushMode Modes[4] = {ETerrainBrushMode::Raise, ETerrainBrushMode::Lower, ETerrainBrushMode::Smooth, ETerrainBrushMode::Flatten};
			BP = ScParams(Modes[Mode], D, Rng.Between(5.0f, 100.0f), 0, FTerrainBrush::FlattenTargetAt(HF, X, Y));
		}
		else
		{
			BP = ScParams(ETerrainBrushMode::Paint, D, Rng.Between(20.0f, 100.0f), ((S / 6) * 2 + (Mode - 4)) % 4, 0.0f);
		}
		Undo.BeginStroke();
		const int32 Ticks = Rng.Range(1, 4);
		for (int32 T = 0; T < Ticks; ++T)
		{
			const FTerrainTickResult R = FTerrainBrush::ApplyTick(HF, BP, X + T * 2.0f, Y, &Undo);
			Live.MarkTick(R);
		}
		Undo.EndStroke(HF);
		if (!Live.Settle(Rng, 0.0))
		{
			AddError(FString::Printf(TEXT("stroke %d did not settle"), S));
			bOk = false;
			break;
		}
		const FScatterHash Ref = ScRef(HF, P);
		if (!(Live.FoldIsms() == Ref) || !(Live.Sch.FoldApplied() == Ref))
		{
			AddError(FString::Printf(TEXT("after stroke %d (mode %d, d %.0f) the live fold differs from the full regeneration"), S, Mode, D));
			bOk = false;
		}
		if (S % 7 == 3 && Undo.CanUndo())
		{
			FTerrainEditDelta Delta;
			Undo.Undo(HF, Delta);
			Live.MarkDelta(Delta);
			if (!Live.Settle(Rng, 0.0))
			{
				AddError(FString::Printf(TEXT("the undo following stroke %d did not settle"), S));
				bOk = false;
			}
			if (!(Live.FoldIsms() == ScRef(HF, P)))
			{
				AddError(FString::Printf(TEXT("after the undo following stroke %d the live fold differs"), S));
				bOk = false;
			}
			Undo.Redo(HF, Delta);
			Live.MarkDelta(Delta);
			if (!Live.Settle(Rng, 0.0))
			{
				AddError(FString::Printf(TEXT("the redo following stroke %d did not settle"), S));
				bOk = false;
			}
			if (!(Live.FoldIsms() == ScRef(HF, P)))
			{
				AddError(FString::Printf(TEXT("after the redo following stroke %d the live fold differs"), S));
				bOk = false;
			}
			++UndoCount;
		}
	}
	for (const FString& E : Live.Errors)
	{
		AddError(E);
		bOk = false;
	}
	const FScatterCounters& C = Live.Sch.GetCounters();
	AddInfo(FString::Printf(TEXT("incremental: %d strokes, %d undo/redo pairs, dispatched %lld applied %lld identical %lld discarded %lld units %lld"), Strokes, UndoCount, C.Dispatched, C.Applied,
		C.SkippedIdentical, C.DiscardedEpoch, C.UnitsApplied));
	if (C.Dispatched != C.Applied + C.SkippedIdentical + C.DiscardedEpoch + C.Cancelled)
	{
		AddError(TEXT("counters do not balance"));
		bOk = false;
	}
	return bOk;
}

SC_TEST(UndoRedoRestoresHashes)
{
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 32, 11, true);
	FTerrainUndo Undo;
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	const uint64 F0 = ScFnv(HF, P);
	Undo.BeginStroke();
	FTerrainBrush::ApplyTick(HF, ScParams(ETerrainBrushMode::Raise, 40.0f, 80.0f, 0, 0.0f), 5.0f, 5.0f, &Undo);
	FTerrainBrush::ApplyTick(HF, ScParams(ETerrainBrushMode::Raise, 40.0f, 80.0f, 0, 0.0f), 5.0f, 5.0f, &Undo);
	Undo.EndStroke(HF);
	const uint64 F1 = ScFnv(HF, P);
	Undo.BeginStroke();
	FTerrainBrush::ApplyTick(HF, ScParams(ETerrainBrushMode::Paint, 50.0f, 100.0f, 2, 0.0f), -10.0f, 12.0f, &Undo);
	Undo.EndStroke(HF);
	const uint64 F2 = ScFnv(HF, P);
	FTerrainEditDelta D;
	bool bOk = true;
	Undo.Undo(HF, D);
	bOk &= (ScFnv(HF, P) == F1);
	Undo.Undo(HF, D);
	bOk &= (ScFnv(HF, P) == F0);
	Undo.Redo(HF, D);
	bOk &= (ScFnv(HF, P) == F1);
	Undo.Redo(HF, D);
	bOk &= (ScFnv(HF, P) == F2);
	if (!bOk || F0 == F1 || F1 == F2)
	{
		AddError(TEXT("undo / redo did not restore the earlier scatter_fnv values (or the strokes changed nothing)"));
		return false;
	}
	return true;
}

SC_TEST(LocalityOfEdits)
{
	// Records outside the dirty tiles of a stroke tick are byte-identical before and after; inside them something changes.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 96, 32, 12, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	const int32 Fine = 32;
	const int32 Coarse = 80;
	TArray<FScatterRecord> Before;
	ReferenceRecords(HF, P, Fine, Coarse, Before);
	const FTerrainTickResult R = FTerrainBrush::ApplyTick(HF, ScParams(ETerrainBrushMode::Raise, 30.0f, 100.0f, 0, 0.0f), -40.0f, 25.0f, nullptr);
	const FTerrainTickResult R2 = FTerrainBrush::ApplyTick(HF, ScParams(ETerrainBrushMode::Paint, 20.0f, 100.0f, 1, 0.0f), 30.0f, -45.0f, nullptr);
	TArray<FScatterRecord> After;
	ReferenceRecords(HF, P, Fine, Coarse, After);
	TArray<int32> Dirty[ScatterGridCount];
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const FScatterTileGrid TG = MakeScatterTileGrid(96, Grid == EScatterGrid::Fine ? Fine : Coarse);
		TArray<int32> A;
		TArray<int32> B;
		ScatterDirtyTiles(TG, Grid, 96, R.HeightRect, R.SplatRect, A);
		ScatterDirtyTiles(TG, Grid, 96, R2.HeightRect, R2.SplatRect, B);
		for (const int32 I : A)
		{
			Dirty[G].Add(I);
		}
		for (const int32 I : B)
		{
			Dirty[G].Add(I);
		}
	}
	FScatterUnitState SA;
	SA.Records = After;
	SA.RebuildSorted();
	FScatterUnitState SB;
	SB.Records = Before;
	SB.RebuildSorted();
	int32 ChangedInside = 0;
	int32 Outside = 0;
	auto IsDirty = [&](const FScatterRecord& Rec)
	{
		EScatterGrid Grid;
		const int32 Tile = ScTileOfRecord(Rec, P, 96, Fine, Coarse, Grid);
		for (const int32 D : Dirty[static_cast<int32>(Grid)])
		{
			if (D == Tile)
			{
				return true;
			}
		}
		return false;
	};
	for (const FScatterRecord& Rec : Before)
	{
		const int32 Other = SA.Find(Rec.Key());
		const bool bSame = Other != INDEX_NONE && After[Other] == Rec;
		if (IsDirty(Rec))
		{
			ChangedInside += bSame ? 0 : 1;
		}
		else
		{
			++Outside;
			if (!bSame)
			{
				AddError(FString::Printf(TEXT("a record outside the dirty tiles changed (class %d cell %d, %d)"), Rec.Class, Rec.IX, Rec.IY));
				return false;
			}
		}
	}
	for (const FScatterRecord& Rec : After)
	{
		if (!IsDirty(Rec) && SB.Find(Rec.Key()) == INDEX_NONE)
		{
			AddError(FString::Printf(TEXT("a record appeared outside the dirty tiles (class %d cell %d, %d)"), Rec.Class, Rec.IX, Rec.IY));
			return false;
		}
	}
	AddInfo(FString::Printf(TEXT("locality: %d records outside the dirty tiles unchanged, %d changed inside"), Outside, ChangedInside));
	return ChangedInside > 100 && Outside > 1000;
}

SC_TEST(ApronCoversReach)
{
	// Single-vertex and single-texel edits: every record that changes lies in a tile the dirty-tile maths marks (small tiles make the apron matter).
	FTerrainHeightfield HF;
	ScMakeMap(HF, 48, 16, 13, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	FScRng Rng(88);
	bool bOk = true;
	int32 TotalChanged = 0;
	const int32 Fine = 16;
	const int32 Coarse = 40;
	for (int32 Trial = 0; Trial < 120 && bOk; ++Trial)
	{
		TArray<FScatterRecord> Before;
		ReferenceRecords(HF, P, Fine, Coarse, Before);
		FTerrainRect VR;
		FTerrainRect SR;
		if (Trial % 2 == 0)
		{
			const int32 X = Rng.Range(1, HF.Width() - 3);
			const int32 Y = Rng.Range(1, HF.Width() - 3);
			for (int32 DY = 0; DY < 2; ++DY)
			{
				for (int32 DX = 0; DX < 2; ++DX)
				{
					HF.SetHeight(X + DX, Y + DY, HF.GetHeight(X + DX, Y + DY) + Rng.Between(-4.0f, 4.0f));
				}
			}
			VR = FTerrainRect(X, Y, X + 2, Y + 2);
		}
		else
		{
			const int32 TX = Rng.Range(0, HF.SplatSize() - 3);
			const int32 TY = Rng.Range(0, HF.SplatSize() - 3);
			const int32 Layer = Rng.Range(1, 3);
			for (int32 DY = 0; DY < 3; ++DY)
			{
				for (int32 DX = 0; DX < 3; ++DX)
				{
					uint8* T = HF.SplatTexel(TX + DX, TY + DY);
					T[0] = T[1] = T[2] = T[3] = 0;
					T[Layer] = 255;
				}
			}
			SR = FTerrainRect(TX, TY, TX + 3, TY + 3);
		}
		TArray<FScatterRecord> After;
		ReferenceRecords(HF, P, Fine, Coarse, After);
		FScatterUnitState SA;
		SA.Records = After;
		SA.RebuildSorted();
		FScatterUnitState SB;
		SB.Records = Before;
		SB.RebuildSorted();
		TArray<int32> Dirty[ScatterGridCount];
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			const EScatterGrid Grid = static_cast<EScatterGrid>(G);
			ScatterDirtyTiles(MakeScatterTileGrid(48, Grid == EScatterGrid::Fine ? Fine : Coarse), Grid, 48, VR, SR, Dirty[G]);
		}
		auto Check = [&](const FScatterRecord& Rec)
		{
			EScatterGrid Grid;
			const int32 Tile = ScTileOfRecord(Rec, P, 48, Fine, Coarse, Grid);
			for (const int32 D : Dirty[static_cast<int32>(Grid)])
			{
				if (D == Tile)
				{
					return;
				}
			}
			AddError(FString::Printf(TEXT("trial %d: a record changed in a tile that was not marked dirty (class %d cell %d, %d)"), Trial, Rec.Class, Rec.IX, Rec.IY));
			bOk = false;
		};
		for (const FScatterRecord& Rec : Before)
		{
			const int32 O = SA.Find(Rec.Key());
			if (O == INDEX_NONE || After[O] != Rec)
			{
				++TotalChanged;
				Check(Rec);
			}
		}
		for (const FScatterRecord& Rec : After)
		{
			if (SB.Find(Rec.Key()) == INDEX_NONE)
			{
				++TotalChanged;
				Check(Rec);
			}
		}
	}
	AddInfo(FString::Printf(TEXT("apron: %d changed records over 120 small vertex / texel block edits, all inside marked tiles"), TotalChanged));
	return bOk && TotalChanged > 20;
}

SC_TEST(ApronCoversTreeProbes)
{
	// The widest reach of any rule: a tree reads dirt at 4 m probes. Paint the splat around the probe of a tree that sits at a coarse tile edge:
	// the tree disappears, and the dirty tiles marked from that texel block include the tree's own tile.
	FTerrainHeightfield HF;
	HF.Init(96, 32);
	FScatterPalette P;
	TArray<FScatterRecord> Recs;
	ReferenceRecords(HF, P, 32, 40, Recs);
	const int32 Coarse = 40;
	int32 Tested = 0;
	for (const FScatterRecord& R : Recs)
	{
		if (R.Class != static_cast<uint8>(EScatterClass::Tree) || Tested >= 60)
		{
			continue;
		}
		const double X = ScM(R.XQ);
		const double Y = ScM(R.YQ);
		// Near a coarse tile boundary (so the probe lies across it), away from the map border.
		const double RX = std::fabs(X - Coarse * std::floor(X / Coarse + 0.5));
		if (RX > 2.5 || std::fabs(X) > 80.0 || std::fabs(Y) > 80.0)
		{
			continue;
		}
		FTerrainHeightfield Work = HF;
		const double PX = X + (X >= Coarse * std::floor(X / Coarse + 0.5) ? -4.0 : 4.0);  // the probe on the other side of the boundary
		const int32 TX0 = static_cast<int32>(std::floor((PX + 96.0) * 2.0)) - 1;
		const int32 TY0 = static_cast<int32>(std::floor((Y + 96.0) * 2.0)) - 1;
		for (int32 DY = 0; DY < 3; ++DY)
		{
			for (int32 DX = 0; DX < 3; ++DX)
			{
				uint8* T = Work.SplatTexel(TX0 + DX, TY0 + DY);
				T[0] = T[2] = T[3] = 0;
				T[1] = 255;
			}
		}
		TArray<FScatterRecord> After;
		ReferenceRecords(Work, P, 32, 40, After);
		FScatterUnitState SA;
		SA.Records = After;
		SA.RebuildSorted();
		if (SA.Find(R.Key()) != INDEX_NONE)
		{
			continue;  // the probe was not at the painted block after all (jitter): not a test of this tree
		}
		++Tested;
		TArray<int32> Dirty;
		ScatterDirtyTiles(MakeScatterTileGrid(96, Coarse), EScatterGrid::Coarse, 96, FTerrainRect(), FTerrainRect(TX0, TY0, TX0 + 3, TY0 + 3), Dirty);
		EScatterGrid Grid;
		const int32 Tile = ScTileOfRecord(R, P, 96, 32, Coarse, Grid);
		if (!Dirty.Contains(Tile))
		{
			AddError(FString::Printf(TEXT("a tree at (%.1f, %.1f) lost its trunk-probe dirt but its tile %d was not marked"), X, Y, Tile));
			return false;
		}
	}
	AddInfo(FString::Printf(TEXT("tree probe reach: %d trees at a tile edge removed by dirt 4 m away, each tile marked"), Tested));
	return Tested >= 3;
}

SC_TEST(DirtyRangeArithmetic)
{
	// Vertex and texel rects against the world-interval formulas of plan C scatter 3.5, evaluated in double precision.
	FScRng Rng(5);
	bool bOk = true;
	for (int32 Trial = 0; Trial < 400 && bOk; ++Trial)
	{
		const int32 E = (Trial % 2) ? 160 : 96;
		const int32 Size = (Trial % 3 == 0) ? 16 : ((Trial % 3 == 1) ? 32 : 64);
		const FScatterTileGrid TG = MakeScatterTileGrid(E, Size);
		const int32 X0 = Rng.Range(0, 2 * E);
		const int32 Y0 = Rng.Range(0, 2 * E);
		const FTerrainRect VR(X0, Y0, FMath::Min(2 * E + 1, X0 + Rng.Range(1, 30)), FMath::Min(2 * E + 1, Y0 + Rng.Range(1, 30)));
		const int64 Apron = (Trial % 2) ? 163840 : 327680;
		const double A = static_cast<double>(Apron) / 65536.0;
		const FScatterTileRange R = ScatterTileRangeForVertexRect(TG, E, VR, Apron);
		const double LoX = VR.X0 - E - 1 - A;
		const double HiX = VR.X1 - E + A;
		const int32 WantMinX = FMath::Max(static_cast<int32>(std::floor(LoX / Size)), TG.Min);
		const int32 WantMaxX = FMath::Min(static_cast<int32>(std::floor(HiX / Size)), TG.Min + TG.Count - 1);
		if (R.MinX != WantMinX || R.MaxX != WantMaxX)
		{
			AddError(FString::Printf(TEXT("vertex range x: got [%d, %d], want [%d, %d]"), R.MinX, R.MaxX, WantMinX, WantMaxX));
			bOk = false;
		}
		const FTerrainRect TR(X0, Y0, FMath::Min(4 * E, X0 + Rng.Range(1, 40)), FMath::Min(4 * E, Y0 + Rng.Range(1, 40)));
		const FScatterTileRange RT = ScatterTileRangeForSplatRect(TG, E, TR, Apron);
		const double TLo = TR.X0 / 2.0 - E - 0.5 - A;
		const double THi = TR.X1 / 2.0 - E + 0.5 + A;
		if (RT.MinX != FMath::Max(static_cast<int32>(std::floor(TLo / Size)), TG.Min) || RT.MaxX != FMath::Min(static_cast<int32>(std::floor(THi / Size)), TG.Min + TG.Count - 1))
		{
			AddError(FString::Printf(TEXT("splat range x: got [%d, %d]"), RT.MinX, RT.MaxX));
			bOk = false;
		}
	}
	if (!ScatterTileRangeForVertexRect(MakeScatterTileGrid(160, 32), 160, FTerrainRect(), 163840).IsEmpty())
	{
		AddError(TEXT("an empty rect gave tiles"));
		bOk = false;
	}
	const FScatterTileGrid TG160 = MakeScatterTileGrid(160, 32);
	const FScatterTileGrid TG80 = MakeScatterTileGrid(160, 80);
	if (TG160.Count != 10 || TG160.Min != -5 || TG80.Count != 4 || TG80.Min != -2)
	{
		AddError(FString::Printf(TEXT("tile tables at E = 160: fine %d from %d, coarse %d from %d (want 10 from -5, 4 from -2)"), TG160.Count, TG160.Min, TG80.Count, TG80.Min));
		bOk = false;
	}
	return bOk;
}

// ---- tilt-only sculpt ---------------------------------------------------------------------------------------------------------

SC_TEST(TiltOnlySculpt)
{
	// A sculpt that changes an instance's cell gradient but not its zq changes its record (gradient is part of it), its hash, its edit script and
	// the instance's up axis: the gradient is in the record, so tilt cannot be missed.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 40, 64, 14, false);
	FScatterPalette P;
	TArray<FScatterRecord> Old;
	ReferenceRecords(HF, P, 32, 80, Old);
	const int32 E = HF.HalfExtentM();
	int32 Tested = 0;
	for (int32 Pick = 0; Pick < Old.Num() && Tested < 4; Pick += 37)
	{
		const FScatterRecord& R = Old[Pick];
		if (R.Class != static_cast<uint8>(EScatterClass::Grass))
		{
			continue;
		}
		const int64 U = static_cast<int64>(R.XQ) + static_cast<int64>(E) * 65536;
		const int64 V = static_cast<int64>(R.YQ) + static_cast<int64>(E) * 65536;
		const int32 CX = static_cast<int32>(U >> 16);
		const int32 CY = static_cast<int32>(V >> 16);
		const int64 FU = U & 0xFFFF;
		const int64 FV = V & 0xFFFF;
		if (FU < 12000 || FV < 12000 || FU > 53000 || FV > 53000 || CX < 2 || CY < 2 || CX > 2 * E - 3 || CY > 2 * E - 3)
		{
			continue;
		}
		const bool bUpper = FV >= FU;
		const int64 HBL = HeightToQ16(HF.GetHeight(CX, CY));
		const int64 HBR = HeightToQ16(HF.GetHeight(CX + 1, CY));
		const int64 HTL = HeightToQ16(HF.GetHeight(CX, CY + 1));
		const int64 HTR = HeightToQ16(HF.GetHeight(CX + 1, CY + 1));
		// Find (t, s) changing the triangle's two edge differences while the surface at the point keeps its Q16 value.
		const FScatterSnapshot OldSnap = MakeFullSnapshot(HF);
		const int64 OldZ = SurfaceQ16(OldSnap, R.XQ, R.YQ);
		bool bFound = false;
		FTerrainHeightfield Tilted = HF;
		for (int64 T = 800; T < 4000 && !bFound; T += 7)
		{
			const int64 S0 = -((FU * T) / FV);
			for (int64 S = S0 - 1; S <= S0 + 1 && !bFound; ++S)
			{
				FTerrainHeightfield Try = HF;
				if (bUpper)
				{
					const int64 NTL = HBL + (HTL - HBL) + S;
					const int64 NTR = NTL + (HTR - HTL) + T;
					Try.SetHeight(CX, CY + 1, static_cast<float>(static_cast<double>(NTL) / 65536.0));
					Try.SetHeight(CX + 1, CY + 1, static_cast<float>(static_cast<double>(NTR) / 65536.0));
				}
				else
				{
					const int64 NBR = HBL + (HBR - HBL) + T;
					const int64 NTR = NBR + (HTR - HBR) + S;
					Try.SetHeight(CX + 1, CY, static_cast<float>(static_cast<double>(NBR) / 65536.0));
					Try.SetHeight(CX + 1, CY + 1, static_cast<float>(static_cast<double>(NTR) / 65536.0));
				}
				const FScatterSnapshot NewSnap = MakeFullSnapshot(Try);
				int32 GX0 = 0;
				int32 GY0 = 0;
				int32 GX1 = 0;
				int32 GY1 = 0;
				CellGradientQ16(OldSnap, R.XQ, R.YQ, GX0, GY0);
				CellGradientQ16(NewSnap, R.XQ, R.YQ, GX1, GY1);
				if (SurfaceQ16(NewSnap, R.XQ, R.YQ) == OldZ && (GX0 != GX1 || GY0 != GY1))
				{
					Tilted = Try;
					bFound = true;
				}
			}
		}
		if (!bFound)
		{
			continue;
		}
		// Regenerate the tile in both fields and find the same candidate.
		TArray<FScatterRecord> New;
		ReferenceRecords(Tilted, P, 32, 80, New);
		FScatterUnitState NewState;
		NewState.Records = New;
		NewState.RebuildSorted();
		const int32 NI = NewState.Find(R.Key());
		if (NI == INDEX_NONE)
		{
			continue;  // the small gradient change moved its acceptance probability below its roll: try another record
		}
		const FScatterRecord& N = New[NI];
		++Tested;
		if (N.ZQ != R.ZQ)
		{
			AddError(FString::Printf(TEXT("the tilt-only construction moved zq (%d -> %d)"), R.ZQ, N.ZQ));
			return false;
		}
		if (N.GXQ == R.GXQ && N.GYQ == R.GYQ)
		{
			AddError(TEXT("gradient did not change"));
			return false;
		}
		if (N == R || HashRecord(N) == HashRecord(R))
		{
			AddError(TEXT("a tilt-only sculpt left the record and its hash unchanged"));
			return false;
		}
		const FScatterInstance IA = BuildInstance(R, P);
		const FScatterInstance IB = BuildInstance(N, P);
		const double QD = std::fabs(IA.Quat[0] - IB.Quat[0]) + std::fabs(IA.Quat[1] - IB.Quat[1]) + std::fabs(IA.Quat[2] - IB.Quat[2]) + std::fabs(IA.Quat[3] - IB.Quat[3]);
		const double UD = std::fabs(IA.Up[0] - IB.Up[0]) + std::fabs(IA.Up[1] - IB.Up[1]);
		if (QD < 1e-6 || UD < 1e-6 || IA.Custom[2] == IB.Custom[2])
		{
			AddError(FString::Printf(TEXT("the aligned instance did not follow the tilt (quat diff %.3g, up diff %.3g)"), QD, UD));
			return false;
		}
		// The keyed script carries it as an in-place update.
		FScatterUnitState OldState;
		OldState.Records = Old;
		OldState.RebuildSorted();
		TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> Next;
		const FScatterEditScript Script = BuildEditScript(OldState, New, Next);
		bool bHasUpdate = false;
		for (const FScatterUpdate& U2 : Script.Updates)
		{
			bHasUpdate |= (U2.Record.Key() == R.Key() && U2.Record == N);
		}
		if (!bHasUpdate)
		{
			AddError(TEXT("the edit script has no update for the tilted record"));
			return false;
		}
	}
	if (Tested < 3)
	{
		AddError(FString::Printf(TEXT("only %d grass records were suitable for the tilt-only construction (want >= 3)"), Tested));
		return false;
	}
	// A real Smooth stroke: every record that survives it in a cell whose gradient changed (recomputed independently from the two snapshots)
	// carries the new gradient, so its hash and its built instance change and the keyed script updates it in place. (A smooth almost always
	// moves zq too; the zq-unchanged case is the constructed one above, and the count is printed.)
	FTerrainHeightfield Sm = HF;
	for (int32 T = 0; T < 3; ++T)
	{
		FTerrainBrush::ApplyTick(Sm, ScParams(ETerrainBrushMode::Smooth, 30.0f, 70.0f, 0, 0.0f), 4.0f + T, -6.0f, nullptr);
	}
	TArray<FScatterRecord> New2;
	ReferenceRecords(Sm, P, 32, 80, New2);
	FScatterUnitState New2State;
	New2State.Records = New2;
	New2State.RebuildSorted();
	FScatterUnitState OldAll;
	OldAll.Records = Old;
	OldAll.RebuildSorted();
	TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> Next2;
	const FScatterEditScript Script2 = BuildEditScript(OldAll, New2, Next2);
	FScatterUnitState Updated;
	for (const FScatterUpdate& U2 : Script2.Updates)
	{
		Updated.Records.Add(U2.Record);
	}
	Updated.RebuildSorted();
	const FScatterSnapshot SnapA = MakeFullSnapshot(HF);
	const FScatterSnapshot SnapB = MakeFullSnapshot(Sm);
	int32 Tilted = 0;
	int32 TiltedSameZ = 0;
	int32 Bad = 0;
	for (const FScatterRecord& R : Old)
	{
		const int32 NI = New2State.Find(R.Key());
		if (NI == INDEX_NONE)
		{
			continue;
		}
		int32 GX0 = 0;
		int32 GY0 = 0;
		int32 GX1 = 0;
		int32 GY1 = 0;
		CellGradientQ16(SnapA, R.XQ, R.YQ, GX0, GY0);
		CellGradientQ16(SnapB, R.XQ, R.YQ, GX1, GY1);
		if (GX0 == GX1 && GY0 == GY1)
		{
			continue;
		}
		const FScatterRecord& N = New2[NI];
		++Tilted;
		TiltedSameZ += (N.ZQ == R.ZQ) ? 1 : 0;
		const FScatterInstance IA = BuildInstance(R, P);
		const FScatterInstance IB = BuildInstance(N, P);
		const bool bFine = ScatterGridOfClass(static_cast<EScatterClass>(R.Class)) == EScatterGrid::Fine;
		const bool bInstanceMoved = bFine ? (IA.Custom[2] != IB.Custom[2] || IA.Custom[3] != IB.Custom[3]) : true;
		const int32 UI = Updated.Find(R.Key());
		if (N.GXQ != GX1 || N.GYQ != GY1 || HashRecord(N) == HashRecord(R) || !bInstanceMoved || UI == INDEX_NONE || !(Updated.Records[UI] == N))
		{
			if (++Bad <= 5)
			{
				AddError(FString::Printf(TEXT("smooth: record (class %d, cell %d, %d) in a tilted cell did not follow (gradient %d,%d want %d,%d)"), R.Class, R.IX, R.IY, N.GXQ, N.GYQ, GX1, GY1));
			}
		}
	}
	AddInfo(FString::Printf(TEXT("tilt-only: %d constructed cases; smooth stroke: %d surviving records in tilted cells (%d with unchanged zq), all updated"), Tested, Tilted, TiltedSameZ));
	if (Tilted < 50)
	{
		AddError(FString::Printf(TEXT("the smooth stroke tilted too few records (%d, %d with unchanged zq)"), Tilted, TiltedSameZ));
		return false;
	}
	return Bad == 0;
}

// ---- edit script ----------------------------------------------------------------------------------------------------------

SC_TEST(EditScriptProperty)
{
	// Random old and new record sets: the ISM model ends with exactly the new set, removals run strictly descending, and the worker's next state is
	// the model's index order.
	FScRng Rng(404);
	TArray<FScatterRecord> Pool;
	for (int32 I = 0; I < 400; ++I)
	{
		FScatterRecord R;
		R.Class = static_cast<uint8>(Rng.Range(0, 3));
		R.Mesh = 0;
		R.Stream = static_cast<uint16>(11 + R.Class);
		R.IX = I - 200;
		R.IY = (I * 7) % 53;
		R.XQ = I * 1000;
		R.YQ = I * 77;
		R.ZQ = I;
		Pool.Add(R);
	}
	bool bOk = true;
	for (int32 Iter = 0; Iter < 400 && bOk; ++Iter)
	{
		TArray<FScatterRecord> OldRecs;
		TArray<FScatterRecord> NewRecs;
		for (const FScatterRecord& R : Pool)
		{
			const uint32 Roll = Rng.Next() % 100;
			if (Roll < 30)
			{
				OldRecs.Add(R);  // removed
			}
			else if (Roll < 55)
			{
				NewRecs.Add(R);  // added
			}
			else if (Roll < 75)
			{
				OldRecs.Add(R);
				NewRecs.Add(R);  // unchanged
			}
			else if (Roll < 90)
			{
				OldRecs.Add(R);
				FScatterRecord M = R;
				M.ZQ += 5 + static_cast<int32>(Rng.Next() % 100);
				NewRecs.Add(M);  // changed in place
			}
		}
		if (Iter % 50 == 0)
		{
			OldRecs.Reset();  // an empty old state
		}
		if (Iter % 50 == 1)
		{
			NewRecs.Reset();  // everything removed
		}
		// Shuffle the old records' order (ISM order is arbitrary), then build the state.
		for (int32 I = OldRecs.Num() - 1; I > 0; --I)
		{
			const int32 J = Rng.Range(0, I);
			const FScatterRecord Tmp = OldRecs[I];
			OldRecs[I] = OldRecs[J];
			OldRecs[J] = Tmp;
		}
		FScatterUnitState Old;
		Old.Records = OldRecs;
		Old.RebuildSorted();
		std::sort(NewRecs.GetData(), NewRecs.GetData() + NewRecs.Num(), [](const FScatterRecord& A, const FScatterRecord& B) { return A.Key() < B.Key(); });
		TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> Next;
		const FScatterEditScript Script = BuildEditScript(Old, NewRecs, Next);
		FFakeIsm Ism;
		Ism.Fill(Old);
		Ism.Apply(Script);
		for (int32 I = 1; I < Script.Removes.Num(); ++I)
		{
			if (Script.Removes[I] >= Script.Removes[I - 1])
			{
				AddError(TEXT("removals are not strictly descending"));
				bOk = false;
			}
		}
		if (!Next.IsValid() || !ScRecordsEqual(Ism.Items, Next->Records))
		{
			AddError(FString::Printf(TEXT("iteration %d: the model's order differs from the worker's next state"), Iter));
			bOk = false;
			continue;
		}
		TArray<FScatterRecord> Got = Ism.Items;
		std::sort(Got.GetData(), Got.GetData() + Got.Num(), [](const FScatterRecord& A, const FScatterRecord& B) { return A.Key() < B.Key(); });
		if (!ScRecordsEqual(Got, NewRecs))
		{
			AddError(FString::Printf(TEXT("iteration %d: the ISM model does not hold exactly the new set (%d vs %d)"), Iter, Got.Num(), NewRecs.Num()));
			bOk = false;
		}
		for (const FScatterUpdate& U : Script.Updates)
		{
			if (Script.Removes.Contains(U.Index))
			{
				AddError(TEXT("an updated index is also removed"));
				bOk = false;
			}
		}
		// A second diff from the next state against the same records produces nothing.
		TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> Next2;
		const FScatterEditScript Again = BuildEditScript(*Next, NewRecs, Next2);
		if (!Again.IsEmpty())
		{
			AddError(TEXT("diffing the new state against itself produced a non-empty script"));
			bOk = false;
		}
	}
	return bOk;
}

SC_TEST(SchedulerFakeExecutor)
{
	// Fake executor (jobs finish in a seeded random order after random delays), a one-unit-per-frame budget that forces partial applies, new dirt
	// while tiles are Busy, an epoch bump mid-flight, strokes open and closed: after quiescence the ISM model equals the reference, no tile was
	// dispatched while Busy, and every dispatched job ended in exactly one counter.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 32, 15, true);
	FTerrainUndo Undo;
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	FScatterSchedulerConfig Cfg = ScConfig(64, 32, 80, 2);
	Cfg.DuringStrokeMs = 100.0;
	Cfg.CasterDuringStrokeMs = 0.0;
	FScLive Live;
	Live.Init(HF, P, Cfg);
	FScRng Rng(9001);
	Live.Sch.BumpEpochAndDirtyAll();
	bool bOk = true;
	int32 Checkpoints = 0;
	for (int32 Round = 0; Round < 14 && bOk; ++Round)
	{
		const float X = Rng.Between(-55.0f, 55.0f);
		const float Y = Rng.Between(-55.0f, 55.0f);
		FTerrainBrushParams BP = (Round % 3 == 2) ? ScParams(ETerrainBrushMode::Paint, 40.0f, 100.0f, 1 + (Round % 3), 0.0f)
												  : ScParams(Round % 2 ? ETerrainBrushMode::Lower : ETerrainBrushMode::Raise, 30.0f + Round * 4.0f, 80.0f, 0, 0.0f);
		Undo.BeginStroke();
		Live.Sch.SetStrokeOpen(true);
		const int32 Ticks = Rng.Range(6, 14);
		for (int32 T = 0; T < Ticks; ++T)
		{
			const FTerrainTickResult R = FTerrainBrush::ApplyTick(HF, BP, X + T, Y, &Undo);
			Live.MarkTick(R);
			Live.Frame(Rng, 1.0, 2);
			Live.DispatchFrame(X, Y);
		}
		Undo.EndStroke(HF);
		Live.Sch.SetStrokeOpen(false);
		if (Round == 5 || Round == 9)
		{
			// The epoch moves while jobs are in flight and results wait (a load, or enable again).
			Live.Sch.BumpEpochAndDirtyAll();
		}
		// Drain frame by frame with the one-unit budget; more dirt arrives while tiles are Busy.
		for (int32 F = 0; F < 6; ++F)
		{
			Live.Frame(Rng, 1.0, 1);
			Live.DispatchFrame(X, Y);
		}
		if (Round % 4 == 1 && Undo.CanUndo())
		{
			FTerrainEditDelta D;
			Undo.Undo(HF, D);
			Live.MarkDelta(D);
		}
		if (!Live.Settle(Rng, 1.0))
		{
			AddError(FString::Printf(TEXT("round %d did not settle"), Round));
			bOk = false;
			break;
		}
		const FScatterHash Ref = ScRef(HF, P);
		++Checkpoints;
		if (!(Live.FoldIsms() == Ref) || !(Live.Sch.FoldApplied() == Ref))
		{
			AddError(FString::Printf(TEXT("round %d: after quiescence the ISM model differs from the reference"), Round));
			bOk = false;
		}
	}
	for (const FString& E : Live.Errors)
	{
		AddError(E);
		bOk = false;
	}
	const FScatterCounters& C = Live.Sch.GetCounters();
	AddInfo(FString::Printf(TEXT("scheduler: %d checkpoints, dispatched %lld applied %lld identical %lld discarded %lld mid-stroke %lld stale redispatch %lld in-flight max %d busy max %d units %lld"),
		Checkpoints, C.Dispatched, C.Applied, C.SkippedIdentical, C.DiscardedEpoch, C.MidStrokeDispatches, C.StaleResultRedispatches, C.InFlightMax, C.BusyMax, C.UnitsApplied));
	if (C.Dispatched != C.Applied + C.SkippedIdentical + C.DiscardedEpoch + C.Cancelled)
	{
		AddError(TEXT("dispatched != applied + skipped_identical + discarded_epoch + cancelled"));
		bOk = false;
	}
	if (C.InFlightMax > 2 || C.InFlightMax < 1 || C.MidStrokeDispatches == 0 || C.DiscardedEpoch == 0)
	{
		AddError(TEXT("the run did not exercise the in-flight cap, the mid-stroke path and the epoch discard"));
		bOk = false;
	}
	if (Live.Sch.NumBusy() != 0 || Live.Sch.NumInFlight() != 0 || Live.Pending.Num() != 0)
	{
		AddError(TEXT("busy or in-flight work remains at quiescence"));
		bOk = false;
	}
	return bOk;
}

SC_TEST(SchedulerStrokeRulesAndCancel)
{
	// Coarse tiles wait for the stroke end, fine tiles go at most every DuringStrokeMs, nearest the brush first; CancelAll counts what was left.
	FScatterScheduler Sch;
	FScatterSchedulerConfig Cfg = ScConfig(160, 32, 80, 3);
	Cfg.DuringStrokeMs = 150.0;
	Cfg.CasterDuringStrokeMs = 0.0;
	Sch.Init(Cfg);
	Sch.BumpEpochAndDirtyAll();
	Sch.SetStrokeOpen(true);
	TArray<FScatterDispatch> Got;
	Sch.Dispatch(0.0, 150.0, 150.0, [&Got](const FScatterDispatch& D) { Got.Add(D); });
	bool bOk = true;
	if (Got.Num() != 3)
	{
		AddError(FString::Printf(TEXT("expected 3 dispatches (the in-flight cap), got %d"), Got.Num()));
		return false;
	}
	for (const FScatterDispatch& D : Got)
	{
		if (D.Grid != EScatterGrid::Fine || !D.bMidStroke)
		{
			AddError(TEXT("a coarse tile (or a non-mid-stroke flag) during a stroke"));
			bOk = false;
		}
	}
	// Nearest the brush first: the three tiles around (150, 150) are the ones whose centres are closest.
	const FScatterTileGrid TG = Sch.GetTileGrid(EScatterGrid::Fine);
	const double D0 = std::fabs((TG.TXOf(Got[0].TileIndex) + 0.5) * 32.0 - 150.0) + std::fabs((TG.TYOf(Got[0].TileIndex) + 0.5) * 32.0 - 150.0);
	if (D0 > 32.0 + 1e-9)
	{
		AddError(TEXT("the first dispatched tile is not the nearest to the brush"));
		bOk = false;
	}
	// Those tiles are Busy: another Dispatch cannot start them; with the cap reached nothing starts.
	int32 More = Sch.Dispatch(10.0, 150.0, 150.0, [](const FScatterDispatch&) {});
	if (More != 0)
	{
		AddError(TEXT("dispatched beyond the in-flight cap"));
		bOk = false;
	}
	Sch.CancelAll();
	for (const FScatterDispatch& D : Got)
	{
		FScatterTileResult R;
		R.Grid = D.Grid;
		R.TileIndex = D.TileIndex;
		R.Gen = D.Gen;
		R.Epoch = D.Epoch;
		Sch.PostResult(MoveTemp(R));
	}
	Sch.PollResults();
	const FScatterCounters& C = Sch.GetCounters();
	if (C.Dispatched != 3 || C.Cancelled != 3 || Sch.NumBusy() != 0 || Sch.NumInFlight() != 0)
	{
		AddError(FString::Printf(TEXT("cancel accounting: dispatched %lld cancelled %lld busy %d in flight %d"), C.Dispatched, C.Cancelled, Sch.NumBusy(), Sch.NumInFlight()));
		bOk = false;
	}
	if (Sch.Dispatch(1000.0, 0.0, 0.0, [](const FScatterDispatch&) {}) != 0)
	{
		AddError(TEXT("dispatched after CancelAll"));
		bOk = false;
	}
	return bOk;
}

SC_TEST(SchedulerDisable)
{
	// scatter 0 while tiles are Busy: one result ready (not applied), one job in flight, more tiles dirty. After Disable + ClearUnitStates + the ISMs
	// cleared, marks are ignored and nothing dispatches; once the straggler is polled nothing is pending, nothing is dirty, the applied fold is
	// empty and the counters balance. Enable again (BumpEpochAndDirtyAll) settles to the reference.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 32, 21, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	FScLive Live;
	Live.Init(HF, P, ScConfig(64, 32, 80, 2));
	FScRng Rng(77);
	Live.Sch.BumpEpochAndDirtyAll();
	if (!Live.Settle(Rng, 0.0))
	{
		AddError(TEXT("the first fill did not settle"));
		return false;
	}
	bool bOk = true;
	// A stroke dirties tiles; two jobs go out, one finishes and is polled (ready, not applied), one stays in flight.
	Live.MarkTick(FTerrainBrush::ApplyTick(HF, ScParams(ETerrainBrushMode::Raise, 60.0f, 100.0f, 0, 0.0f), 5.0f, -3.0f, nullptr));
	Live.DispatchFrame(5.0, -3.0);
	if (Live.Pending.Num() != 2)
	{
		AddError(FString::Printf(TEXT("expected 2 jobs in flight, got %d"), Live.Pending.Num()));
		return false;
	}
	Live.Complete(0);
	Live.Sch.PollResults();
	const int32 DirtyBefore = Live.Sch.NumDirty();
	if (Live.Sch.NumReady() != 1 || Live.Sch.NumInFlight() != 1 || DirtyBefore < 3)
	{
		AddError(FString::Printf(TEXT("precondition: ready %d in flight %d dirty %d"), Live.Sch.NumReady(), Live.Sch.NumInFlight(), DirtyBefore));
		return false;
	}
	Live.DisableAndClear();
	if (Live.Sch.IsEnabled() || Live.Sch.NumReady() != 0 || Live.Sch.NumDirty() != 0 || Live.Sch.NumBusy() != 1 || !Live.Sch.HasPendingWork())
	{
		AddError(FString::Printf(TEXT("right after Disable: enabled %d ready %d dirty %d busy %d pending %d (want 0, 0, 0, 1, 1)"), Live.Sch.IsEnabled() ? 1 : 0, Live.Sch.NumReady(),
			Live.Sch.NumDirty(), Live.Sch.NumBusy(), Live.Sch.HasPendingWork() ? 1 : 0));
		bOk = false;
	}
	// More edits while disabled: ignored; no dispatch.
	Live.MarkTick(FTerrainBrush::ApplyTick(HF, ScParams(ETerrainBrushMode::Paint, 40.0f, 100.0f, 1, 0.0f), -20.0f, 20.0f, nullptr));
	if (Live.Sch.NumDirty() != 0 || Live.DispatchFrame(-20.0, 20.0) != 0)
	{
		AddError(TEXT("a disabled scheduler marked or dispatched a tile"));
		bOk = false;
	}
	// The straggler finishes; the owner keeps polling and applying while disabled.
	while (Live.Pending.Num() > 0)
	{
		Live.Complete(0);
	}
	for (int32 F = 0; F < 2; ++F)
	{
		Live.Frame(Rng, 1.0, 0);
		Live.DispatchFrame(0.0, 0.0);
	}
	const FScatterCounters& C = Live.Sch.GetCounters();
	if (Live.Sch.HasPendingWork() || Live.Sch.NumDirty() != 0 || Live.Sch.NumBusy() != 0 || Live.Sch.NumInFlight() != 0 || Live.Pending.Num() != 0)
	{
		AddError(FString::Printf(TEXT("after the straggler: pending %d dirty %d busy %d in flight %d jobs %d"), Live.Sch.HasPendingWork() ? 1 : 0, Live.Sch.NumDirty(),
			Live.Sch.NumBusy(), Live.Sch.NumInFlight(), Live.Pending.Num()));
		bOk = false;
	}
	if (Live.Sch.FoldApplied().TotalCount() != 0 || Live.FoldIsms().TotalCount() != 0)
	{
		AddError(TEXT("instances came back while scatter was disabled"));
		bOk = false;
	}
	if (C.Dispatched != C.Applied + C.SkippedIdentical + C.DiscardedEpoch + C.Cancelled || C.DiscardedEpoch < 2)
	{
		AddError(FString::Printf(TEXT("counters: dispatched %lld applied %lld identical %lld discarded %lld cancelled %lld"), C.Dispatched, C.Applied, C.SkippedIdentical, C.DiscardedEpoch, C.Cancelled));
		bOk = false;
	}
	AddInfo(FString::Printf(TEXT("disable: %d dirty tiles, 1 ready and 1 in flight at disable; afterwards 0 dirty, 0 busy, nothing pending; discarded %lld"), DirtyBefore, C.DiscardedEpoch));
	// Enable again: everything regenerates and equals the reference of the edited map.
	Live.Sch.BumpEpochAndDirtyAll();
	if (!Live.Settle(Rng, 1.0))
	{
		AddError(TEXT("enable after disable did not settle"));
		return false;
	}
	const FScatterHash Ref = ScRef(HF, P);
	if (!(Live.FoldIsms() == Ref) || !(Live.Sch.FoldApplied() == Ref))
	{
		AddError(TEXT("enable after disable: the live fold differs from the reference"));
		bOk = false;
	}
	for (const FString& E : Live.Errors)
	{
		AddError(E);
		bOk = false;
	}
	return bOk;
}

SC_TEST(SchedulerGameThreadMode)
{
	// ScatterThreads=0 (MaxInFlight 0): at most one tile per Dispatch call, generated inline, never counted in flight. Fill, strokes (open and
	// closed) and an undo settle to the reference with in_flight_max = 0 and gt_generations = dispatched > 0 (SX4's s1x_b path proof).
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 32, 22, true);
	FTerrainUndo Undo;
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	FScatterSchedulerConfig Cfg = ScConfig(64, 32, 80, 0);
	Cfg.DuringStrokeMs = 100.0;
	FScLive Live;
	Live.Init(HF, P, Cfg);
	FScRng Rng(31337);
	Live.Sch.BumpEpochAndDirtyAll();
	bool bOk = true;
	bool bLeftInFlight = false;
	int32 MaxPerCall = 0;
	auto Drain = [&](int32 MaxFrames) -> bool
	{
		for (int32 F = 0; F < MaxFrames; ++F)
		{
			Live.Frame(Rng, 0.0, 0);
			MaxPerCall = FMath::Max(MaxPerCall, Live.DispatchFrame(0.0, 0.0));
			bLeftInFlight |= (Live.Sch.NumInFlight() != 0 || Live.Pending.Num() != 0);
			if (!Live.Sch.HasPendingWork())
			{
				return true;
			}
		}
		return false;
	};
	if (!Drain(5000))
	{
		AddError(TEXT("the game-thread fill did not settle"));
		return false;
	}
	for (int32 S = 0; S < 6 && bOk; ++S)
	{
		const float X = Rng.Between(-50.0f, 50.0f);
		const float Y = Rng.Between(-50.0f, 50.0f);
		const FTerrainBrushParams BP = (S % 2) ? ScParams(ETerrainBrushMode::Paint, 30.0f, 100.0f, 1 + (S % 3), 0.0f) : ScParams(ETerrainBrushMode::Raise, 40.0f, 80.0f, 0, 0.0f);
		Undo.BeginStroke();
		Live.Sch.SetStrokeOpen(true);
		for (int32 T = 0; T < 8; ++T)
		{
			Live.MarkTick(FTerrainBrush::ApplyTick(HF, BP, X + T, Y, &Undo));
			Live.Frame(Rng, 0.0, 0);
			MaxPerCall = FMath::Max(MaxPerCall, Live.DispatchFrame(X, Y));
			Live.Clock += 120.0;
		}
		Undo.EndStroke(HF);
		Live.Sch.SetStrokeOpen(false);
		if (S == 3 && Undo.CanUndo())
		{
			FTerrainEditDelta D;
			Undo.Undo(HF, D);
			Live.MarkDelta(D);
		}
		if (!Drain(5000))
		{
			AddError(FString::Printf(TEXT("stroke %d did not settle in the game-thread mode"), S));
			return false;
		}
		const FScatterHash Ref = ScRef(HF, P);
		if (!(Live.FoldIsms() == Ref) || !(Live.Sch.FoldApplied() == Ref))
		{
			AddError(FString::Printf(TEXT("stroke %d: the game-thread live fold differs from the reference"), S));
			bOk = false;
		}
	}
	const FScatterCounters& C = Live.Sch.GetCounters();
	AddInfo(FString::Printf(TEXT("game-thread mode: dispatched %lld gt_generations %lld in-flight max %d max per call %d mid-stroke %lld"), C.Dispatched, C.GtGenerations, C.InFlightMax,
		MaxPerCall, C.MidStrokeDispatches));
	if (C.InFlightMax != 0 || C.GtGenerations == 0 || C.GtGenerations != C.Dispatched || C.GtGenerations != Live.InlineJobs || MaxPerCall != 1 || C.MidStrokeDispatches == 0)
	{
		AddError(TEXT("game-thread mode: want in_flight_max 0, gt_generations = dispatched = inline jobs > 0, one tile per call, mid-stroke dispatches > 0"));
		bOk = false;
	}
	if (bLeftInFlight)
	{
		AddError(TEXT("the game-thread mode left a job in flight"));
		bOk = false;
	}
	if (C.Dispatched != C.Applied + C.SkippedIdentical + C.DiscardedEpoch + C.Cancelled)
	{
		AddError(TEXT("counters do not balance"));
		bOk = false;
	}
	for (const FString& E : Live.Errors)
	{
		AddError(E);
		bOk = false;
	}
	return bOk;
}

// ---- placement rules ----------------------------------------------------------------------------------------------------------

SC_TEST(MonotoneThinning)
{
	// Painting dirt, rock or snow on the grass removes classes and never adds or moves them. Exempt: rocks (rock paint) and shrubs (the hedge term).
	// 1,000 random single-stroke paints, each from the same grass base (compounded paints can legitimately trade one layer for another).
	FTerrainHeightfield Base;
	ScMakeMap(Base, 64, 32, 16, false);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	const int32 Fine = 32;
	const int32 Coarse = 80;
	// Cache the base records per tile.
	TArray<FScatterTileRecords> BaseRecs[ScatterGridCount];
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const FScatterTileGrid TG = MakeScatterTileGrid(64, Grid == EScatterGrid::Fine ? Fine : Coarse);
		BaseRecs[G].SetNum(TG.NumTiles());
		for (int32 I = 0; I < TG.NumTiles(); ++I)
		{
			const FScatterTileKey Key = ScatterTileKeyAt(Grid, TG, I);
			EvaluateTile(MakeSnapshot(Base, Key), P, Key, BaseRecs[G][I]);
		}
	}
	FScRng Rng(1234);
	int64 Removed = 0;
	int64 RockGains = 0;
	int64 ShrubGains = 0;
	for (int32 Trial = 0; Trial < 1000; ++Trial)
	{
		FTerrainHeightfield Work = Base;
		const int32 Layer = 1 + (Trial % 3);
		const float X = Rng.Between(-60.0f, 60.0f);
		const float Y = Rng.Between(-60.0f, 60.0f);
		FTerrainTickResult Union;
		const int32 Ticks = Rng.Range(1, 3);
		for (int32 T = 0; T < Ticks; ++T)
		{
			const FTerrainTickResult R = FTerrainBrush::ApplyTick(Work, ScParams(ETerrainBrushMode::Paint, Rng.Between(5.0f, 60.0f), Rng.Between(10.0f, 100.0f), Layer, 0.0f), X, Y, nullptr);
			Union.SplatRect.Union(R.SplatRect);
		}
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			const EScatterGrid Grid = static_cast<EScatterGrid>(G);
			const FScatterTileGrid TG = MakeScatterTileGrid(64, Grid == EScatterGrid::Fine ? Fine : Coarse);
			TArray<int32> Tiles;
			ScatterDirtyTiles(TG, Grid, 64, FTerrainRect(), Union.SplatRect, Tiles);
			for (const int32 I : Tiles)
			{
				const FScatterTileKey Key = ScatterTileKeyAt(Grid, TG, I);
				FScatterTileRecords After;
				EvaluateTile(MakeSnapshot(Work, Key), P, Key, After);
				for (int32 M = 0; M < ScatterMeshCount; ++M)
				{
					FScatterUnitState Before;
					Before.Records = BaseRecs[G][I].PerMesh[M];
					Before.RebuildSorted();
					for (const FScatterRecord& R : After.PerMesh[M])
					{
						if (Before.Find(R.Key()) == INDEX_NONE)
						{
							// May still exist in another mesh (conifer vs broadleaf do not move with paint, so no): a gain is a gain.
							if (R.Class == static_cast<uint8>(EScatterClass::Rock))
							{
								++RockGains;
							}
							else if (R.Class == static_cast<uint8>(EScatterClass::Shrub))
							{
								++ShrubGains;
							}
							else
							{
								AddError(FString::Printf(TEXT("trial %d: class %d gained an instance under dirt/rock/snow paint (cell %d, %d)"), Trial, R.Class, R.IX, R.IY));
								return false;
							}
						}
						else if (Before.Records[Before.Find(R.Key())].XQ != R.XQ || Before.Records[Before.Find(R.Key())].YQ != R.YQ)
						{
							AddError(FString::Printf(TEXT("trial %d: an instance moved in XY under paint"), Trial));
							return false;
						}
					}
					FScatterUnitState AfterState;
					AfterState.Records = After.PerMesh[M];
					AfterState.RebuildSorted();
					Removed += BaseRecs[G][I].PerMesh[M].Num() - (AfterState.Records.Num() - 0);
				}
			}
		}
	}
	AddInfo(FString::Printf(TEXT("monotone: 1,000 paints; net records removed %lld; rock gains %lld; shrub gains %lld (exempt classes)"), Removed, RockGains, ShrubGains));
	if (Removed < 10000)
	{
		AddError(FString::Printf(TEXT("the paints removed only %lld records net: the thinning was not exercised"), Removed));
		return false;
	}
	return true;
}

SC_TEST(HedgePositive)
{
	// A painted dirt line raises shrubs in its verge band (the 3 m ring sees dirt while the shrub's own dirt weight is low) and leaves none in its core.
	// Dirt falls off smoothly from 255 (2 m from the axis) to 0 (8 m), so the verge is 2-3 m wide. Other shrub terms are zeroed so every gain is the hedge
	// term. Positive: the Fh gate made neutral (the path rule itself). Gate: with the default Fh ramp (0.50-0.70), a stretch where Fh < 0.45 gets no hedge.
	const int32 Half = 24;
	auto PaintLine = [](FTerrainHeightfield& HF, float CY)
	{
		for (int32 TY = 0; TY < HF.SplatSize(); ++TY)
		{
			for (int32 TX = 0; TX < HF.SplatSize(); ++TX)
			{
				const float WY = (static_cast<float>(TY) + 0.5f) * 0.5f - static_cast<float>(HF.HalfExtentM());
				const float D = std::fabs(WY - CY);
				const float W = D <= 2.0f ? 255.0f : (D >= 8.0f ? 0.0f : 255.0f * (8.0f - D) / 6.0f);
				uint8* T = HF.SplatTexel(TX, TY);
				T[1] = static_cast<uint8>(W + 0.5f);
				T[0] = static_cast<uint8>(255 - T[1]);
				T[2] = 0;
				T[3] = 0;
			}
		}
	};
	auto Count = [](const FTerrainHeightfield& HF, const FScatterPalette& P, float CY, int32& Verge, int32& Core, int32& OutOfBand)
	{
		const FScatterSnapshot S = MakeFullSnapshot(HF);
		TArray<FScatterRecord> Recs;
		ReferenceRecords(HF, P, 32, 80, Recs);
		Verge = Core = OutOfBand = 0;
		for (const FScatterRecord& R : Recs)
		{
			if (R.Class != static_cast<uint8>(EScatterClass::Shrub))
			{
				continue;
			}
			if (std::fabs(ScM(R.XQ)) > 20.0)
			{
				continue;  // away from the line's ends
			}
			const double D = std::fabs(ScM(R.YQ) - CY);
			if (SplatQ8Bilinear(S, R.XQ, R.YQ, 1) >= 24)
			{
				++Core;
			}
			else
			{
				++Verge;
				if (D < 6.5 || D > 10.5)
				{
					++OutOfBand;
				}
			}
		}
	};
	FScatterPalette P;
	P.SetByName("ShrubWoodP", 0.0);
	P.SetByName("ShrubFsP", 0.0);
	FTerrainHeightfield Flat;
	Flat.Init(64, 32);
	// (a) The path rule with the Fh gate neutral.
	FScatterPalette Neutral = P;
	Neutral.SetByName("ShrubFhLo", 0.0);
	Neutral.SetByName("ShrubFhHi", 0.001);
	int32 V0 = 0;
	int32 C0 = 0;
	int32 O0 = 0;
	Count(Flat, Neutral, 0.0f, V0, C0, O0);
	FTerrainHeightfield Painted = Flat;
	PaintLine(Painted, 0.0f);
	int32 V1 = 0;
	int32 C1 = 0;
	int32 O1 = 0;
	Count(Painted, Neutral, 0.0f, V1, C1, O1);
	AddInfo(FString::Printf(TEXT("hedge (Fh neutral): shrubs along a %d m dirt line before %d / %d (verge / core), after %d / %d, %d outside the 6.5-10.5 m band"), 2 * Half, V0, C0, V1, C1, O1));
	bool bOk = true;
	if (V0 != 0 || C0 != 0 || V1 < 1 || C1 != 0 || O1 != 0)
	{
		AddError(TEXT("hedge rule: the painted line must raise shrubs in its verge band (none before), none in its core"));
		bOk = false;
	}
	// (b) The Fh gate: find a low-Fh stretch (Fh < 0.45 along y = CY for |x| <= 20) with the default ramp.
	float LowY = 1000.0f;
	for (uint32 Seed = 0; Seed < 400 && LowY > 999.0f; ++Seed)
	{
		P.Seed = (Seed == 0) ? FScatterPalette().Seed : Seed * 104729u + 1u;
		for (int32 Y = -40; Y <= 40 && LowY > 999.0f; Y += 4)
		{
			bool bLow = true;
			for (int32 X = -24; X <= 24 && bLow; X += 2)
			{
				for (int32 DY = -10; DY <= 10 && bLow; DY += 4)
				{
					bLow = ScatterFieldQ16(P, EScatterField::Hedge, static_cast<int64>(X) * 65536, static_cast<int64>(Y + DY) * 65536) < static_cast<int64>(0.45 * 65536);
				}
			}
			if (bLow)
			{
				LowY = static_cast<float>(Y);
			}
		}
	}
	if (LowY > 999.0f)
	{
		AddError(TEXT("no low-Fh stretch found in 400 seeds"));
		return false;
	}
	FTerrainHeightfield Painted2 = Flat;
	PaintLine(Painted2, LowY);
	int32 V2 = 0;
	int32 C2 = 0;
	int32 O2 = 0;
	Count(Painted2, P, LowY, V2, C2, O2);
	// The same stretch with the gate neutral does grow a hedge, so the gate is what removed it.
	FScatterPalette Neutral2 = P;
	Neutral2.SetByName("ShrubFhLo", 0.0);
	Neutral2.SetByName("ShrubFhHi", 0.001);
	int32 V3 = 0;
	int32 C3 = 0;
	int32 O3 = 0;
	Count(Painted2, Neutral2, LowY, V3, C3, O3);
	AddInfo(FString::Printf(TEXT("hedge Fh gate (seed %u, line y = %.0f, Fh < 0.45): default gate %d / %d, gate neutral %d / %d (verge / core)"), P.Seed, LowY, V2, C2, V3, C3));
	if (V2 != 0 || C2 != 0 || V3 < 1)
	{
		AddError(TEXT("hedge Fh gate: a low-Fh stretch must get no hedge, and the same stretch with the gate off must"));
		bOk = false;
	}
	// (c) The plan's case with the default palette and gate (only the wood and clump terms zeroed so every gain is the hedge's): a dirt path
	// painted with the brush (8 m, strength 15, a tick per metre: centre dirt about 150, the dirt-48 contour about 2.7 m from the axis) along a stretch where Fh >= 0.7 (the default
	// seed first). Shrubs appear on its verges, none in its core (own dirt >= 24). The rule's geometry: the 3 m ring must see dirt in the
	// 48-160 band, so every verge shrub stands within 3.5 m outside the path's dirt-48 contour (3 m ring plus bilinear reach). Distances to
	// the dirt-48 and dirt-128 contours are printed.
	FScatterPalette Def;
	Def.SetByName("ShrubWoodP", 0.0);
	Def.SetByName("ShrubFsP", 0.0);
	const int32 HalfLen = 20;
	int32 LineX = 0;
	int32 LineY = 0;
	bool bFound = false;
	for (uint32 SeedTry = 0; SeedTry < 200 && !bFound; ++SeedTry)
	{
		Def.Seed = (SeedTry == 0) ? FScatterPalette().Seed : SeedTry * 7919u + 3u;
		for (int32 Y = -40; Y <= 40 && !bFound; Y += 2)
		{
			for (int32 X = -36; X <= 36 && !bFound; X += 2)
			{
				bool bHigh = true;
				for (int32 DX = -HalfLen - 3; DX <= HalfLen + 3 && bHigh; DX += 1)
				{
					for (int32 DY = -6; DY <= 6 && bHigh; DY += 1)
					{
						bHigh = ScatterFieldQ16(Def, EScatterField::Hedge, static_cast<int64>(X + DX) * 65536, static_cast<int64>(Y + DY) * 65536) >= Def.Get(EScatterParam::ShrubFhHi);
					}
				}
				if (bHigh)
				{
					LineX = X;
					LineY = Y;
					bFound = true;
				}
			}
		}
	}
	if (!bFound)
	{
		AddError(TEXT("no 46 m stretch with Fh >= 0.7 found in 200 seeds"));
		return false;
	}
	FTerrainHeightfield Path = Flat;
	for (int32 K = -HalfLen; K <= HalfLen; ++K)
	{
		FTerrainBrush::ApplyTick(Path, ScParams(ETerrainBrushMode::Paint, 8.0f, 15.0f, 1, 0.0f), static_cast<float>(LineX + K), static_cast<float>(LineY), nullptr);
	}
	const FScatterSnapshot PathSnap = MakeFullSnapshot(Path);
	{
		FString Prof;
		for (int32 K = 0; K <= 14; ++K)
		{
			Prof += FString::Printf(TEXT("%d "), SplatQ8Bilinear(PathSnap, static_cast<int64>(LineX) * 65536, static_cast<int64>(LineY) * 65536 + K * 32768, 1));
		}
		AddInfo(FString::Printf(TEXT("hedge path dirt profile from the axis, every 0.5 m: %s"), *Prof));
	}
	TArray<FScatterRecord> PathRecs;
	ReferenceRecords(Path, Def, 32, 80, PathRecs);
	TArray<FScatterRecord> FlatRecs;
	ReferenceRecords(Flat, Def, 32, 80, FlatRecs);
	auto NearLine = [&](const FScatterRecord& R)
	{
		return R.Class == static_cast<uint8>(EScatterClass::Shrub) && std::fabs(ScM(R.XQ) - LineX) <= HalfLen && std::fabs(ScM(R.YQ) - LineY) <= 8.0;
	};
	int32 Before = 0;
	for (const FScatterRecord& R : FlatRecs)
	{
		Before += NearLine(R) ? 1 : 0;
	}
	// Distance from a shrub to the contour where the path's dirt reaches Level, walking toward the axis in 0.05 m steps.
	auto EdgeDistance = [&](const FScatterRecord& R, int32 Level)
	{
		const int64 Dir = (R.YQ > static_cast<int64>(LineY) * 65536) ? -1 : 1;
		for (int32 Step = 0; Step <= 200; ++Step)
		{
			if (SplatQ8Bilinear(PathSnap, R.XQ, static_cast<int64>(R.YQ) + Dir * static_cast<int64>(Step) * 3277, 1) >= Level)
			{
				return Step * 0.05;
			}
		}
		return 1.0e9;
	};
	int32 Verge = 0;
	int32 Core = 0;
	int32 OutBand = 0;
	double Min48 = 1.0e9;
	double Max48 = -1.0e9;
	double Min128 = 1.0e9;
	double Max128 = -1.0e9;
	for (const FScatterRecord& R : PathRecs)
	{
		if (!NearLine(R))
		{
			continue;
		}
		if (SplatQ8Bilinear(PathSnap, R.XQ, R.YQ, 1) >= Def.Get(EScatterParam::ShrubHedgeOwnHi))
		{
			++Core;
			continue;
		}
		++Verge;
		const double D48 = EdgeDistance(R, 48);
		const double D128 = EdgeDistance(R, 128);
		Min48 = FMath::Min(Min48, D48);
		Max48 = FMath::Max(Max48, D48);
		Min128 = FMath::Min(Min128, D128);
		Max128 = FMath::Max(Max128, D128);
		OutBand += (D48 > 3.5) ? 1 : 0;
	}
	AddInfo(FString::Printf(TEXT("hedge default gate (seed %u, %d m path at (%d, %d), Fh >= 0.7 along it): shrubs before %d; after %d verge / %d core; verge distance %.2f-%.2f m from the dirt-48 contour, %.2f-%.2f m from the dirt-128 contour; %d beyond 3.5 m"),
		Def.Seed, 2 * HalfLen, LineX, LineY, Before, Verge, Core, Min48, Max48, Min128, Max128, OutBand));
	if (Before != 0 || Verge < 3 || Core != 0 || OutBand != 0)
	{
		AddError(TEXT("hedge default gate: a painted path on a high-Fh stretch must raise >= 3 verge shrubs within 3.5 m of its edge and none in its core"));
		bOk = false;
	}
	return bOk;
}

SC_TEST(ExclusionRules)
{
	// Independent recomputation from the heightfield (not the generator's helpers) of the hard exclusions.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 160, 32, 17, true);
	// A steep ridge too, so slope rules are exercised.
	for (int32 Y = 0; Y < HF.Width(); ++Y)
	{
		for (int32 X = 0; X < HF.Width(); ++X)
		{
			if (X > 250 && X < 270)
			{
				HF.SetHeight(X, Y, HF.GetHeight(X, Y) + 3.0f * static_cast<float>(X - 250));
			}
		}
	}
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	TArray<FScatterRecord> Recs;
	ReferenceRecords(HF, P, 32, 80, Recs);
	const int32 E = HF.HalfExtentM();
	auto Weight = [&](const FScatterRecord& R, int32 Channel)
	{
		// Bilinear splat read straight from HF.Splat bytes in double precision, rounded like the generator's byte.
		const double SX = (ScM(R.XQ) + E) * 2.0 - 0.5;
		const double SY = (ScM(R.YQ) + E) * 2.0 - 0.5;
		const int32 TX = static_cast<int32>(std::floor(SX));
		const int32 TY = static_cast<int32>(std::floor(SY));
		const double FX = SX - TX;
		const double FY = SY - TY;
		auto At = [&](int32 X, int32 Y) { return static_cast<double>(HF.SplatTexel(FMath::Clamp(X, 0, HF.SplatSize() - 1), FMath::Clamp(Y, 0, HF.SplatSize() - 1))[Channel]); };
		return (At(TX, TY) * (1 - FX) + At(TX + 1, TY) * FX) * (1 - FY) + (At(TX, TY + 1) * (1 - FX) + At(TX + 1, TY + 1) * FX) * FY;
	};
	auto SlopeG2 = [&](const FScatterRecord& R)
	{
		// The cell triangle's gradient from the float heights (double precision), tan^2 of the slope.
		const double U = ScM(R.XQ) + E;
		const double V = ScM(R.YQ) + E;
		const int32 CX = static_cast<int32>(std::floor(U));
		const int32 CY = static_cast<int32>(std::floor(V));
		const double FU = U - CX;
		const double FV = V - CY;
		const double BL = HF.GetHeight(CX, CY);
		const double BR = HF.GetHeight(CX + 1, CY);
		const double TL = HF.GetHeight(CX, CY + 1);
		const double TR = HF.GetHeight(CX + 1, CY + 1);
		const double GX = FV >= FU ? TR - TL : BR - BL;
		const double GY = FV >= FU ? TL - BL : TR - BR;
		return GX * GX + GY * GY;
	};
	const double T35 = static_cast<double>(LookShared::RockSlopeThr) / 4294967296.0;
	const double T30 = static_cast<double>(P.Get(EScatterParam::StHi)) / 4294967296.0;
	int32 Checked[ScatterClassCount] = {0};
	for (const FScatterRecord& R : Recs)
	{
		const EScatterClass C = static_cast<EScatterClass>(R.Class);
		++Checked[R.Class];
		const bool bGrassLike = (C == EScatterClass::Grass || C == EScatterClass::NearCard || C == EScatterClass::Flower);
		if (bGrassLike)
		{
			if (Weight(R, 0) < 95.0)
			{
				AddError(FString::Printf(TEXT("class %d at wG %.1f < 96"), R.Class, Weight(R, 0)));
				return false;
			}
			if (C != EScatterClass::Flower && SlopeG2(R) > T35 * 1.001)
			{
				AddError(FString::Printf(TEXT("class %d on a slope past 35 degrees (tan^2 %.3f)"), R.Class, SlopeG2(R)));
				return false;
			}
		}
		if (bGrassLike || C == EScatterClass::Tussock || C == EScatterClass::Fern)
		{
			if (Weight(R, 3) >= 64.5)
			{
				AddError(FString::Printf(TEXT("class %d at wS %.1f >= 64"), R.Class, Weight(R, 3)));
				return false;
			}
		}
		if (C == EScatterClass::Tree || C == EScatterClass::Sapling)
		{
			if (Weight(R, 1) > 32.5)
			{
				AddError(FString::Printf(TEXT("trunk at wD %.1f > 32"), Weight(R, 1)));
				return false;
			}
			if (SlopeG2(R) > T30 * 1.001)
			{
				AddError(FString::Printf(TEXT("tree on a slope past 30 degrees (tan^2 %.3f)"), SlopeG2(R)));
				return false;
			}
		}
	}
	AddInfo(FString::Printf(TEXT("exclusions checked on %d records: grass %d near card %d flower %d tussock %d tree %d sapling %d shrub %d fern %d rock %d"), Recs.Num(), Checked[0], Checked[3], Checked[2], Checked[1], Checked[4],
		Checked[5], Checked[6], Checked[7], Checked[8]));
	return Checked[0] > 1000 && Checked[4] > 20;
}

SC_TEST(ZRuleExact)
{
	// Every record's z equals its class's rule on the integer surface exactly, and its stored gradient is the cell triangle's.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 96, 32, 18, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	TArray<FScatterRecord> Recs;
	ReferenceRecords(HF, P, 32, 80, Recs);
	const FScatterSnapshot S = MakeFullSnapshot(HF);
	for (const FScatterRecord& R : Recs)
	{
		const int64 Z = ScatterRecordZRuleQ16(S, P, R);
		if (Z != R.ZQ)
		{
			AddError(FString::Printf(TEXT("class %d record at (%d, %d): zq %d, rule %lld"), R.Class, R.XQ, R.YQ, R.ZQ, Z));
			return false;
		}
		int32 GX = 0;
		int32 GY = 0;
		CellGradientQ16(S, R.XQ, R.YQ, GX, GY);
		if (GX != R.GXQ || GY != R.GYQ)
		{
			AddError(TEXT("a record's stored gradient is not its cell triangle's"));
			return false;
		}
	}
	return Recs.Num() > 1000;
}

SC_TEST(NearCardAndLevels)
{
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 32, 19, false);
	FScatterPalette L0;
	FScatterPalette L1;
	L1.Level = EScatterLevel::L1;
	const FScatterHash H0 = ScRef(HF, L0);
	const FScatterHash H1 = ScRef(HF, L1);
	const int64 NC = static_cast<int64>(EScatterClass::NearCard);
	if (H0.Classes[NC].Count != 0 || H1.Classes[NC].Count < 500)
	{
		AddError(FString::Printf(TEXT("near card: L0 %lld (want 0), L1 %lld (want > 500)"), H0.Classes[NC].Count, H1.Classes[NC].Count));
		return false;
	}
	// All other classes are identical between levels.
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		if (C != NC && (H0.Classes[C].Count != H1.Classes[C].Count || H0.Classes[C].Sum != H1.Classes[C].Sum))
		{
			AddError(FString::Printf(TEXT("class %d differs between levels"), C));
			return false;
		}
	}
	if (L0.ConfigFnv() == L1.ConfigFnv() || ScFnv(HF, L0) == ScFnv(HF, L1))
	{
		AddError(TEXT("config_fnv / scatter_fnv do not tell the levels apart"));
		return false;
	}
	if (ScatterMeshExistsAtLevel(EScatterMesh::NearCard, EScatterLevel::L0) || !ScatterMeshExistsAtLevel(EScatterMesh::NearCard, EScatterLevel::L1) || !ScatterMeshExistsAtLevel(EScatterMesh::GrassT0, EScatterLevel::L0))
	{
		AddError(TEXT("mesh slots by level"));
		return false;
	}
	return true;
}

SC_TEST(FieldCoverage)
{
	// Coverage at the default seed (plan C scatter 3.4): woodland (W' >= 0.62) 12-20 % of the playable interior and 60-90 % of the border ring;
	// flower drifts (F >= 0.70) 5-12 % of the meadow.
	const FScatterPalette P;
	int64 InN = 0;
	int64 InW = 0;
	int64 InF = 0;
	int64 MeadowN = 0;
	int64 RingN = 0;
	int64 RingW = 0;
	for (int32 Y = -159; Y < 160; ++Y)
	{
		for (int32 X = -159; X < 160; ++X)
		{
			const int64 XQ = static_cast<int64>(X) * 65536 + 32768;
			const int64 YQ = static_cast<int64>(Y) * 65536 + 32768;
			const bool bWood = ScatterFieldQ16(P, EScatterField::WoodlandBiased, XQ, YQ) >= P.Get(EScatterParam::TreeEdgeW);
			if (FMath::Abs(X) < 128 && FMath::Abs(Y) < 128)
			{
				++InN;
				InW += bWood ? 1 : 0;
				if (!bWood)
				{
					++MeadowN;
					InF += (ScatterFieldQ16(P, EScatterField::Drift, XQ, YQ) >= P.Get(EScatterParam::FlowerDriftLo)) ? 1 : 0;
				}
			}
			else
			{
				++RingN;
				RingW += bWood ? 1 : 0;
			}
		}
	}
	const double Wood = 100.0 * static_cast<double>(InW) / static_cast<double>(InN);
	const double Ring = 100.0 * static_cast<double>(RingW) / static_cast<double>(RingN);
	const double Drift = 100.0 * static_cast<double>(InF) / static_cast<double>(MeadowN);
	AddInfo(FString::Printf(TEXT("field coverage at seed %u: woodland %.1f %% of the interior (want 12-20), %.1f %% of the border ring (60-90), flower drift %.1f %% of the meadow (5-12)"), P.Seed, Wood, Ring, Drift));
	bool bOk = true;
	if (Wood < 12.0 || Wood > 20.0)
	{
		AddError(TEXT("woodland interior coverage out of range"));
		bOk = false;
	}
	if (Ring < 60.0 || Ring > 90.0)
	{
		AddError(TEXT("woodland border ring coverage out of range"));
		bOk = false;
	}
	if (Drift < 5.0 || Drift > 12.0)
	{
		AddError(TEXT("flower drift coverage out of range"));
		bOk = false;
	}
	return bOk;
}

SC_TEST(ClassTotalsAndFieldMap)
{
	// Per-class totals on a flat all-grass E = 160 map (plan C scatter 3.4 EST) and the field map THINX's coordinates are chosen from.
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	TArray<FScatterRecord> Recs;
	ReferenceRecords(HF, P, 32, 80, Recs);
	int64 Count[ScatterClassCount] = {0};
	for (const FScatterRecord& R : Recs)
	{
		++Count[R.Class];
	}
	const TCHAR* Names[ScatterClassCount] = {TEXT("grass"), TEXT("tussock"), TEXT("flower"), TEXT("near card"), TEXT("tree"), TEXT("sapling"), TEXT("shrub"), TEXT("fern"), TEXT("rock")};
	FString Line;
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		Line += FString::Printf(TEXT("%s %lld, "), Names[C], Count[C]);
	}
	AddInfo(FString::Printf(TEXT("flat all-grass E = 160 (L1) totals: %s total %d"), *Line, Recs.Num()));
	AddInfo(FString::Printf(TEXT("flat E = 160 L1 seed %u: config_fnv %016llx scatter_fnv %016llx"), P.Seed, static_cast<unsigned long long>(P.ConfigFnv()), static_cast<unsigned long long>(ScFnv(HF, P))));
	// EST of plan C scatter 3.4: floors at half the estimate, ceilings at 3x (flowers and rocks are bounded by their own tuning notes).
	struct FBand
	{
		int32 Class;
		int64 Lo;
		int64 Hi;
	};
	const FBand Bands[] = {{0, 75000, 450000}, {1, 1500, 9000}, {2, 4000, 24000}, {3, 17000, 105000}, {4, 750, 6000}, {7, 1500, 9000}, {8, 100, 600}};
	bool bOk = true;
	for (const FBand& B : Bands)
	{
		if (Count[B.Class] < B.Lo || Count[B.Class] > B.Hi)
		{
			AddError(FString::Printf(TEXT("class %s total %lld outside [%lld, %lld]"), Names[B.Class], Count[B.Class], B.Lo, B.Hi));
			bOk = false;
		}
	}
	if (Count[5] + Count[6] < 1250 || Count[5] + Count[6] > 7500)
	{
		AddError(FString::Printf(TEXT("shrubs + saplings %lld outside [1250, 7500]"), Count[5] + Count[6]));
		bOk = false;
	}
	// Field map: for THINX, the 10 m cells with the highest flower, tussock and tree yield inside a 10 m radius (flat map, 20 m grid).
	struct FSpot
	{
		int32 X;
		int32 Y;
		int32 N;
	};
	const EScatterClass Wanted[3] = {EScatterClass::Flower, EScatterClass::Tussock, EScatterClass::Tree};
	for (const EScatterClass W : Wanted)
	{
		FSpot Best[3] = {{0, 0, -1}, {0, 0, -1}, {0, 0, -1}};
		const double Radius = (W == EScatterClass::Tree) ? 12.0 : 5.0;
		for (int32 Y = -100; Y <= 100; Y += 10)
		{
			for (int32 X = -100; X <= 100; X += 10)
			{
				int32 N = 0;
				for (const FScatterRecord& R : Recs)
				{
					if (R.Class != static_cast<uint8>(W))
					{
						continue;
					}
					const double DX = ScM(R.XQ) - X;
					const double DY = ScM(R.YQ) - Y;
					if (DX * DX + DY * DY <= Radius * Radius)
					{
						++N;
					}
				}
				for (int32 K = 0; K < 3; ++K)
				{
					if (N > Best[K].N)
					{
						for (int32 J = 2; J > K; --J)
						{
							Best[J] = Best[J - 1];
						}
						Best[K] = {X, Y, N};
						break;
					}
				}
			}
		}
		AddInfo(FString::Printf(TEXT("THINX field map: %s yield within %.0f m of (x, y): (%d, %d) = %d, (%d, %d) = %d, (%d, %d) = %d"), Names[static_cast<int32>(W)], Radius, Best[0].X, Best[0].Y, Best[0].N,
			Best[1].X, Best[1].Y, Best[1].N, Best[2].X, Best[2].Y, Best[2].N));
	}
	return bOk;
}

SC_TEST(BuildInstanceBasics)
{
	FScatterPalette P;
	FScatterRecord R;
	R.Class = static_cast<uint8>(EScatterClass::Grass);
	R.Mesh = static_cast<uint8>(EScatterMesh::GrassT0);
	R.XQ = 65536 * 3 + 16384;
	R.YQ = -65536 * 2;
	R.ZQ = 65536 * 7;
	R.Scale = 4096 + 2048;
	R.ZScale = 4096 * 2;
	R.CD0 = 65535;
	R.CD1 = 0;
	R.Yaw = 0;
	FScatterInstance I = BuildInstance(R, P);
	bool bOk = true;
	auto Near = [&](const TCHAR* What, double A, double B, double Tol)
	{
		if (std::fabs(A - B) > Tol)
		{
			AddError(FString::Printf(TEXT("%s: got %.6f, want %.6f"), What, A, B));
			bOk = false;
		}
	};
	Near(TEXT("pos x"), I.Pos[0], 3.25, 1e-9);
	Near(TEXT("pos y"), I.Pos[1], -2.0, 1e-9);
	Near(TEXT("pos z"), I.Pos[2], 7.0, 1e-9);
	Near(TEXT("scale xy"), I.Scale[0], 1.5, 1e-9);
	Near(TEXT("scale z"), I.Scale[2], 2.0, 1e-9);
	Near(TEXT("flat up z"), I.Up[2], 1.0, 1e-9);
	Near(TEXT("flat quat w"), I.Quat[3], 1.0, 1e-9);
	Near(TEXT("custom 0"), I.Custom[0], 1.0, 1e-6);
	Near(TEXT("custom 1"), I.Custom[1], 0.0, 1e-6);
	Near(TEXT("custom 2"), I.Custom[2], 0.0, 1e-6);
	if (I.NumCustom != 4)
	{
		AddError(TEXT("fine grid has 4 custom floats"));
		bOk = false;
	}
	// A slope of 0.5 in x: align 0.5 for grass tilts the up axis halfway (normalised) toward the normal (-0.5, 0, 1).
	R.GXQ = 32768;
	I = BuildInstance(R, P);
	const double NL = std::sqrt(1.25);
	double Want[3] = {-0.5 / NL * 0.5, 0.0, 1.0 / NL * 0.5 + 0.5};
	const double WL = std::sqrt(Want[0] * Want[0] + Want[2] * Want[2]);
	Near(TEXT("aligned up x"), I.Up[0], Want[0] / WL, 1e-9);
	Near(TEXT("aligned up z"), I.Up[2], Want[2] / WL, 1e-9);
	Near(TEXT("custom normal x"), I.Custom[2], -0.5 / NL, 1e-6);
	double QL = 0.0;
	for (int32 K = 0; K < 4; ++K)
	{
		QL += I.Quat[K] * I.Quat[K];
	}
	Near(TEXT("quaternion length"), QL, 1.0, 1e-9);
	// The quaternion rotates Z onto the up axis: rotate (0,0,1) by q.
	{
		const double X = I.Quat[0], Y = I.Quat[1], Z = I.Quat[2], W = I.Quat[3];
		const double RX = 2 * (X * Z + W * Y);
		const double RY = 2 * (Y * Z - W * X);
		const double RZ = 1 - 2 * (X * X + Y * Y);
		Near(TEXT("rotated z x"), RX, I.Up[0], 1e-9);
		Near(TEXT("rotated z y"), RY, I.Up[1], 1e-9);
		Near(TEXT("rotated z z"), RZ, I.Up[2], 1e-9);
	}
	// A flower carries its colour index, a coarse record two floats.
	FScatterRecord F = R;
	F.Class = static_cast<uint8>(EScatterClass::Flower);
	F.Mesh = static_cast<uint8>(EScatterMesh::Flower);
	F.CD1 = 2;
	Near(TEXT("flower colour"), BuildInstance(F, P).Custom[1], 2.0, 1e-9);
	FScatterRecord T = R;
	T.Class = static_cast<uint8>(EScatterClass::Tree);
	T.Mesh = static_cast<uint8>(EScatterMesh::TreeBroadA);
	T.CD1 = 65535;
	const FScatterInstance IT = BuildInstance(T, P);
	Near(TEXT("tree custom count"), IT.NumCustom, 2, 0);
	Near(TEXT("tree custom 1"), IT.Custom[1], 1.0, 1e-6);
	Near(TEXT("tree upright"), IT.Up[2], 1.0, 1e-9);
	return bOk;
}

SC_TEST(RockTiltFrameConditioned)
{
	// A rock's hashed lean must not swing with the gradient's direction on near-flat ground: one gradient unit in any direction moves its up axis by about one
	// unit (1/65536 rad), never by up to twice the tilt angle (the tilt frame is world X projected onto the plane perpendicular to the aligned axis).
	FScatterPalette P;
	FScatterRecord R;
	R.Class = static_cast<uint8>(EScatterClass::Rock);
	R.Mesh = static_cast<uint8>(EScatterMesh::RockA);
	R.Scale = 4096;
	R.ZScale = 4096;
	bool bOk = true;
	auto Ang = [](const FScatterInstance& A, const FScatterInstance& B)
	{
		const double D = A.Up[0] * B.Up[0] + A.Up[1] * B.Up[1] + A.Up[2] * B.Up[2];
		return std::acos(FMath::Clamp(D, -1.0, 1.0));
	};
	static const int32 G[][2] = {{1, 0}, {-1, 0}, {0, 1}, {0, -1}, {1, 1}, {-1, -1}, {3, -2}};
	for (uint32 K = 0; K < 64; ++K)
	{
		R.Tilt = static_cast<uint16>(1024u + K * 1009u);
		R.Yaw = static_cast<uint16>(K * 7919u);
		R.GXQ = 0;
		R.GYQ = 0;
		const FScatterInstance Flat = BuildInstance(R, P);
		const double Lean = std::acos(FMath::Clamp(Flat.Up[2], -1.0, 1.0));
		if (Lean < 1e-4)
		{
			AddError(FString::Printf(TEXT("tilt %u gave no lean on flat ground"), R.Tilt));
			bOk = false;
		}
		for (const auto& D : G)
		{
			R.GXQ = D[0];
			R.GYQ = D[1];
			const double A = Ang(Flat, BuildInstance(R, P));
			if (A > 1e-4)
			{
				AddError(FString::Printf(TEXT("tilt %u yaw %u: gradient (%d,%d) moved the up axis by %.3g rad"), R.Tilt, R.Yaw, D[0], D[1], A));
				bOk = false;
			}
		}
	}
	return bOk;
}

// ---- palette, options, config ---------------------------------------------------------------------------------------------

SC_TEST(PaletteAndConfigFnv)
{
	FScatterPalette P;
	const uint64 Base = P.ConfigFnv();
	bool bOk = true;
	// Seed, level and every parameter move the hash.
	FScatterPalette Q = P;
	Q.Seed += 1;
	bOk &= (Q.ConfigFnv() != Base);
	Q = P;
	Q.Level = EScatterLevel::L1;
	bOk &= (Q.ConfigFnv() != Base);
	for (int32 I = 0; I < ScatterParamCount; ++I)
	{
		Q = P;
		Q.V[I] += 1;
		if (Q.ConfigFnv() == Base)
		{
			AddError(FString::Printf(TEXT("parameter %s does not move config_fnv"), ANSI_TO_TCHAR(ScatterParamInfo(static_cast<EScatterParam>(I)).Name)));
			bOk = false;
		}
	}
	// SetByName: units, ranges and errors.
	Q = P;
	if (Q.SetByName("GrassBaseP", 0.5) != EScatterSetResult::Ok || Q.Get(EScatterParam::GrassBaseP) != 32768)
	{
		AddError(TEXT("SetByName GrassBaseP 0.5 -> 32768"));
		bOk = false;
	}
	if (Q.SetByName("NoSuchParam", 1.0) != EScatterSetResult::UnknownName)
	{
		AddError(TEXT("an unknown name must be reported"));
		bOk = false;
	}
	if (Q.SetByName("GrassCellM", 3.0) != EScatterSetResult::OutOfRange || Q.SetByName("GrassCellM", 2.0) != EScatterSetResult::Ok)
	{
		AddError(TEXT("cell sizes must be 0.5, 1, 2 or 4 m"));
		bOk = false;
	}
	if (Q.SetByName("StHi", 30.0) != EScatterSetResult::Ok || FMath::Abs(Q.Get(EScatterParam::StHi) - P.Get(EScatterParam::StHi)) > 3)
	{
		AddError(FString::Printf(TEXT("SetByName slope degrees: StHi 30 deg gave %lld, default %lld"), Q.Get(EScatterParam::StHi), P.Get(EScatterParam::StHi)));
		bOk = false;
	}
	if (Q.SetByName("GLo", 300.0) != EScatterSetResult::OutOfRange || Q.SetByName("GLo", 100.0) != EScatterSetResult::Ok)
	{
		AddError(TEXT("byte parameters 0..255"));
		bOk = false;
	}
	// The defaults hold their ramp order (lo < hi) for the pairs the rules use.
	struct FPair
	{
		EScatterParam Lo;
		EScatterParam Hi;
	};
	const FPair Pairs[] = {{EScatterParam::GLo, EScatterParam::GHi}, {EScatterParam::SgLo, EScatterParam::SgHi}, {EScatterParam::StLo, EScatterParam::StHi}, {EScatterParam::HgLo, EScatterParam::HgHi},
		{EScatterParam::HtLo, EScatterParam::HtHi}, {EScatterParam::FlowerDriftLo, EScatterParam::FlowerDriftHi}, {EScatterParam::FlowerSlopeLo, EScatterParam::FlowerSlopeHi},
		{EScatterParam::FernSlopeLo, EScatterParam::FernSlopeHi}, {EScatterParam::RockSlopeLo, EScatterParam::RockSlopeHi}, {EScatterParam::RockFrLo, EScatterParam::RockFrHi},
		{EScatterParam::ShrubFsLo, EScatterParam::ShrubFsHi}, {EScatterParam::ShrubFhLo, EScatterParam::ShrubFhHi}, {EScatterParam::FernWLo, EScatterParam::FernWHi},
		{EScatterParam::TussockForestLo, EScatterParam::TussockForestPk}, {EScatterParam::TussockForestPk, EScatterParam::TussockForestHi},
		{EScatterParam::ShrubHedgeLo, EScatterParam::ShrubHedgePk}, {EScatterParam::ShrubHedgePk, EScatterParam::ShrubHedgeHi}, {EScatterParam::RockBandLo, EScatterParam::RockBandPk},
		{EScatterParam::RockBandPk, EScatterParam::RockBandHi}, {EScatterParam::SaplingLo, EScatterParam::SaplingPk}, {EScatterParam::SaplingPk, EScatterParam::SaplingHi}};
	for (const FPair& Pair : Pairs)
	{
		if (!(P.Get(Pair.Lo) < P.Get(Pair.Hi)))
		{
			AddError(FString::Printf(TEXT("ramp %s !< %s"), ANSI_TO_TCHAR(ScatterParamInfo(Pair.Lo).Name), ANSI_TO_TCHAR(ScatterParamInfo(Pair.Hi).Name)));
			bOk = false;
		}
	}
	// Slope defaults follow the material (SgHi = RockCos) and the plan's angles.
	if (P.Get(EScatterParam::SgHi) != LookShared::RockSlopeThr)
	{
		AddError(TEXT("the grass slope limit does not follow TerrainLookShared.h"));
		bOk = false;
	}
	// Density folds into the records.
	FTerrainHeightfield HF;
	ScMakeMap(HF, 48, 32, 20, false);
	FScatterPalette Half = P;
	Half.SetByName("Density", 0.5);
	const FScatterHash H1 = ScRef(HF, P);
	const FScatterHash H2 = ScRef(HF, Half);
	if (H2.Classes[0].Count >= H1.Classes[0].Count * 0.7 || H2.Classes[0].Count < H1.Classes[0].Count * 0.3)
	{
		AddError(FString::Printf(TEXT("Density 0.5 changed grass %lld -> %lld (want about half)"), H1.Classes[0].Count, H2.Classes[0].Count));
		bOk = false;
	}
	return bOk;
}

SC_TEST(PaletteOverridesAndValidate)
{
	// ApplyOverrides is all or nothing and runs Validate: reaches beyond the aprons, RockCellM != TreeCellM and a zero period are rejected and leave
	// the palette unchanged; the raw '#' form stores the integer itself (the build-independent form for slopes); the defaults validate.
	bool bOk = true;
	const FScatterPalette Defaults;
	if (const char* Rule = Defaults.Validate())
	{
		AddError(FString::Printf(TEXT("the default palette breaks a rule: %s"), ANSI_TO_TCHAR(Rule)));
		bOk = false;
	}
	struct FCase
	{
		const char* Spec;
		bool bAccept;
	};
	const FCase Cases[] = {
		{"GrassBaseP=0.5,TreeCoreP=0.2", true},
		{"StHi=#1000000000", true},
		{"GrassVergeRingM=2", true},
		{"GrassVergeRingM=2.5", false},
		{"TreeProbeM=4.5", true},
		{"TreeProbeM=6", false},
		{"ShrubHedgeRingM=5", false},
		{"TreeBaseProbeM=4.6", false},
		{"RockCellM=2", false},
		{"RockCellM=2,TreeCellM=2", true},
		{"TreeCellM=1", false},
		{"WWarpPeriodM=0", false},
		{"TPeriodM=0.5", false},
		{"NoSuch=1", false},
		{"GrassBaseP=abc", false},
		{"GrassBaseP=0.5,NoSuch=1", false},
		{"GrassCellM=3", false},
		{"GLo=#300", false},
	};
	for (const FCase& C : Cases)
	{
		FScatterPalette P;
		char Err[512];
		const int32 Errors = P.ApplyOverrides(C.Spec, Err, static_cast<int32>(sizeof(Err)));
		const bool bAccepted = Errors == 0;
		if (bAccepted != C.bAccept)
		{
			AddError(FString::Printf(TEXT("ApplyOverrides(\"%s\"): accepted %d, want %d (%s)"), ANSI_TO_TCHAR(C.Spec), bAccepted ? 1 : 0, C.bAccept ? 1 : 0, ANSI_TO_TCHAR(Err)));
			bOk = false;
		}
		if (!bAccepted && P.ConfigFnv() != Defaults.ConfigFnv())
		{
			AddError(FString::Printf(TEXT("ApplyOverrides(\"%s\") failed but changed the palette"), ANSI_TO_TCHAR(C.Spec)));
			bOk = false;
		}
		if (!bAccepted && Err[0] == 0)
		{
			AddError(FString::Printf(TEXT("ApplyOverrides(\"%s\") failed without a message"), ANSI_TO_TCHAR(C.Spec)));
			bOk = false;
		}
		if (bAccepted && P.Validate() != nullptr)
		{
			AddError(TEXT("an accepted palette does not validate"));
			bOk = false;
		}
	}
	FScatterPalette RawP;
	char Err[256];
	RawP.ApplyOverrides("StHi=#1000000000, GrassBaseP = 0.5", Err, static_cast<int32>(sizeof(Err)));
	if (RawP.Get(EScatterParam::StHi) != 1000000000 || RawP.Get(EScatterParam::GrassBaseP) != 32768)
	{
		AddError(TEXT("raw and spaced items were not stored exactly"));
		bOk = false;
	}
	// The warp periods are palette rows now: changing one moves config_fnv and the woodland field.
	FScatterPalette WarpP;
	WarpP.ApplyOverrides("WWarpPeriodM=32", Err, static_cast<int32>(sizeof(Err)));
	int32 Moved = 0;
	for (int32 K = 0; K < 64; ++K)
	{
		const int64 X = static_cast<int64>(K * 37 - 1000) * 65536 / 7;
		const int64 Y = static_cast<int64>(K * 53 - 900) * 65536 / 5;
		Moved += ScatterFieldQ16(WarpP, EScatterField::Woodland, X, Y) != ScatterFieldQ16(Defaults, EScatterField::Woodland, X, Y) ? 1 : 0;
	}
	if (WarpP.ConfigFnv() == Defaults.ConfigFnv() || Moved < 32)
	{
		AddError(FString::Printf(TEXT("WWarpPeriodM does not reach config_fnv or the woodland field (%d of 64 samples moved)"), Moved));
		bOk = false;
	}
	AddInfo(FString::Printf(TEXT("overrides: %d cases; WWarpPeriodM=32 moves %d of 64 woodland samples"), static_cast<int32>(sizeof(Cases) / sizeof(Cases[0])), Moved));
	return bOk;
}

SC_TEST(LookSharedMatchesMaterial)
{
	// The generated header's RockCos and RockBand follow Scripts/make_ground_material.py SCALARS (test_scatter_constants.py also regenerates the file and diffs it).
	FString Text;
	if (!ScLoadText(TEXT("Scripts/make_ground_material.py"), Text))
	{
		AddError(TEXT("Scripts/make_ground_material.py missing"));
		return false;
	}
	auto ReadScalar = [&Text](const TCHAR* Name, double& Out) -> bool
	{
		const FString Key = FString::Printf(TEXT("('%s', "), Name);
		const int32 At = Text.Find(*Key);
		if (At == INDEX_NONE)
		{
			return false;
		}
		const FString Rest = Text.Mid(At + Key.Len(), 24);
		Out = FCString::Atod(*Rest);
		return true;
	};
	double Cos = 0.0;
	double Band = 0.0;
	if (!ReadScalar(TEXT("RockCos"), Cos) || !ReadScalar(TEXT("RockBand"), Band))
	{
		AddError(TEXT("RockCos / RockBand not found in SCALARS"));
		return false;
	}
	bool bOk = true;
	if (LookShared::RockCosQ16 != ScQ(Cos) || LookShared::RockBandQ16 != ScQ(Band))
	{
		AddError(FString::Printf(TEXT("TerrainLookShared.h is stale: RockCos %.4f RockBand %.4f in the material, Q16 %lld / %lld in the header"), Cos, Band, LookShared::RockCosQ16, LookShared::RockBandQ16));
		bOk = false;
	}
	auto Thr = [](double C) { return (1.0 / (C * C) - 1.0) * 4294967296.0; };
	if (std::fabs(static_cast<double>(LookShared::RockSlopeThr) - Thr(Cos)) > 4.0 || std::fabs(static_cast<double>(LookShared::RockBandLoThr) - Thr(Cos + Band)) > 4.0
		|| std::fabs(static_cast<double>(LookShared::RockBandHiThr) - Thr(Cos - Band)) > 4.0)
	{
		AddError(TEXT("TerrainLookShared.h thresholds do not match (1/c^2 - 1) * 2^32"));
		bOk = false;
	}
	if (!(LookShared::RockBandLoThr < LookShared::RockSlopeThr && LookShared::RockSlopeThr < LookShared::RockBandHiThr))
	{
		AddError(TEXT("rock band thresholds out of order"));
		bOk = false;
	}
	return bOk;
}

// ---- text rules ---------------------------------------------------------------------------------------------------------------

namespace
{
	/** The text with comments removed (// and block) and the SCATTER_FP_BEGIN..END regions blanked, or the fenced text itself when bFenceOnly. */
	FString ScStrip(const FString& In, bool bDropFences)
	{
		FString Out;
		const int32 Len = In.Len();
		bool bInBlock = false;
		bool bInFence = false;
		int32 I = 0;
		while (I < Len)
		{
			const TCHAR C = In[I];
			const TCHAR N = (I + 1 < Len) ? In[I + 1] : TEXT(' ');
			if (bInBlock)
			{
				if (C == TEXT('*') && N == TEXT('/'))
				{
					bInBlock = false;
					I += 2;
				}
				else
				{
					++I;
				}
				continue;
			}
			if (C == TEXT('/') && N == TEXT('*'))
			{
				bInBlock = true;
				I += 2;
				continue;
			}
			if (C == TEXT('/') && N == TEXT('/'))
			{
				// A line comment: look for the fence markers in it, then skip to the end of the line.
				int32 J = I;
				while (J < Len && In[J] != TEXT('\n'))
				{
					++J;
				}
				const FString Comment = In.Mid(I, J - I);
				if (Comment.Contains(TEXT("SCATTER_FP_BEGIN")))
				{
					bInFence = true;
				}
				else if (Comment.Contains(TEXT("SCATTER_FP_END")))
				{
					bInFence = false;
				}
				I = J;
				continue;
			}
			if (!(bDropFences && bInFence))
			{
				Out.AppendChar(C);
			}
			++I;
		}
		return Out;
	}

	bool ScIsIdent(TCHAR C)
	{
		return (C >= TEXT('a') && C <= TEXT('z')) || (C >= TEXT('A') && C <= TEXT('Z')) || (C >= TEXT('0') && C <= TEXT('9')) || C == TEXT('_');
	}

	/** Whole-word occurrence (identifier boundaries). */
	bool ScHasWord(const FString& Text, const TCHAR* Word)
	{
		const FString W(Word);
		int32 From = 0;
		while (true)
		{
			const int32 At = Text.Find(*W, ESearchCase::CaseSensitive, ESearchDir::FromStart, From);
			if (At == INDEX_NONE)
			{
				return false;
			}
			const bool bLeft = (At == 0) || !ScIsIdent(Text[At - 1]);
			const bool bRight = (At + W.Len() >= Text.Len()) || !ScIsIdent(Text[At + W.Len()]);
			if (bLeft && bRight)
			{
				return true;
			}
			From = At + 1;
		}
	}

	const TCHAR* const GScGeneratorFiles[] = {TEXT("Source/ChimeraTerrain/Data/TerrainScatterTypes.h"), TEXT("Source/ChimeraTerrain/Data/TerrainScatterMath.h"),
		TEXT("Source/ChimeraTerrain/Data/TerrainScatterMath.cpp"), TEXT("Source/ChimeraTerrain/Data/TerrainScatterPalette.h"), TEXT("Source/ChimeraTerrain/Data/TerrainScatterPalette.cpp"),
		TEXT("Source/ChimeraTerrain/Data/TerrainScatter.h"), TEXT("Source/ChimeraTerrain/Data/TerrainScatter.cpp"), TEXT("Source/ChimeraTerrain/Data/TerrainScatterScheduler.cpp"),
		TEXT("Source/ChimeraTerrain/Data/TerrainLookShared.h")};
}

SC_TEST(TextScanIncludes)
{
	// The generator never includes TerrainSimExport.h or any Game/, Render/ or UObject header.
	bool bOk = true;
	for (const TCHAR* Rel : GScGeneratorFiles)
	{
		FString Text;
		if (!ScLoadText(Rel, Text))
		{
			AddError(FString::Printf(TEXT("%s missing"), Rel));
			bOk = false;
			continue;
		}
		const int32 Len = Text.Len();
		int32 Pos = 0;
		while (Pos < Len)
		{
			int32 End = Pos;
			while (End < Len && Text[End] != TEXT('\n'))
			{
				++End;
			}
			const FString Line = Text.Mid(Pos, End - Pos);
			Pos = End + 1;
			if (!Line.Contains(TEXT("#include")))
			{
				continue;
			}
			const bool bAllowed = Line.Contains(TEXT("\"CoreMinimal.h\"")) || Line.Contains(TEXT("\"Data/")) || Line.Contains(TEXT("\"Async/ParallelFor.h\"")) || Line.Contains(TEXT("\"HAL/CriticalSection.h\"")) || Line.Contains(TEXT("\"Misc/ScopeLock.h\"")) || Line.Contains(TEXT("\"Misc/Parse.h\""))
				|| Line.Contains(TEXT("\"Misc/CString.h\"")) || Line.Contains(TEXT("<algorithm>")) || Line.Contains(TEXT("<cmath>")) || Line.Contains(TEXT("<cstring>"));
			const bool bBanned = Line.Contains(TEXT("TerrainSimExport")) || Line.Contains(TEXT("Game/")) || Line.Contains(TEXT("Render/")) || Line.Contains(TEXT("UObject"))
				|| Line.Contains(TEXT("GameFramework")) || Line.Contains(TEXT("Engine/")) || Line.Contains(TEXT("Components/"));
			if (!bAllowed || bBanned)
			{
				AddError(FString::Printf(TEXT("%s: include not allowed in the generator layer: %s"), Rel, *Line));
				bOk = false;
			}
		}
	}
	return bOk;
}

SC_TEST(TextScanForbidden)
{
	// No float or double in any accept, select, position or Z decision (they appear only inside SCATTER_FP regions, and the scheduler's timing file is
	// exempt); no engine random, clock, frame counter, camera, mesh bounds, TMap or TSet anywhere in the generator layer.
	bool bOk = true;
	const TCHAR* const Banned[] = {TEXT("FRandomStream"), TEXT("HashCombine"), TEXT("GFrameCounter"), TEXT("FPlatformTime"), TEXT("DeltaTime"), TEXT("TMap"), TEXT("TSet"), TEXT("Camera"),
		TEXT("GetBounds"), TEXT("FBoxSphereBounds"), TEXT("FMath::Rand"), TEXT("FMath::FRand"), TEXT("GetWorld")};
	int32 FenceRegions = 0;
	for (const TCHAR* Rel : GScGeneratorFiles)
	{
		FString Text;
		if (!ScLoadText(Rel, Text))
		{
			AddError(FString::Printf(TEXT("%s missing"), Rel));
			bOk = false;
			continue;
		}
		const FString AllCode = ScStrip(Text, false);
		for (const TCHAR* Word : Banned)
		{
			const bool bFound = FString(Word).Contains(TEXT("::")) ? AllCode.Contains(Word) : ScHasWord(AllCode, Word);
			if (bFound)
			{
				AddError(FString::Printf(TEXT("%s: forbidden token %s"), Rel, Word));
				bOk = false;
			}
		}
		const FString File(Rel);
		// Only the scheduler's file (timing in ms, no record decision) is exempt as a whole; every header fences its float or double regions.
		const bool bFpExempt = File.Contains(TEXT("TerrainScatterScheduler.cpp"));
		FenceRegions += Text.Contains(TEXT("SCATTER_FP_BEGIN")) ? 1 : 0;
		if (!bFpExempt)
		{
			const FString Decisions = ScStrip(Text, true);
			if (ScHasWord(Decisions, TEXT("float")) || ScHasWord(Decisions, TEXT("double")))
			{
				AddError(FString::Printf(TEXT("%s: float or double outside a SCATTER_FP region"), Rel));
				bOk = false;
			}
		}
	}
	if (FenceRegions < 6)
	{
		AddError(FString::Printf(TEXT("expected the FP fence in Types.h, Math.h, Palette.h, Palette.cpp, TerrainScatter.h and Scatter.cpp, found %d files"), FenceRegions));
		bOk = false;
	}
	return bOk;
}

SC_TEST(HlslAndAssetTextRules)
{
	// Placeholder for S3's shader text tests: nothing to scan yet when the scatter shader does not exist; once it does, PerInstanceRandom and
	// derivatives in the opacity-mask path are banned (plan C scatter 3.7). Passing means "no scatter HLSL violates them".
	FString Text;
	if (!ScLoadText(TEXT("Scripts/scatter/ChimeraScatter.hlsl"), Text))
	{
		// Vacuous until S3 creates the shader at exactly this path; S3's acceptance makes the file required (then a missing file fails).
		AddInfo(TEXT("VACUOUS: Scripts/scatter/ChimeraScatter.hlsl not present yet (task S3 creates it at this path); no shader text was checked"));
		return true;
	}
	const FString Code = ScStrip(Text, false);
	bool bOk = true;
	if (ScHasWord(Code, TEXT("PerInstanceRandom")))
	{
		AddError(TEXT("ChimeraScatter.hlsl reads PerInstanceRandom (index-order dependent, F25)"));
		bOk = false;
	}
	if (ScHasWord(Code, TEXT("ddx")) || ScHasWord(Code, TEXT("ddy")) || ScHasWord(Code, TEXT("fwidth")))
	{
		AddError(TEXT("ChimeraScatter.hlsl uses ddx/ddy/fwidth (zero in the Nanite programmable raster, F24)"));
		bOk = false;
	}
	return bOk;
}

#if !defined(CHIMERA_SCATTER_STANDALONE)

// ---- options and the real ISM -----------------------------------------------------------------------------------------------------

SC_TEST(OptionsParsing)
{
	bool bOk = true;
	auto Expect = [&](const TCHAR* What, bool bCond)
	{
		if (!bCond)
		{
			AddError(FString::Printf(TEXT("options: %s"), What));
			bOk = false;
		}
	};
	// Scatter is on by default only without a script (the mouse controller's precedent).
	Expect(TEXT("default on without a script"), FScatterOptions::FromCommandLine(TEXT("")).bEnabled);
	Expect(TEXT("script turns it off"), !FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScript=S1")).bEnabled);
	Expect(TEXT("script with explicit 1 turns it on"), FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScript=S1 -ChimeraTerrainScatter=1")).bEnabled);
	Expect(TEXT("explicit 0 without a script"), !FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatter=0")).bEnabled);
	// Clamps.
	const FScatterOptions C = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterThreads=9 -ChimeraTerrainScatterGovernor=11 -ChimeraTerrainScatterFineTileM=48 -ChimeraTerrainScatterCoarseTileM=100 -ChimeraTerrainScatterBudgetMs=0 -ChimeraTerrainScatterDensity=3 -ChimeraTerrainScatterDuringStroke=-5"));
	Expect(TEXT("threads clamp to 4"), C.Threads == 4);
	Expect(TEXT("governor clamps to 6"), C.Governor == 6);
	Expect(TEXT("fine tile 48 -> 32"), C.FineTileM == 32);
	Expect(TEXT("coarse tile 100 -> 80"), C.CoarseTileM == 80);
	Expect(TEXT("budget clamps up"), C.BudgetMs >= 0.05f);
	Expect(TEXT("density clamps to 1"), C.Palette.Get(EScatterParam::Density) == 65536);
	Expect(TEXT("during stroke clamps to 0"), C.DuringStrokeMs == 0);
	// Values.
	const FScatterOptions V = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterSeed=77 -ChimeraTerrainScatterLevel=L1 -ChimeraTerrainScatterLayers=grass,trees -ChimeraTerrainScatterMobility=movable -ChimeraTerrainScatterApply=clear -ChimeraTerrainScatterGrassNanite=0 -ChimeraTerrainScatterFineTileM=64 -ChimeraTerrainScatterCoarseTileM=160 -ChimeraTerrainScatterThreads=0 -ChimeraTerrainScatterDuringStroke=300 -ChimeraTerrainScatterCasterDuringStroke=150"));
	Expect(TEXT("seed"), V.Palette.Seed == 77);
	Expect(TEXT("level"), V.Palette.Level == EScatterLevel::L1);
	Expect(TEXT("layers mask grass + trees"), V.LayerMask == ((1u << static_cast<int32>(EScatterLayer::Grass)) | (1u << static_cast<int32>(EScatterLayer::Trees))));
	Expect(TEXT("mobility"), V.Mobility == TEXT("movable"));
	Expect(TEXT("apply clear"), V.ApplyMode == TEXT("clear"));
	Expect(TEXT("grass nanite off"), !V.bGrassNanite);
	Expect(TEXT("tiles 64 / 160"), V.FineTileM == 64 && V.CoarseTileM == 160);
	Expect(TEXT("threads 0"), V.Threads == 0);
	Expect(TEXT("during stroke 300, caster 150"), V.DuringStrokeMs == 300 && V.CasterDuringStrokeMs == 150);
	// Params.
	const FScatterOptions Pm = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterParams=GrassBaseP=0.5,TreeCoreP=0.2"));
	Expect(TEXT("params applied"), Pm.Palette.Get(EScatterParam::GrassBaseP) == 32768 && Pm.ParamsError.IsEmpty());
	const FScatterPalette Defaults;
	const FScatterOptions Bad = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterParams=NoSuch=1,GrassBaseP=0.5"));
	Expect(TEXT("unknown param reported, nothing applied (all or nothing)"), Bad.ParamsError.Contains(TEXT("NoSuch")) && Bad.Palette.Get(EScatterParam::GrassBaseP) == Defaults.Get(EScatterParam::GrassBaseP));
	const FScatterOptions Reach = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterParams=ShrubHedgeRingM=5"));
	Expect(TEXT("a reach beyond the apron is rejected"), !Reach.ParamsError.IsEmpty() && Reach.Palette.Get(EScatterParam::ShrubHedgeRingM) == Defaults.Get(EScatterParam::ShrubHedgeRingM));
	const FScatterOptions Raw = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterParams=StHi=#1000000000"));
	Expect(TEXT("raw slope form"), Raw.ParamsError.IsEmpty() && Raw.Palette.Get(EScatterParam::StHi) == 1000000000);
	// Invalid enumerated values keep their defaults and are reported (never silently replaced).
	Expect(TEXT("fine tile 48 reported"), C.ParamsError.Contains(TEXT("FineTileM")) && C.ParamsError.Contains(TEXT("CoarseTileM")) && C.ParamsError.Contains(TEXT("ScatterThreads")));
	const FScatterOptions BadEnum = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterLevel=L2 -ChimeraTerrainScatterMobility=wobbly -ChimeraTerrainScatterApply=merge"));
	Expect(TEXT("bad level, mobility and apply reported, defaults kept"), BadEnum.ParamsError.Contains(TEXT("ScatterLevel")) && BadEnum.ParamsError.Contains(TEXT("ScatterMobility"))
		&& BadEnum.ParamsError.Contains(TEXT("ScatterApply")) && BadEnum.Palette.Level == EScatterLevel::L0 && BadEnum.Mobility == TEXT("stationary") && BadEnum.ApplyMode == TEXT("diff"));
	Expect(TEXT("valid options report nothing"), V.ParamsError.IsEmpty());
	const FScatterOptions BadLayer = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterLayers=grass,wibble"));
	Expect(TEXT("bad layer reported"), !BadLayer.ParamsError.IsEmpty());
	// CullM (S6 round 3): per-unit cull end override, drawing only; 0 = the unit's default; bad items are reported and skipped.
	Expect(TEXT("no CullM: every end 0"), V.CullEndM.Num() == ScatterMeshCount && V.CullEndM[static_cast<int32>(EScatterMesh::GrassT0)] == 0.0);
	const FScatterOptions Cull = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterCullM=GrassT0=70,GrassT1=45.5"));
	Expect(TEXT("CullM applied"), Cull.ParamsError.IsEmpty() && Cull.CullEndM[static_cast<int32>(EScatterMesh::GrassT0)] == 70.0
		&& Cull.CullEndM[static_cast<int32>(EScatterMesh::GrassT1)] == 45.5 && Cull.CullEndM[static_cast<int32>(EScatterMesh::Tussock)] == 0.0);
	const FScatterOptions BadCull = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterCullM=Wibble=70,GrassT0=0,GrassT1=x,Flower=80"));
	Expect(TEXT("bad CullM items reported, the valid one kept"), BadCull.ParamsError.Contains(TEXT("Wibble")) && BadCull.ParamsError.Contains(TEXT("GrassT0"))
		&& BadCull.ParamsError.Contains(TEXT("GrassT1")) && BadCull.CullEndM[static_cast<int32>(EScatterMesh::GrassT0)] == 0.0
		&& BadCull.CullEndM[static_cast<int32>(EScatterMesh::Flower)] == 80.0);
	// S6 close: a slot given twice in CullM or Meshes is reported and the first value kept (never a silent last-one-wins).
	const FScatterOptions DupCull = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterCullM=GrassT0=70,GrassT0=40"));
	Expect(TEXT("duplicate CullM slot reported, first kept"), DupCull.ParamsError.Contains(TEXT("twice")) && DupCull.CullEndM[static_cast<int32>(EScatterMesh::GrassT0)] == 70.0);
	const FScatterOptions DupMesh = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatterMeshes=TreeBroadA=L1,TreeBroadA=L0"));
	Expect(TEXT("duplicate Meshes slot reported, first kept"), DupMesh.ParamsError.Contains(TEXT("twice")) && DupMesh.MeshChoice[static_cast<int32>(EScatterMesh::TreeBroadA)] == TEXT("L1"));
	return bOk;
}

SC_TEST(ConfigFnvFoldsOnlyRecordOptions)
{
	// config_fnv moves for every option that changes records (seed, level, density, params) and for none of the others.
	const uint64 Base = FScatterOptions::FromCommandLine(TEXT("")).ConfigFnv();
	bool bOk = true;
	const TCHAR* const Folded[] = {TEXT("-ChimeraTerrainScatterSeed=5"), TEXT("-ChimeraTerrainScatterLevel=L1"), TEXT("-ChimeraTerrainScatterDensity=0.5"), TEXT("-ChimeraTerrainScatterParams=TreeCoreP=0.2")};
	for (const TCHAR* Opt : Folded)
	{
		if (FScatterOptions::FromCommandLine(Opt).ConfigFnv() == Base)
		{
			AddError(FString::Printf(TEXT("%s does not move config_fnv"), Opt));
			bOk = false;
		}
	}
	const TCHAR* const Unfolded[] = {TEXT("-ChimeraTerrainScatterFineTileM=64"), TEXT("-ChimeraTerrainScatterCoarseTileM=160"), TEXT("-ChimeraTerrainScatterThreads=0"), TEXT("-ChimeraTerrainScatterBudgetMs=3"),
		TEXT("-ChimeraTerrainScatterLoadBudgetMs=20"), TEXT("-ChimeraTerrainScatterDuringStroke=0"), TEXT("-ChimeraTerrainScatterCasterDuringStroke=150"), TEXT("-ChimeraTerrainScatterMobility=movable"),
		TEXT("-ChimeraTerrainScatterApply=clear"), TEXT("-ChimeraTerrainScatterGovernor=3"), TEXT("-ChimeraTerrainScatterLayers=grass"), TEXT("-ChimeraTerrainScatterGrassNanite=0"), TEXT("-ChimeraTerrainScatter=1"),
		TEXT("-ChimeraTerrainScatter=0"), TEXT("-ChimeraTerrainScatterCullM=GrassT0=70,GrassT1=45"), TEXT("-ChimeraTerrainScatterMeshes=TreeBroadA=L1")};
	for (const TCHAR* Opt : Unfolded)
	{
		if (FScatterOptions::FromCommandLine(Opt).ConfigFnv() != Base)
		{
			AddError(FString::Printf(TEXT("%s moves config_fnv but changes no record"), Opt));
			bOk = false;
		}
	}
	return bOk;
}

SC_TEST(EngineIsm)
{
	// FFakeIsm against a real UInstancedStaticMeshComponent (unregistered, remove-at-swap, 4 custom floats): 1,000 random edit scripts, the engine's
	// PerInstanceSMData and custom data match the model index by index after every script. (UNVERIFIED in the plan that an unregistered component
	// accepts the calls: this test is the check.)
	FTerrainHeightfield HF;
	ScMakeMap(HF, 64, 32, 21, true);
	FScatterPalette P;
	P.Level = EScatterLevel::L1;
	TArray<FScatterRecord> All;
	ReferenceRecords(HF, P, 32, 80, All);
	TArray<FScatterRecord> Fine;
	for (const FScatterRecord& R : All)
	{
		if (ScatterGridOfClass(static_cast<EScatterClass>(R.Class)) == EScatterGrid::Fine)
		{
			Fine.Add(R);
		}
	}
	if (Fine.Num() < 3000)
	{
		AddError(TEXT("not enough fine records"));
		return false;
	}
	UInstancedStaticMeshComponent* Ism = NewObject<UInstancedStaticMeshComponent>(GetTransientPackage());
	Ism->AddToRoot();
	Ism->SetRemoveSwap();
	Ism->SetNumCustomDataFloats(4);
	FFakeIsm Model;
	FScRng Rng(31337);
	bool bOk = true;
	double MinQuatDot = 1.0;
	auto ToTransform = [&P](const FScatterRecord& R, float(&Custom)[4])
	{
		const FScatterInstance I = BuildInstance(R, P);
		for (int32 K = 0; K < 4; ++K)
		{
			Custom[K] = I.Custom[K];
		}
		return FTransform(FQuat(I.Quat[0], I.Quat[1], I.Quat[2], I.Quat[3]), FVector(I.Pos[0] * 100.0, I.Pos[1] * 100.0, I.Pos[2] * 100.0), FVector(I.Scale[0], I.Scale[1], I.Scale[2]));
	};
	// Pool slices so old and new sets overlap partially.
	for (int32 Iter = 0; Iter < 1000 && bOk; ++Iter)
	{
		TArray<FScatterRecord> NewRecs;
		const int32 Start = Rng.Range(0, Fine.Num() - 400);
		const int32 Count = (Iter % 97 == 0) ? 0 : Rng.Range(20, 300);
		for (int32 K = 0; K < Count; ++K)
		{
			FScatterRecord R = Fine[Start + K * 1];
			if (Rng.Range(0, 5) == 0)
			{
				R.ZQ += 1000;
				R.GXQ += 77;
			}
			NewRecs.Add(R);
		}
		std::sort(NewRecs.GetData(), NewRecs.GetData() + NewRecs.Num(), [](const FScatterRecord& A, const FScatterRecord& B) { return A.Key() < B.Key(); });
		FScatterUnitState Old;
		Old.Records = Model.Items;
		Old.RebuildSorted();
		TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> Next;
		const FScatterEditScript Script = BuildEditScript(Old, NewRecs, Next);
		// Execute on the engine component with the calls the renderer will use.
		for (const FScatterUpdate& U : Script.Updates)
		{
			float Custom[4];
			const FTransform T = ToTransform(U.Record, Custom);
			TArray<FTransform> One;
			One.Add(T);
			Ism->BatchUpdateInstancesTransforms(U.Index, One, false, false, false);
			Ism->SetCustomData(U.Index, U.Index, TConstArrayView<float>(Custom, 4));
		}
		if (Script.Removes.Num() > 0)
		{
			Ism->RemoveInstances(Script.Removes, true);
		}
		if (Script.Appends.Num() > 0)
		{
			TArray<FTransform> Ts;
			TArray<float> Cs;
			for (const FScatterRecord& R : Script.Appends)
			{
				float Custom[4];
				Ts.Add(ToTransform(R, Custom));
				Cs.Append(Custom, 4);
			}
			const int32 First = Ism->GetNumInstances();
			Ism->AddInstances(Ts, false, false, false);
			Ism->SetCustomData(First, First + Ts.Num() - 1, TConstArrayView<float>(Cs.GetData(), Cs.Num()));
		}
		Model.Apply(Script);
		// Index-by-index comparison.
		if (Ism->GetNumInstances() != Model.Items.Num() || Ism->PerInstanceSMCustomData.Num() != Model.Items.Num() * 4)
		{
			AddError(FString::Printf(TEXT("iteration %d: engine has %d instances, model %d"), Iter, Ism->GetNumInstances(), Model.Items.Num()));
			bOk = false;
			break;
		}
		for (int32 I = 0; I < Model.Items.Num() && bOk; ++I)
		{
			float Custom[4];
			const FTransform Want = ToTransform(Model.Items[I], Custom);
			FTransform Got;
			Ism->GetInstanceTransform(I, Got, false);
			if (!Got.GetTranslation().Equals(Want.GetTranslation(), 0.02))
			{
				AddError(FString::Printf(TEXT("iteration %d index %d: translation differs (%s vs %s)"), Iter, I, *Got.GetTranslation().ToString(), *Want.GetTranslation().ToString()));
				bOk = false;
			}
			// The full PerInstanceSMData transform: rotation (the same rotation up to the quaternion sign) and scale.
			const double QDot = FMath::Abs(Got.GetRotation() | Want.GetRotation());
			MinQuatDot = FMath::Min(MinQuatDot, QDot);
			if (QDot < 1.0 - 1.0e-6)
			{
				AddError(FString::Printf(TEXT("iteration %d index %d: rotation differs (|dot| %.9f)"), Iter, I, QDot));
				bOk = false;
			}
			if (!Got.GetScale3D().Equals(Want.GetScale3D(), 1.0e-5))
			{
				AddError(FString::Printf(TEXT("iteration %d index %d: scale differs (%s vs %s)"), Iter, I, *Got.GetScale3D().ToString(), *Want.GetScale3D().ToString()));
				bOk = false;
			}
			for (int32 K = 0; K < 4; ++K)
			{
				if (Ism->PerInstanceSMCustomData[I * 4 + K] != Custom[K])
				{
					AddError(FString::Printf(TEXT("iteration %d index %d: custom data %d differs"), Iter, I, K));
					bOk = false;
					break;
				}
			}
		}
	}
	AddInfo(FString::Printf(TEXT("EngineIsm: 1,000 scripts, final %d instances; translation, rotation (min |dot| %.9f), scale and 4 custom floats equal index by index"), Model.Items.Num(), MinQuatDot));
	Ism->RemoveFromRoot();
	return bOk;
}

#endif

#endif

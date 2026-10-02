// Project Chimera terrain trial (plan C scatter 3.5, task S2). Original Chimera code.
// FScatterScheduler: tile states, the Busy rule, dispatch order and the budgeted apply. Pure bookkeeping: the engine calls (snapshot, task launch,
// ISM edits) are injected by the owner, so the tests drive the whole loop with a fake executor and a fake clock.

#include "Data/TerrainScatter.h"

#include <algorithm>
#include <cmath>

namespace ChimeraTerrain
{
	void FScatterScheduler::Init(const FScatterSchedulerConfig& InConfig)
	{
		Config = InConfig;
		TileGrids[static_cast<int32>(EScatterGrid::Fine)] = MakeScatterTileGrid(Config.HalfExtentM, Config.FineTileM);
		TileGrids[static_cast<int32>(EScatterGrid::Coarse)] = MakeScatterTileGrid(Config.HalfExtentM, Config.CoarseTileM);
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			const int32 N = TileGrids[G].NumTiles();
			Tiles[G].Reset();
			Tiles[G].SetNum(N);
			States[G].Reset();
			States[G].SetNum(N * ScatterMeshCountOfGrid(static_cast<EScatterGrid>(G)));
		}
		bStrokeOpen = false;
		bCancelled = false;
		bEnabled = true;
		Epoch = 1;
		GenCounter = 0;
		InFlight = 0;
		Counters = FScatterCounters();
		{
			FScopeLock Lock(&PostedLock);
			Posted.Reset();
		}
		Ready.Reset();
		FrontUnitsApplied = 0;
		bFrontAnyChange = false;
	}

	void FScatterScheduler::MarkTile(EScatterGrid Grid, int32 TileIndex)
	{
		if (!bEnabled)
		{
			return;
		}
		Tiles[static_cast<int32>(Grid)][TileIndex].DirtyGen = ++GenCounter;
	}

	void FScatterScheduler::MarkRange(EScatterGrid Grid, const FScatterTileRange& Range)
	{
		if (Range.IsEmpty())
		{
			return;
		}
		const FScatterTileGrid& TG = TileGrids[static_cast<int32>(Grid)];
		const int32 X0 = FMath::Max(Range.MinX, TG.Min);
		const int32 Y0 = FMath::Max(Range.MinY, TG.Min);
		const int32 X1 = FMath::Min(Range.MaxX, TG.Min + TG.Count - 1);
		const int32 Y1 = FMath::Min(Range.MaxY, TG.Min + TG.Count - 1);
		for (int32 TY = Y0; TY <= Y1; ++TY)
		{
			for (int32 TX = X0; TX <= X1; ++TX)
			{
				MarkTile(Grid, TG.IndexOf(TX, TY));
			}
		}
	}

	void FScatterScheduler::MarkRects(const FTerrainRect& VertexRect, const FTerrainRect& SplatRect)
	{
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			const EScatterGrid Grid = static_cast<EScatterGrid>(G);
			const int64 Apron = ScatterApronQ16(Grid);
			MarkRange(Grid, ScatterTileRangeForVertexRect(TileGrids[G], Config.HalfExtentM, VertexRect, Apron));
			MarkRange(Grid, ScatterTileRangeForSplatRect(TileGrids[G], Config.HalfExtentM, SplatRect, Apron));
		}
	}

	void FScatterScheduler::Disable()
	{
		++Epoch;
		bEnabled = false;
		// Ready results (the front one possibly half applied) belong to the old epoch: drop them; their tiles stop being Busy.
		for (const FScatterTileResult& R : Ready)
		{
			Tiles[static_cast<int32>(R.Grid)][R.TileIndex].bBusy = false;
			++Counters.DiscardedEpoch;
		}
		Ready.Reset();
		FrontUnitsApplied = 0;
		bFrontAnyChange = false;
		// No tile is dirty afterwards. A tile with a job in flight stays Busy until PollResults discards that result.
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			for (FTile& T : Tiles[G])
			{
				T.AppliedGen = T.DirtyGen;
				T.DispatchedGen = T.DirtyGen;
				T.bAppliedStale = false;
				T.LastDispatchMs = -1.0e18;
			}
		}
	}

	void FScatterScheduler::BumpEpochAndDirtyAll()
	{
		++Epoch;
		bEnabled = true;
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			for (int32 I = 0; I < Tiles[G].Num(); ++I)
			{
				MarkTile(static_cast<EScatterGrid>(G), I);
			}
		}
	}

	void FScatterScheduler::CancelAll()
	{
		bCancelled = true;
		// Results that were ready but not applied count as cancelled; a job still running counts when it posts.
		for (const FScatterTileResult& R : Ready)
		{
			Tiles[static_cast<int32>(R.Grid)][R.TileIndex].bBusy = false;
			++Counters.Cancelled;
		}
		Ready.Reset();
		FrontUnitsApplied = 0;
		bFrontAnyChange = false;
	}

	void FScatterScheduler::PostResult(FScatterTileResult&& Result)
	{
		FScopeLock Lock(&PostedLock);
		Posted.Add(MoveTemp(Result));
	}

	int32 FScatterScheduler::PollResults()
	{
		TArray<FScatterTileResult> Local;
		{
			FScopeLock Lock(&PostedLock);
			Local = MoveTemp(Posted);
			Posted.Reset();
		}
		int32 Moved = 0;
		for (FScatterTileResult& R : Local)
		{
			FTile& T = Tiles[static_cast<int32>(R.Grid)][R.TileIndex];
			if (T.bInFlight)
			{
				T.bInFlight = false;
				--InFlight;
			}
			if (bCancelled)
			{
				++Counters.Cancelled;
				T.bBusy = false;
			}
			else if (R.Epoch != Epoch)
			{
				++Counters.DiscardedEpoch;
				T.bBusy = false;
			}
			else
			{
				Ready.Add(MoveTemp(R));
				++Moved;
			}
		}
		return Moved;
	}

	int32 FScatterScheduler::Dispatch(double NowMs, double BrushXM, double BrushYM, const FLaunchFn& Launch)
	{
		if (bCancelled || !bEnabled)
		{
			return 0;
		}
		// ScatterThreads=0: one tile per call, generated inline on the game thread by the owner, outside the in-flight count.
		const bool bGameThread = Config.MaxInFlight <= 0;
		struct FCandidate
		{
			double Dist2;
			int32 Grid;
			int32 Index;
		};
		TArray<FCandidate> Candidates;
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			const FScatterTileGrid& TG = TileGrids[G];
			const double Interval = (G == static_cast<int32>(EScatterGrid::Fine)) ? Config.DuringStrokeMs : Config.CasterDuringStrokeMs;
			for (int32 I = 0; I < Tiles[G].Num(); ++I)
			{
				const FTile& T = Tiles[G][I];
				if (T.bBusy || T.AppliedGen >= T.DirtyGen)
				{
					continue;
				}
				if (bStrokeOpen)
				{
					if (Interval <= 0.0 || NowMs - T.LastDispatchMs < Interval)
					{
						continue;
					}
				}
				const double CX = (static_cast<double>(TG.TXOf(I)) + 0.5) * TG.SizeM;
				const double CY = (static_cast<double>(TG.TYOf(I)) + 0.5) * TG.SizeM;
				FCandidate C;
				C.Dist2 = (CX - BrushXM) * (CX - BrushXM) + (CY - BrushYM) * (CY - BrushYM);
				C.Grid = G;
				C.Index = I;
				Candidates.Add(C);
			}
		}
		std::sort(Candidates.GetData(), Candidates.GetData() + Candidates.Num(), [](const FCandidate& A, const FCandidate& B)
		{
			if (A.Dist2 != B.Dist2)
			{
				return A.Dist2 < B.Dist2;
			}
			if (A.Grid != B.Grid)
			{
				return A.Grid < B.Grid;
			}
			return A.Index < B.Index;
		});
		int32 Count = 0;
		for (const FCandidate& C : Candidates)
		{
			if (bGameThread ? Count >= 1 : InFlight >= Config.MaxInFlight)
			{
				break;
			}
			FTile& T = Tiles[C.Grid][C.Index];
			T.bBusy = true;
			T.bInFlight = !bGameThread;
			T.DispatchedGen = T.DirtyGen;
			T.LastDispatchMs = NowMs;
			if (bGameThread)
			{
				++Counters.GtGenerations;
			}
			else
			{
				++InFlight;
			}
			++Counters.Dispatched;
			if (bStrokeOpen)
			{
				++Counters.MidStrokeDispatches;
			}
			if (T.bAppliedStale)
			{
				++Counters.StaleResultRedispatches;
				T.bAppliedStale = false;
			}
			Counters.InFlightMax = FMath::Max(Counters.InFlightMax, InFlight);
			SetBusyMax();
			FScatterDispatch D;
			D.Grid = static_cast<EScatterGrid>(C.Grid);
			D.TileIndex = C.Index;
			D.Gen = T.DispatchedGen;
			D.Epoch = Epoch;
			D.bMidStroke = bStrokeOpen;
			D.bGameThread = bGameThread;
			Launch(D);
			++Count;
		}
		return Count;
	}

	double FScatterScheduler::PredictUnitMs(const FScatterUnitWork& Unit) const
	{
		if (Unit.Script.IsEmpty())
		{
			return 0.0;
		}
		return Config.PredictA + Config.PredictB * static_cast<double>(Unit.Script.Changes()) + Config.PredictC * static_cast<double>(Unit.InstancesAfter());
	}

	int32 FScatterScheduler::ApplyReady(double BudgetMs, const FClockFn& Clock, const FApplyFn& Apply)
	{
		const double Start = Clock();
		int32 Worked = 0;
		while (Ready.Num() > 0)
		{
			FScatterTileResult& R = Ready[0];
			FTile& T = Tiles[static_cast<int32>(R.Grid)][R.TileIndex];
			if (R.Epoch != Epoch)
			{
				// The epoch moved while this result waited (possibly half applied): drop the rest. The tile is dirty again or the units are cleared.
				++Counters.DiscardedEpoch;
				T.bBusy = false;
				Ready.RemoveAt(0);
				FrontUnitsApplied = 0;
				bFrontAnyChange = false;
				continue;
			}
			if (FrontUnitsApplied < R.Units.Num())
			{
				const FScatterUnitWork& U = R.Units[FrontUnitsApplied];
				if (!U.Script.IsEmpty())
				{
					if (BudgetMs > 0.0 && Worked > 0 && (Clock() - Start) + PredictUnitMs(U) > BudgetMs)
					{
						break;
					}
					Apply(R.Grid, R.TileIndex, U);
					bFrontAnyChange = true;
					++Worked;
					++Counters.UnitsApplied;
				}
				const int32 MeshesInGrid = ScatterMeshCountOfGrid(R.Grid);
				States[static_cast<int32>(R.Grid)][R.TileIndex * MeshesInGrid + ScatterMeshIndexInGrid(static_cast<EScatterMesh>(U.Mesh))] = U.Next;
				++FrontUnitsApplied;
			}
			if (FrontUnitsApplied >= R.Units.Num())
			{
				// The last unit is applied and its state swapped: the tile stops being Busy.
				T.AppliedGen = R.Gen;
				T.bAppliedStale = T.DirtyGen > R.Gen;
				T.bBusy = false;
				if (bFrontAnyChange)
				{
					++Counters.Applied;
				}
				else
				{
					++Counters.SkippedIdentical;
				}
				Ready.RemoveAt(0);
				FrontUnitsApplied = 0;
				bFrontAnyChange = false;
			}
		}
		return Worked;
	}

	void FScatterScheduler::SetBusyMax()
	{
		Counters.BusyMax = FMath::Max(Counters.BusyMax, NumBusy());
	}

	int32 FScatterScheduler::NumBusy() const
	{
		int32 N = 0;
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			for (const FTile& T : Tiles[G])
			{
				N += T.bBusy ? 1 : 0;
			}
		}
		return N;
	}

	int32 FScatterScheduler::NumDirty() const
	{
		int32 N = 0;
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			for (const FTile& T : Tiles[G])
			{
				N += (T.AppliedGen < T.DirtyGen) ? 1 : 0;
			}
		}
		return N;
	}

	bool FScatterScheduler::HasPendingWork() const
	{
		if (Ready.Num() > 0)
		{
			return true;
		}
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			for (const FTile& T : Tiles[G])
			{
				if (T.bBusy || T.AppliedGen < T.DirtyGen)
				{
					return true;
				}
			}
		}
		return false;
	}

	bool FScatterScheduler::IsCurrent(EScatterGrid Grid, int32 TileIndex) const
	{
		const FTile& T = Tiles[static_cast<int32>(Grid)][TileIndex];
		return !T.bBusy && T.AppliedGen >= T.DirtyGen;
	}

	TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> FScatterScheduler::GetUnitState(EScatterGrid Grid, int32 TileIndex, int32 MeshIndexInGrid) const
	{
		return States[static_cast<int32>(Grid)][TileIndex * ScatterMeshCountOfGrid(Grid) + MeshIndexInGrid];
	}

	void FScatterScheduler::ClearUnitStates()
	{
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			for (TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe>& S : States[G])
			{
				S.Reset();
			}
		}
	}

	FScatterHash FScatterScheduler::FoldApplied() const
	{
		FScatterHash H;
		for (int32 G = 0; G < ScatterGridCount; ++G)
		{
			for (const TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe>& S : States[G])
			{
				if (S.IsValid())
				{
					ScatterFoldState(H, *S);
				}
			}
		}
		return H;
	}
}

// Project Chimera terrain trial (plan C scatter 3.5, 3.7, task S4). Original Chimera code.

#include "Render/TerrainScatterRenderer.h"

#include "ChimeraTerrain.h"
#include "HAL/PlatformTime.h"
#include "Misc/ScopeExit.h"

namespace ChimeraTerrain
{
	FTransform ScatterInstanceTransform(const FScatterInstance& I)
	{
		return FTransform(FQuat(I.Quat[0], I.Quat[1], I.Quat[2], I.Quat[3]), FVector(I.Pos[0] * 100.0, I.Pos[1] * 100.0, I.Pos[2] * 100.0),
			FVector(I.Scale[0], I.Scale[1], I.Scale[2]));
	}

	namespace
	{
		void AddRecord(const FScatterRecord& R, const FScatterPalette& P, int32 NumCustom, TArray<FTransform>& Ts, TArray<float>& Cs)
		{
			const FScatterInstance I = BuildInstance(R, P);
			Ts.Add(ScatterInstanceTransform(I));
			for (int32 K = 0; K < NumCustom; ++K)
			{
				Cs.Add(K < I.NumCustom ? I.Custom[K] : 0.0f);
			}
		}
	}

	void PrepareScatterUnit(const FScatterUnitWork& Work, const FScatterPalette& Palette, bool bFull, FScatterUnitPrepared& Out)
	{
		const EScatterGrid Grid = ScatterGridOfMesh(static_cast<EScatterMesh>(Work.Mesh));
		Out.NumCustom = Grid == EScatterGrid::Fine ? 4 : 2;
		Out.UpdateTransforms.Reset(Work.Script.Updates.Num());
		Out.UpdateCustom.Reset(Work.Script.Updates.Num() * Out.NumCustom);
		for (const FScatterUpdate& U : Work.Script.Updates)
		{
			AddRecord(U.Record, Palette, Out.NumCustom, Out.UpdateTransforms, Out.UpdateCustom);
		}
		Out.AppendTransforms.Reset(Work.Script.Appends.Num());
		Out.AppendCustom.Reset(Work.Script.Appends.Num() * Out.NumCustom);
		for (const FScatterRecord& R : Work.Script.Appends)
		{
			AddRecord(R, Palette, Out.NumCustom, Out.AppendTransforms, Out.AppendCustom);
		}
		Out.bFull = bFull;
		if (bFull && Work.Next.IsValid())
		{
			const TArray<FScatterRecord>& All = Work.Next->Records;
			Out.FullTransforms.Reset(All.Num());
			Out.FullCustom.Reset(All.Num() * Out.NumCustom);
			for (const FScatterRecord& R : All)
			{
				AddRecord(R, Palette, Out.NumCustom, Out.FullTransforms, Out.FullCustom);
			}
		}
	}

	void FScatterProxyCounters::Reset()
	{
		FirstCreates = 0;
		ExpectedRebuilds = 0;
		Recreates = 0;
		RecreatesDuringEdits = 0;
		ApplyDirtiedRecreates = 0;
		EngineRecreates = 0;
		EngineRecreatesDuringEdits = 0;
		CompileRecreates = 0;
		CompileRecreatesDuringEdits = 0;
		Refills = 0;
		RefillsDuringEdits = 0;
		PsoRecreates = 0;
		PsoRecreatesDuringEdits = 0;
		ShaderPropagations = 0;
		AssetPostCompiles = 0;
		CompileBusyFrames = 0;
		FlushCycles = 0;
		FlushCalls = 0;
		bEditsActive = false;
	}

	FScatterProxyCounters& ScatterProxyCounters()
	{
		static FScatterProxyCounters Counters;
		return Counters;
	}
}

using namespace ChimeraTerrain;

UChimeraScatterISM::UChimeraScatterISM()
{
	PrimaryComponentTick.bCanEverTick = false;
	// Plan C scatter 3.7: the owner sets every render setting before registration; these are the class defaults it starts from.
	bDisableCollision = true;
	SetCollisionEnabled(ECollisionEnabled::NoCollision);
	SetCanEverAffectNavigation(false);
	SetGenerateOverlapEvents(false);
}

FPrimitiveSceneProxy* UChimeraScatterISM::CreateSceneProxy()
{
	// PSO precaching (PrimitiveComponent.h:1031): a proxy made while the component's PSOs still compile is replaced when they finish
	// (UWorld::AddPrimitiveComponentPSOPrecacheCompleted -> MarkRenderStateDirty, LevelTick.cpp:919-928).
	const bool bPsoPendingNow = IsPSOPrecaching();
	FPrimitiveSceneProxy* Proxy = Super::CreateSceneProxy();
	// Every dirty mark before this point is consumed by this creation, whether or not it yields a proxy.
	const bool bCompileDirty = bCompileDirtied.exchange(false);
	const bool bApplyDirty = bApplyDirtied.exchange(false);
	if (Proxy == nullptr)
	{
		// An empty unit (F3) or a PSO-precache delay: no proxy. The next proxy fills a proxy-less component, it replaces nothing.
		bProxyless.store(true);
		return Proxy;
	}
	FScatterProxyCounters& C = ScatterProxyCounters();
	const bool bDuringEdits = C.bEditsActive.load();
	const int32 Previous = ProxyCreateCount.fetch_add(1);
	const bool bWasProxyless = bProxyless.exchange(false);
	const bool bPreviousHadPendingPso = bLastProxyPsoPending.exchange(bPsoPendingNow);
	if (Previous == 0)
	{
		C.FirstCreates.fetch_add(1);
		return Proxy;
	}
	int32 Expected = ExpectedRebuildCount.load();
	while (Expected > 0)
	{
		if (ExpectedRebuildCount.compare_exchange_weak(Expected, Expected - 1))
		{
			C.ExpectedRebuilds.fetch_add(1);
			return Proxy;
		}
	}
	if (bWasProxyless)
	{
		C.Refills.fetch_add(1);
		if (bDuringEdits)
		{
			C.RefillsDuringEdits.fetch_add(1);
		}
		return Proxy;
	}
	// From here a live proxy is being replaced. Scatter's own apply dirtying it outranks every other explanation (plan C scatter 3.5: an edit
	// changes instances in place and never rebuilds a proxy).
	if (!bApplyDirty)
	{
		if (!bLastDestroyWasDirty.load())
		{
			C.EngineRecreates.fetch_add(1);
			if (bDuringEdits)
			{
				C.EngineRecreatesDuringEdits.fetch_add(1);
			}
			return Proxy;
		}
		if (bPreviousHadPendingPso)
		{
			C.PsoRecreates.fetch_add(1);
			if (bDuringEdits)
			{
				C.PsoRecreatesDuringEdits.fetch_add(1);
			}
			return Proxy;
		}
		if (bCompileDirty)
		{
			C.CompileRecreates.fetch_add(1);
			if (bDuringEdits)
			{
				C.CompileRecreatesDuringEdits.fetch_add(1);
			}
			UE_LOG(LogChimeraTerrain, Verbose, TEXT("scatter proxy recreate (editor compile) unit=%s create=%d frame=%llu"), *GetName(), Previous + 1, static_cast<unsigned long long>(GFrameCounter));
			return Proxy;
		}
	}
	C.Recreates.fetch_add(1);
	if (bApplyDirty)
	{
		C.ApplyDirtiedRecreates.fetch_add(1);
	}
	if (bDuringEdits)
	{
		C.RecreatesDuringEdits.fetch_add(1);
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter proxy recreate unit=%s create=%d frame=%llu during_edits=%d apply_dirtied=%d instances=%d"), *GetName(), Previous + 1,
		static_cast<unsigned long long>(GFrameCounter), bDuringEdits ? 1 : 0, bApplyDirty ? 1 : 0, GetNumInstances());
	return Proxy;
}

void UChimeraScatterISM::NoteCompilePropagation()
{
	if (IsRenderStateDirty())
	{
		bCompileDirtied.store(true);
	}
}

void UChimeraScatterISM::DestroyRenderState_Concurrent()
{
	// The component's own MarkRenderStateDirty (ActorComponent.cpp:2695-2702) leaves bRenderStateDirty set until the deferred recreate
	// (DoDeferredRenderUpdates_Concurrent, :2667-2670); an engine-wide recreate context or re-register destroys with the flag clear.
	bLastDestroyWasDirty.store(IsRenderStateDirty());
	Super::DestroyRenderState_Concurrent();
}

void UChimeraScatterISM::SendRenderInstanceData_Concurrent()
{
	const uint64 T0 = FPlatformTime::Cycles64();
	Super::SendRenderInstanceData_Concurrent();
	FScatterProxyCounters& C = ScatterProxyCounters();
	C.FlushCycles.fetch_add(FPlatformTime::Cycles64() - T0);
	C.FlushCalls.fetch_add(1);
}

void UChimeraScatterISM::ApplyScript(const FScatterEditScript& Script, const FScatterUnitPrepared& P)
{
	const int32 NC = P.NumCustom;
	if (P.bFull)
	{
		if (SceneProxy != nullptr)
		{
			ExpectRebuild();
		}
		ClearInstances();
		if (P.FullTransforms.Num() > 0)
		{
			AddInstances(P.FullTransforms, false, false, false);
			SetCustomData(0, P.FullTransforms.Num() - 1, TConstArrayView<float>(P.FullCustom.GetData(), P.FullCustom.Num()));
		}
		return;
	}
	// A diff apply on a live proxy must not mark the render state dirty (the instance data manager updates the proxy in place). If it does,
	// the next proxy is counted as a recreate whatever else happens before the end-of-frame update (SX8).
	const bool bDirtyBefore = IsRenderStateDirty();
	const bool bHadProxy = SceneProxy != nullptr;
	ON_SCOPE_EXIT
	{
		if (bHadProxy && !bDirtyBefore && IsRenderStateDirty() && GetNumInstances() > 0)
		{
			bApplyDirtied.store(true);
		}
	};
	// Updates: Script.Updates is sorted by index; one engine call per run of consecutive indices.
	const int32 NU = Script.Updates.Num();
	int32 I = 0;
	while (I < NU)
	{
		int32 J = I + 1;
		while (J < NU && Script.Updates[J].Index == Script.Updates[J - 1].Index + 1)
		{
			++J;
		}
		const int32 Start = Script.Updates[I].Index;
		const int32 Count = J - I;
		BatchUpdateInstancesTransforms(Start, TArrayView<const FTransform>(P.UpdateTransforms.GetData() + I, Count), false, false, false);
		SetCustomData(Start, Start + Count - 1, TConstArrayView<float>(P.UpdateCustom.GetData() + I * NC, Count * NC));
		I = J;
	}
	if (Script.Removes.Num() > 0)
	{
		// Strictly descending (BuildEditScript); remove-at-swap (SetRemoveSwap at creation).
		RemoveInstances(Script.Removes, true);
	}
	if (P.AppendTransforms.Num() > 0)
	{
		const int32 First = GetNumInstances();
		AddInstances(P.AppendTransforms, false, false, false);
		SetCustomData(First, First + P.AppendTransforms.Num() - 1, TConstArrayView<float>(P.AppendCustom.GetData(), P.AppendCustom.Num()));
	}
}

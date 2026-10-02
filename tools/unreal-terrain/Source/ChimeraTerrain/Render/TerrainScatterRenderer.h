// Project Chimera terrain trial (plan C scatter 3.1, 3.5, 3.7, task S4). Original Chimera code.
// The scatter's engine side: UChimeraScatterISM (a plain UInstancedStaticMeshComponent that counts its scene-proxy creations and times its
// end-of-frame instance flush, F5), the worker-built payload of a unit's edit script (FScatterUnitPrepared), and the game-thread execution of
// a script on one component (update runs, remove-at-swap, append; plan C scatter 3.5). Every call cited here is in
// Engine/Classes/Components/InstancedStaticMeshComponent.h (5.8.3): AddInstances :275, SetCustomData(Start, End) :331 (End inclusive, F2),
// BatchUpdateInstancesTransforms (array view) :391, RemoveInstances(..., bSortedReverse) :427, ClearInstances :431, SetCullDistances :443,
// SetRemoveSwap :465, SendRenderInstanceData_Concurrent :531, CreateSceneProxy :535.
#pragma once

#include "CoreMinimal.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Data/TerrainScatter.h"
#include <atomic>
#include "TerrainScatterRenderer.generated.h"

namespace ChimeraTerrain
{
	/**
	 * What the worker builds for one unit's script (plan C scatter 3.2 step 3: EvaluateTile -> BuildInstance -> BuildEditScript on the worker), so
	 * the game thread only makes the engine calls. Transforms are component-local = world (the scatter actor sits at the origin), in cm.
	 */
	struct FScatterUnitPrepared : public FScatterUnitPayload
	{
		int32 NumCustom = 0;
		/** One transform and NumCustom floats per Script.Updates entry, in the same order (ascending index). */
		TArray<FTransform> UpdateTransforms;
		TArray<float> UpdateCustom;
		/** One per Script.Appends entry. */
		TArray<FTransform> AppendTransforms;
		TArray<float> AppendCustom;
		/** ScatterApply=clear: the whole next state (ClearInstances + AddInstances). */
		bool bFull = false;
		TArray<FTransform> FullTransforms;
		TArray<float> FullCustom;
	};

	/** BuildInstance's output as an engine transform (metres -> cm). The one conversion; verify uses it too. */
	FTransform ScatterInstanceTransform(const FScatterInstance& I);

	/** Worker side: the payload of a unit's script (bFull: also the whole next state, for ScatterApply=clear). */
	void PrepareScatterUnit(const FScatterUnitWork& Work, const FScatterPalette& Palette, bool bFull, FScatterUnitPrepared& Out);

	/** Process-wide proxy and flush counters of every UChimeraScatterISM (plan C scatter 3.5 "Proxy counters"; F5: may run off the game thread). */
	struct FScatterProxyCounters
	{
		/** A component's first non-null proxy. */
		std::atomic<int64> FirstCreates{0};
		/** A later proxy while the component's expected-rebuild count was above zero (scatter's own rebuilds: scatter 1, ScatterApply=clear, scatter_fresh). */
		std::atomic<int64> ExpectedRebuilds{0};
		/**
		 * Any other later proxy (gated 0, SX8): it replaced a live proxy and (a) scatter's own apply marked the component's render state dirty
		 * (ApplyDirtiedRecreates, a subset), or (b) the component's render state was marked dirty with no expectation, no engine-wide context, no
		 * PSO completion and no editor-compile propagation behind it.
		 */
		std::atomic<int64> Recreates{0};
		/** The same while edits were in flight (a stroke open or scatter tiles pending outside the fill). */
		std::atomic<int64> RecreatesDuringEdits{0};
		/** The part of Recreates whose dirty mark came from scatter's own diff apply (an edit path that rebuilt a proxy: the defect SX8 exists for). */
		std::atomic<int64> ApplyDirtiedRecreates{0};
		/**
		 * A later proxy after a recreate the engine drove for many components at once (a FComponentRecreateRenderStateContext or re-register): the
		 * component was not marked dirty, so no scatter edit caused it. Reported; gated 0 during edits.
		 */
		std::atomic<int64> EngineRecreates{0};
		std::atomic<int64> EngineRecreatesDuringEdits{0};
		/**
		 * A later proxy whose dirty mark the editor's compilers set, attributed per component, not by a time window: the scatter actor listens to
		 * FEditorSupportDelegates::RedrawAllViewports, which FShaderCompilingManager broadcasts right after PropagateMaterialChangesToPrimitives
		 * marks every primitive using a finished material dirty (ShaderCompiler.cpp:2405-2410, 2438-2441), and to
		 * FAssetCompilingManager::OnAssetPostCompileEvent, which the texture and static-mesh compilers broadcast after marking their users dirty
		 * (TextureCompiler.cpp:634-660, StaticMeshCompiler.cpp:291-312). In the handler every scatter component whose render state is dirty at
		 * that moment is flagged; its next proxy is a compile recreate unless scatter's own apply had dirtied it first. Only the editor build
		 * (-game) compiles; a cooked game never does. Reported; gated 0 during edits.
		 */
		std::atomic<int64> CompileRecreates{0};
		std::atomic<int64> CompileRecreatesDuringEdits{0};
		/** A later proxy for a component that had none (emptied, or created empty): it replaces no proxy (e.g. trees painted back after an undo). */
		std::atomic<int64> Refills{0};
		std::atomic<int64> RefillsDuringEdits{0};
		/** A later proxy replacing one that was made while the component's PSOs were still precaching (the engine swaps it when they finish). */
		std::atomic<int64> PsoRecreates{0};
		std::atomic<int64> PsoRecreatesDuringEdits{0};
		/** Compile propagations seen (shader-map propagations, asset post-compile events) and frames with compile work outstanding. */
		std::atomic<int64> ShaderPropagations{0};
		std::atomic<int64> AssetPostCompiles{0};
		std::atomic<int64> CompileBusyFrames{0};
		/** SendRenderInstanceData_Concurrent: summed cycles and calls since the last read (scatter_flush_ms). */
		std::atomic<uint64> FlushCycles{0};
		std::atomic<int64> FlushCalls{0};
		/** Set by the scatter actor each frame. */
		std::atomic<bool> bEditsActive{false};

		void Reset();
	};
	FScatterProxyCounters& ScatterProxyCounters();
}

/**
 * One scatter unit (tile, mesh). Never HISM (plan C scatter 3.7). Settings are applied by the owner before RegisterComponent; the component
 * itself only counts proxies and times its instance flush.
 */
UCLASS(ClassGroup = (Rendering))
class CHIMERATERRAIN_API UChimeraScatterISM : public UInstancedStaticMeshComponent
{
	GENERATED_BODY()

public:
	UChimeraScatterISM();

	/** Scatter's own rebuild calls raise this before they act (enable after a disable, ScatterApply=clear, scatter_fresh). Game thread. */
	void ExpectRebuild() { ExpectedRebuildCount.fetch_add(1); }
	/** Drop any unused expectation (after a fill or a fresh rebuild settled, every expected proxy exists). */
	void ClearExpectedRebuilds() { ExpectedRebuildCount.store(0); }
	int32 GetProxyCreates() const { return ProxyCreateCount.load(); }
	/**
	 * Game thread, from the owner's compile-propagation handler: if this component's render state is dirty right now, the compiler that just
	 * propagated marked it, so its next proxy is attributed to the editor compile (unless scatter's own apply dirtied it first).
	 */
	void NoteCompilePropagation();
	bool HasLiveProxy() const { return SceneProxy != nullptr; }

	/** Grid, tile index and mesh slot of the unit (set by the owner at creation). */
	uint8 Grid = 0;
	int32 TileIndex = 0;
	uint8 MeshSlot = 0;

	//~ UPrimitiveComponent
	virtual FPrimitiveSceneProxy* CreateSceneProxy() override;
	virtual void DestroyRenderState_Concurrent() override;
	//~ UActorComponent
	virtual void SendRenderInstanceData_Concurrent() override;

	/**
	 * Execute one unit's script on this component with the worker-built payload (plan C scatter 3.5): BatchUpdateInstancesTransforms per run of
	 * consecutive indices plus SetCustomData, RemoveInstances(descending, sorted) with remove-at-swap, AddInstances(no indices, local, no navigation)
	 * plus SetCustomData. With a full payload: ClearInstances + AddInstances (the A/B), raising the expected rebuild first when a proxy exists.
	 */
	void ApplyScript(const ChimeraTerrain::FScatterEditScript& Script, const ChimeraTerrain::FScatterUnitPrepared& Prepared);

private:
	std::atomic<int32> ExpectedRebuildCount{0};
	std::atomic<int32> ProxyCreateCount{0};
	std::atomic<bool> bLastDestroyWasDirty{false};
	/** The last CreateSceneProxy returned null (no proxy since): the next proxy is a refill. */
	std::atomic<bool> bProxyless{true};
	/** The render state was marked dirty by a compile propagation since the last proxy (NoteCompilePropagation). */
	std::atomic<bool> bCompileDirtied{false};
	/** The render state was marked dirty by scatter's own diff apply since the last proxy (ApplyScript). */
	std::atomic<bool> bApplyDirtied{false};
	/** The live proxy was created while IsPSOPrecaching() was true. */
	std::atomic<bool> bLastProxyPsoPending{false};
};

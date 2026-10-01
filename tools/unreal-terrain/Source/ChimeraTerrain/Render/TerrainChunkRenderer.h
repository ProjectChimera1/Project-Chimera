// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 1, 3.3: the engine-neutral seam between the heightfield (Data/) and whatever draws it. RMC today (FRmcTerrainRenderer);
// the CF fallback (UDynamicMeshComponent) would be a second implementation of this interface and nothing else changes.
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

class AActor;
class UMaterialInterface;
class UPrimitiveComponent;

namespace ChimeraTerrain
{
	/** Draw path of the chunk buffers. Dynamic is the trial's path; Static is recorded only (RMC #303, plan C 3.3, 6 risk 1). */
	enum class ETerrainDrawType : uint8
	{
		Dynamic,
		Static
	};

	/** Counters every renderer reports (plan C 3.8 metrics). Monotonic over the renderer's life. */
	struct FTerrainRenderStats
	{
		int32 Chunks = 0;
		int64 Triangles = 0;
		/** Ranged in-place edits submitted (one per touched chunk per update). */
		int64 RangedEdits = 0;
		/** Scene proxies created beyond each chunk's first: every one after creation is an RMC fallback publish (plan C 3.3). */
		int64 ProxyRecreates = 0;
		/** Proxy recreations that happened while a stroke was open (P2's numerator). */
		int64 ProxyRecreatesDuringStrokes = 0;
		/** Times a chunk's tracked Z range had to widen during a stroke (plan C 3.3). */
		int64 BoundsWidenings = 0;
		/** Exact bounds recomputations (stroke end, undo, redo, load). */
		int64 BoundsExactPushes = 0;
		/** Elements (vertices) uploaded by ranged edits, summed. */
		int64 UploadedVertices = 0;
	};

	/** Host-side cost split of the last UpdateHeights call, milliseconds (plan C 3.8: normals vs upload-submit). */
	struct FTerrainUpdateTiming
	{
		/** Time spent computing normals and tangents (the central-difference pass). */
		double NormalsMs = 0.0;
		/** Everything else in the call: position writes, stream copies, submit to the render side. */
		double SubmitMs = 0.0;
		/** Vertices rewritten (summed over the touched chunks, borders counted once per chunk). */
		int32 Vertices = 0;
	};

	/** One finished ranged edit: how long the render side took to accept it (plan C 3.8, P2). */
	struct FTerrainEditLatency
	{
		/** Frames between the submit and the frame in which the future was seen ready (0 = same frame). */
		int32 Frames = 0;
		double Ms = 0.0;
	};

	/** Why a chunk's physics collision was rewritten (plan C 3.6: stroke end, undo and redo; the cvar's throttled mid-stroke updates). */
	enum class ETerrainCollisionReason : uint8
	{
		Init,
		StrokeEnd,
		MidStroke,
		Undo,
		Redo,
		Load,
		Count
	};

	const TCHAR* CollisionReasonName(ETerrainCollisionReason Reason);

	/** Collision settings (plan C 3.6). Set before Initialize. */
	struct FTerrainCollisionOptions
	{
		/** FRealtimeMeshCollisionConfiguration::bShouldFastCookMeshes (measured both ways in C5). */
		bool bFastCook = true;
	};

	/** One finished per-chunk collision update: submit to the resolved future (plan C 3.6 "cook time"). */
	struct FTerrainCollisionCook
	{
		int32 Chunk = INDEX_NONE;
		ETerrainCollisionReason Reason = ETerrainCollisionReason::Init;
		/** Milliseconds and frames from SetCustomComplexMeshGeometry to the future's value. */
		double Ms = 0.0;
		int32 Frames = 0;
		/** ERealtimeMeshCollisionUpdateResult as an int (0 Unknown, 1 Updated, 2 Ignored, 3 Error). */
		uint8 Result = 0;
		/** TriMeshGeometries.Num() of the mesh's body when the future resolved (a cook counts only when > 0). */
		int32 TriMeshes = 0;
	};

	/** One collision batch (all chunks of one stroke end, undo, redo, ...): the game-thread cost of applying it (P5 "GT apply"). */
	struct FTerrainCollisionBatch
	{
		int32 Id = 0;
		ETerrainCollisionReason Reason = ETerrainCollisionReason::Init;
		int32 Chunks = 0;
		/** Game-thread ms to build the chunks' collision geometry and submit it. */
		double SubmitMs = 0.0;
		/** Game-thread ms the chunks spent recreating their physics state for this batch's new bodies (Destroy + Create). */
		double PhysicsStateMs = 0.0;
		bool bComplete = false;
		double GtApplyMs() const { return SubmitMs + PhysicsStateMs; }
	};

	/** A chunk's collision as the director's wait_collision sees it. */
	struct FTerrainChunkCollisionState
	{
		int32 Chunk = INDEX_NONE;
		/** A submitted update whose future has not resolved yet. */
		bool bPending = false;
		int64 Submits = 0;
		/** Result of the newest resolved update (ERealtimeMeshCollisionUpdateResult as an int). */
		uint8 LastResult = 0;
		/** TriMeshGeometries.Num() of the mesh's current body (0 = no body or no trimesh). */
		int32 TriMeshes = 0;
		/** The component's body instance is valid and uses the mesh's current body setup. */
		bool bPhysicsCurrent = false;
		/** Chunk centre in terrain metres (XY of the render rect's centre). */
		FVector2D CenterM = FVector2D::ZeroVector;
		/** The chunk's component (valid for the frame it was read in): the centre probe must hit this chunk, not a neighbour. */
		const UPrimitiveComponent* Component = nullptr;
	};

	/**
	 * Draws an FTerrainHeightfield as a grid of chunk components. Owns no height data: every call reads the caller's arrays.
	 * Threading: game thread only.
	 */
	class ITerrainChunkRenderer
	{
	public:
		virtual ~ITerrainChunkRenderer() = default;

		/** Create one component per chunk under Owner, build every chunk from HF and apply Material. False on failure (logged). */
		virtual bool Initialize(AActor* Owner, const FTerrainHeightfield& HF, UMaterialInterface* Material, ETerrainDrawType DrawType) = 0;

		/**
		 * Heights changed inside ChangedRect (vertex indices, half-open). Re-uploads positions and tangents of every chunk whose render
		 * rect meets ChangedRect grown by 1 vertex (normals use central differences), and widens chunk bounds when a vertex left them.
		 */
		virtual void UpdateHeights(const FTerrainHeightfield& HF, const FTerrainRect& ChangedRect) = 0;

		/** Recompute exact Z bounds of the chunks whose render rect meets VertexRect (stroke end, undo, redo) and push them to the scene. */
		virtual void RecomputeBounds(const FTerrainHeightfield& HF, const FTerrainRect& VertexRect) = 0;

		/** Rebuild every chunk from HF (after a load). */
		virtual void RebuildAll(const FTerrainHeightfield& HF) = 0;

		/** Tell the renderer a stroke is open or closed (attributes proxy recreations to strokes). */
		virtual void SetStrokeOpen(bool bOpen) = 0;

		virtual void SetVisible(bool bVisible) = 0;
		virtual void SetMaterial(UMaterialInterface* Material) = 0;

		/** True while a submitted mesh update has not been accepted by the render side yet. */
		virtual bool HasPendingWork() const = 0;

		/** Observe completed futures (records edit latencies). Call once per frame, game thread. */
		virtual void PollCompletions() = 0;
		/** Cost split of the most recent UpdateHeights. */
		virtual FTerrainUpdateTiming GetLastUpdateTiming() const = 0;
		/** Latencies of every ranged edit that finished since construction (oldest first). */
		virtual const TArray<FTerrainEditLatency>& GetEditLatencies() const = 0;

		virtual FTerrainRenderStats GetStats() const = 0;
		virtual void GetComponents(TArray<UPrimitiveComponent*>& Out) const = 0;

		// ---- physics collision (plan C 3.6) ----------------------------------------------------------------------
		/** Collision settings; call before Initialize. */
		virtual void SetCollisionOptions(const FTerrainCollisionOptions& InOptions) = 0;
		virtual FTerrainCollisionOptions GetCollisionOptions() const = 0;
		/**
		 * Rewrite the physics collision of every chunk whose render rect meets VertexRect from the CPU heights (render sections stay
		 * collision-free). Returns the number of chunks submitted.
		 */
		virtual int32 UpdateCollision(const FTerrainHeightfield& HF, const FTerrainRect& VertexRect, ETerrainCollisionReason Reason) = 0;
		/** True while any submitted collision update has not resolved. */
		virtual bool HasPendingCollision() const = 0;
		virtual void GetChunkCollisionStates(TArray<FTerrainChunkCollisionState>& Out) const = 0;
		/** Every resolved per-chunk update since construction (oldest first). */
		virtual const TArray<FTerrainCollisionCook>& GetCollisionCooks() const = 0;
		/** Every collision batch since construction (oldest first); bComplete once all its chunks resolved. */
		virtual const TArray<FTerrainCollisionBatch>& GetCollisionBatches() const = 0;
		virtual const TCHAR* GetName() const = 0;
	};
}

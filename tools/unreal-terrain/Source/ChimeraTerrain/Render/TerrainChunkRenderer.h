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

		virtual FTerrainRenderStats GetStats() const = 0;
		virtual void GetComponents(TArray<UPrimitiveComponent*>& Out) const = 0;
		virtual const TCHAR* GetName() const = 0;
	};
}

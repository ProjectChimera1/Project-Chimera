// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 1, 3.3: the RealtimeMeshComponent (RMC b8669a0) implementation of ITerrainChunkRenderer.
#pragma once

#include "CoreMinimal.h"
#include "Async/Future.h"
#include "Render/TerrainChunkRenderer.h"
#include "Core/RealtimeMeshDataStream.h"
#include "Core/RealtimeMeshKeys.h"
#include "RealtimeMeshCore.h"

class UTerrainChunkComponent;
class URealtimeMeshSimple;

namespace ChimeraTerrain
{
	/**
	 * Pure geometry of one chunk's RMC streams, shared by the renderer and the render tests.
	 * Layout (plan C 3.3): builder TRealtimeMeshBuilderLocal<uint32, FPackedNormal, FVector2f, 1>; vertex (lx, ly) of the chunk's
	 * render rect is element ly * W + lx (W = render-rect width); Position in Unreal cm = (100 (X - E), 100 (Y - E), 100 h);
	 * Tangents from the global central differences; TexCoord0 = global (X, Y) / (Width - 1), written once; triangles
	 * (BL, TL, TR) and (BL, TR, BR) as the FastUpdate example (RealtimeMeshExample_Simple_FastUpdate.cpp:115-129).
	 */
	namespace RmcTerrainGeometry
	{
		/** Build every stream of chunk Id from HF into Out (replaces its contents). */
		void BuildChunk(const FTerrainHeightfield& HF, int32 Id, RealtimeMesh::FRealtimeMeshStreamSet& Out);

		/**
		 * Rewrite Position and Tangents of the chunk's vertices inside VertexRect (global indices, half-open). OutRange is the
		 * contiguous element range of the touched rows [r0 * W, (r1 + 1) * W). OutMinZ/OutMaxZ span the written heights (metres).
		 * Returns false (nothing written) when the rect misses the chunk's render rect.
		 */
		bool WriteRect(const FTerrainHeightfield& HF, int32 Id, const FTerrainRect& VertexRect, RealtimeMesh::FRealtimeMeshStreamSet& Streams,
			FInt32Range& OutRange, float& OutMinZ, float& OutMaxZ, double* OutTangentSeconds = nullptr);

		/** The single buffer-set key every chunk mesh uses. */
		FRealtimeMeshBufferSetKey ChunkBufferSetKey();
	}

	class FRmcTerrainRenderer : public ITerrainChunkRenderer
	{
	public:
		virtual ~FRmcTerrainRenderer() override = default;

		virtual bool Initialize(AActor* Owner, const FTerrainHeightfield& HF, UMaterialInterface* Material, ETerrainDrawType DrawType) override;
		virtual void UpdateHeights(const FTerrainHeightfield& HF, const FTerrainRect& ChangedRect) override;
		virtual void RecomputeBounds(const FTerrainHeightfield& HF, const FTerrainRect& VertexRect) override;
		virtual void RebuildAll(const FTerrainHeightfield& HF) override;
		virtual void SetStrokeOpen(bool bOpen) override;
		virtual void SetVisible(bool bVisible) override;
		virtual void SetMaterial(UMaterialInterface* Material) override;
		virtual bool HasPendingWork() const override;
		virtual void PollCompletions() override;
		virtual FTerrainUpdateTiming GetLastUpdateTiming() const override { return LastTiming; }
		virtual const TArray<FTerrainEditLatency>& GetEditLatencies() const override { return EditLatencies; }
		virtual FTerrainRenderStats GetStats() const override;
		virtual void GetComponents(TArray<UPrimitiveComponent*>& Out) const override;
		virtual const TCHAR* GetName() const override { return TEXT("rmc"); }

		/** Chunk component by id (tests). */
		UTerrainChunkComponent* GetChunkComponent(int32 Id) const;
		/** The chunk's mesh (tests read its streams back with ProcessMesh). */
		URealtimeMeshSimple* GetChunkMesh(int32 Id) const;

	private:
		/** A submitted mesh update. Ranged edits are timed from submit to the frame the future is seen ready (P2). */
		struct FPendingUpdate
		{
			TFuture<ERealtimeMeshProxyUpdateStatus> Future;
			uint64 SubmitFrame = 0;
			double SubmitSeconds = 0.0;
			bool bRanged = false;

			FPendingUpdate() = default;
			FPendingUpdate(TFuture<ERealtimeMeshProxyUpdateStatus>&& InFuture, bool bInRanged);
			bool IsDone() const { return !Future.IsValid() || Future.IsReady(); }
		};

		struct FChunk
		{
			TWeakObjectPtr<UTerrainChunkComponent> Component;
			TWeakObjectPtr<URealtimeMeshSimple> Mesh;
			TArray<FPendingUpdate> Pending;
		};

		void BuildChunkMesh(const FTerrainHeightfield& HF, int32 Id, bool bCreate);
		void PrunePending();

		TArray<FChunk> ChunkData;
		ETerrainDrawType Draw = ETerrainDrawType::Dynamic;
		FTerrainRenderStats Stats;
		TWeakObjectPtr<UMaterialInterface> CurrentMaterial;
		FTerrainUpdateTiming LastTiming;
		TArray<FTerrainEditLatency> EditLatencies;
	};
}

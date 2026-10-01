// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 1, 3.3: the RealtimeMeshComponent (RMC b8669a0) implementation of ITerrainChunkRenderer.
#pragma once

#include "CoreMinimal.h"
#include "Async/Future.h"
#include "Render/TerrainChunkRenderer.h"
#include "Core/RealtimeMeshDataStream.h"
#include "Core/RealtimeMeshKeys.h"
#include "RealtimeMeshCore.h"
#include "Core/RealtimeMeshCollision.h"

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

		/**
		 * The chunk's physics collision mesh (plan C 3.6): the same vertices (Unreal cm, render-rect order, written by the same position
		 * function) and the same triangles as the render streams, so collision and render geometry are identical.
		 */
		void BuildCollisionMesh(const FTerrainHeightfield& HF, int32 Id, FRealtimeMeshCollisionMesh& Out);
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

		virtual void SetCollisionOptions(const FTerrainCollisionOptions& InOptions) override { CollisionOptions = InOptions; }
		virtual FTerrainCollisionOptions GetCollisionOptions() const override { return CollisionOptions; }
		virtual int32 UpdateCollision(const FTerrainHeightfield& HF, const FTerrainRect& VertexRect, ETerrainCollisionReason Reason) override;
		virtual bool HasPendingCollision() const override;
		virtual void GetChunkCollisionStates(TArray<FTerrainChunkCollisionState>& Out) const override;
		virtual const TArray<FTerrainCollisionCook>& GetCollisionCooks() const override { return CollisionCooks; }
		virtual const TArray<FTerrainCollisionBatch>& GetCollisionBatches() const override { return CollisionBatches; }

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

		/** Filled by the collision future's continuation, which runs on the game thread (RealtimeMeshManaged.cpp ProcessEndOfFrameUpdates). */
		struct FCollisionOutcome
		{
			bool bDone = false;
			uint8 Result = 0;
			double DoneSeconds = 0.0;
			uint64 DoneFrame = 0;
			/** The component's physics-state seconds read inside the continuation, right after ApplyCollisionUpdate's broadcast. */
			double PhysicsSecondsAtDone = -1.0;
		};

		/** A submitted per-chunk collision update. */
		struct FPendingCollision
		{
			TSharedRef<FCollisionOutcome> Outcome = MakeShared<FCollisionOutcome>();
			double SubmitSeconds = 0.0;
			uint64 SubmitFrame = 0;
			ETerrainCollisionReason Reason = ETerrainCollisionReason::Init;
			int32 BatchIndex = INDEX_NONE;
			/** The component's physics-state seconds at submit; the batch's GT apply is the growth until the update resolves. */
			double PhysicsSecondsAtSubmit = 0.0;
		};

		struct FChunk
		{
			TWeakObjectPtr<UTerrainChunkComponent> Component;
			TWeakObjectPtr<URealtimeMeshSimple> Mesh;
			TArray<FPendingUpdate> Pending;
			TArray<FPendingCollision> PendingCollision;
			int64 CollisionSubmits = 0;
			uint8 LastCollisionResult = 0;
			/**
			 * Physics-state seconds already charged to a batch. Each resolve is charged only the time after this, so two overlapping updates
			 * of one chunk (merged in one frame, or mid-stroke then stroke end) never count the same recreate twice.
			 */
			double PhysicsSecondsCharged = 0.0;
		};

		void BuildChunkMesh(const FTerrainHeightfield& HF, int32 Id, bool bCreate);
		void PrunePending();
		void PruneCollision();

		TArray<FChunk> ChunkData;
		ETerrainDrawType Draw = ETerrainDrawType::Dynamic;
		FTerrainRenderStats Stats;
		TWeakObjectPtr<UMaterialInterface> CurrentMaterial;
		FTerrainUpdateTiming LastTiming;
		TArray<FTerrainEditLatency> EditLatencies;
		FTerrainCollisionOptions CollisionOptions;
		TArray<FTerrainCollisionCook> CollisionCooks;
		TArray<FTerrainCollisionBatch> CollisionBatches;
		/** Unresolved chunk updates per batch (index = batch index). */
		TArray<int32> BatchOpen;
	};
}

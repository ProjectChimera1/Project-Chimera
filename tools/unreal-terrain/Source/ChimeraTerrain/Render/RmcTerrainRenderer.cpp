// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Render/RmcTerrainRenderer.h"

#include "ChimeraTerrain.h"
#include "Render/TerrainChunkComponent.h"
#include "RealtimeMeshSimple.h"
#include "Core/RealtimeMeshBuilder.h"
#include "Core/RealtimeMeshBufferSetConfig.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformTime.h"
#include "Materials/MaterialInterface.h"

using namespace RealtimeMesh;

namespace ChimeraTerrain
{
	namespace
	{
		/** Plan C 3.3: FVector2f texcoords (FVector2DHalf would put the splat 0.3 texel off near u = 1). */
		using FChunkBuilder = TRealtimeMeshBuilderLocal<uint32, FPackedNormal, FVector2f, 1>;
		using FTangentElement = FChunkBuilder::TangentStreamType;

		FVector3f VertexPositionCm(const FTerrainHeightfield& HF, int32 X, int32 Y)
		{
			return FVector3f(100.0f * HF.VertexToWorld(X), 100.0f * HF.VertexToWorld(Y), 100.0f * HF.GetHeight(X, Y));
		}
	}

	namespace RmcTerrainGeometry
	{
		FRealtimeMeshBufferSetKey ChunkBufferSetKey()
		{
			return FRealtimeMeshBufferSetKey::Create(0, FName(TEXT("ChimeraTerrainChunk")));
		}

		bool WriteRect(const FTerrainHeightfield& HF, int32 Id, const FTerrainRect& VertexRect, FRealtimeMeshStreamSet& Streams,
			FInt32Range& OutRange, float& OutMinZ, float& OutMaxZ, double* OutTangentSeconds)
		{
			const FTerrainRect R = HF.ChunkRenderRect(Id);
			const FTerrainRect I = VertexRect.Intersect(R);
			if (I.IsEmpty())
			{
				return false;
			}
			FRealtimeMeshStream* PosStream = Streams.Find(FRealtimeMeshStreams::Position);
			FRealtimeMeshStream* TanStream = Streams.Find(FRealtimeMeshStreams::Tangents);
			check(PosStream && TanStream);
			TArrayView<FVector3f> Pos = PosStream->GetArrayView<FVector3f>();
			TArrayView<FTangentElement> Tan = TanStream->GetArrayView<FTangentElement>();
			const int32 W = R.Width();
			check(Pos.Num() == W * R.Height() && Tan.Num() == Pos.Num());

			// Pass 1: positions and the height range. Pass 2: normals and tangents (timed on its own, plan C 3.8 "normals ms").
			// The values written are identical to a single fused pass; one code path writes every tangent (build and update),
			// so shared border vertices stay bit-equal across chunks.
			float MinZ = TNumericLimits<float>::Max();
			float MaxZ = -TNumericLimits<float>::Max();
			for (int32 Y = I.Y0; Y < I.Y1; ++Y)
			{
				const int32 Row = (Y - R.Y0) * W;
				for (int32 X = I.X0; X < I.X1; ++X)
				{
					const float H = HF.GetHeight(X, Y);
					MinZ = FMath::Min(MinZ, H);
					MaxZ = FMath::Max(MaxZ, H);
					Pos[Row + (X - R.X0)] = VertexPositionCm(HF, X, Y);
				}
			}
			const double T0 = OutTangentSeconds ? FPlatformTime::Seconds() : 0.0;
			for (int32 Y = I.Y0; Y < I.Y1; ++Y)
			{
				const int32 Row = (Y - R.Y0) * W;
				for (int32 X = I.X0; X < I.X1; ++X)
				{
					Tan[Row + (X - R.X0)] = FTangentElement(HF.GetNormal(X, Y), HF.GetTangent(X, Y));
				}
			}
			if (OutTangentSeconds)
			{
				*OutTangentSeconds += FPlatformTime::Seconds() - T0;
			}
			OutRange = FInt32Range((I.Y0 - R.Y0) * W, (I.Y1 - R.Y0) * W);
			OutMinZ = MinZ;
			OutMaxZ = MaxZ;
			return true;
		}

		void BuildChunk(const FTerrainHeightfield& HF, int32 Id, FRealtimeMeshStreamSet& Out)
		{
			Out = FRealtimeMeshStreamSet();
			const FTerrainRect R = HF.ChunkRenderRect(Id);
			const int32 W = R.Width();
			const int32 H = R.Height();
			const float UvScale = 1.0f / static_cast<float>(HF.Width() - 1);
			{
				FChunkBuilder Builder(Out);
				Builder.EnableTangents();
				Builder.EnableTexCoords();
				Builder.ReserveNumVertices(W * H);
				Builder.ReserveNumTriangles((W - 1) * (H - 1) * 2);
				for (int32 Y = R.Y0; Y < R.Y1; ++Y)
				{
					for (int32 X = R.X0; X < R.X1; ++X)
					{
						// TexCoord0 = global XY, written once and never edited (plan C 3.3).
						Builder.AddVertex(VertexPositionCm(HF, X, Y))
							.SetNormalAndTangent(FVector3f(0.0f, 0.0f, 1.0f), FVector3f(1.0f, 0.0f, 0.0f))
							.SetTexCoord(FVector2f(static_cast<float>(X) * UvScale, static_cast<float>(Y) * UvScale));
					}
				}
				for (int32 Ly = 0; Ly < H - 1; ++Ly)
				{
					for (int32 Lx = 0; Lx < W - 1; ++Lx)
					{
						const int32 BottomLeft = Ly * W + Lx;
						const int32 BottomRight = BottomLeft + 1;
						const int32 TopLeft = BottomLeft + W;
						const int32 TopRight = TopLeft + 1;
						Builder.AddTriangle(BottomLeft, TopLeft, TopRight);
						Builder.AddTriangle(BottomLeft, TopRight, BottomRight);
					}
				}
			}
			// Positions and tangents go through the same writer the ranged updates use.
			FInt32Range Range(0, 0);
			float MinZ = 0.0f;
			float MaxZ = 0.0f;
			WriteRect(HF, Id, R, Out, Range, MinZ, MaxZ);
		}
	}

	// ---- FRmcTerrainRenderer ----------------------------------------------------------------------------------

	FRmcTerrainRenderer::FPendingUpdate::FPendingUpdate(TFuture<ERealtimeMeshProxyUpdateStatus>&& InFuture, bool bInRanged)
		: Future(MoveTemp(InFuture))
		, SubmitFrame(GFrameCounter)
		, SubmitSeconds(FPlatformTime::Seconds())
		, bRanged(bInRanged)
	{
	}

	bool FRmcTerrainRenderer::Initialize(AActor* Owner, const FTerrainHeightfield& HF, UMaterialInterface* Material, ETerrainDrawType DrawType)
	{
		check(IsInGameThread());
		if (!Owner || !HF.IsInitialized())
		{
			UE_LOG(LogChimeraTerrain, Error, TEXT("FRmcTerrainRenderer::Initialize: no owner or heightfield"));
			return false;
		}
		Draw = DrawType;
		CurrentMaterial = Material;
		ChunkData.SetNum(HF.NumChunks());
		Stats = FTerrainRenderStats();
		Stats.Chunks = HF.NumChunks();
		for (int32 Id = 0; Id < HF.NumChunks(); ++Id)
		{
			const FName Name(*FString::Printf(TEXT("TerrainChunk_%03d"), Id));
			UTerrainChunkComponent* Comp = NewObject<UTerrainChunkComponent>(Owner, Name);
			Comp->SetupAttachment(Owner->GetRootComponent());
			Comp->RegisterComponent();
			Owner->AddInstanceComponent(Comp);
			URealtimeMeshSimple* Mesh = Comp->InitializeRealtimeMesh<URealtimeMeshSimple>();
			Mesh->SetupMaterialSlot(0, FName(TEXT("Terrain")), Material);
			if (Material)
			{
				Comp->SetMaterial(0, Material);
			}

			const FTerrainRect R = HF.ChunkRenderRect(Id);
			const float E = static_cast<float>(HF.HalfExtentM());
			Comp->SetFootprint(Id, FVector2D(100.0 * (R.X0 - E), 100.0 * (R.Y0 - E)), FVector2D(100.0 * (R.X1 - 1 - E), 100.0 * (R.Y1 - 1 - E)));
			Stats.Triangles += static_cast<int64>(R.Width() - 1) * (R.Height() - 1) * 2;

			ChunkData[Id].Component = Comp;
			ChunkData[Id].Mesh = Mesh;
			BuildChunkMesh(HF, Id, true);
		}
		UE_LOG(LogChimeraTerrain, Display, TEXT("renderer=rmc chunks=%d chunk_quads=%d triangles=%lld draw_type=%s"),
			Stats.Chunks, HF.ChunkQuads(), Stats.Triangles, Draw == ETerrainDrawType::Dynamic ? TEXT("Dynamic") : TEXT("Static"));
		return true;
	}

	void FRmcTerrainRenderer::BuildChunkMesh(const FTerrainHeightfield& HF, int32 Id, bool bCreate)
	{
		FChunk& C = ChunkData[Id];
		URealtimeMeshSimple* Mesh = C.Mesh.Get();
		UTerrainChunkComponent* Comp = C.Component.Get();
		if (!Mesh || !Comp)
		{
			return;
		}
		FRealtimeMeshStreamSet Streams;
		RmcTerrainGeometry::BuildChunk(HF, Id, Streams);
		const FRealtimeMeshBufferSetConfig Config(Draw == ETerrainDrawType::Dynamic ? ERealtimeMeshSectionDrawType::Dynamic : ERealtimeMeshSectionDrawType::Static);
		if (bCreate)
		{
			C.Pending.Add(FPendingUpdate(Mesh->CreateBufferSet(RmcTerrainGeometry::ChunkBufferSetKey(), MoveTemp(Streams), Config), false));
		}
		else
		{
			C.Pending.Add(FPendingUpdate(Mesh->UpdateBufferSet(RmcTerrainGeometry::ChunkBufferSetKey(), MoveTemp(Streams)), false));
		}
		float MinZ = 0.0f;
		float MaxZ = 0.0f;
		HF.GetChunkZRange(Id, MinZ, MaxZ);
		Comp->SetExactZRange(MinZ, MaxZ);
	}

	void FRmcTerrainRenderer::UpdateHeights(const FTerrainHeightfield& HF, const FTerrainRect& ChangedRect)
	{
		if (ChangedRect.IsEmpty())
		{
			return;
		}
		const double CallStart = FPlatformTime::Seconds();
		double TangentSeconds = 0.0;
		int32 VerticesWritten = 0;
		PrunePending();
		// Normals use central differences: a changed vertex alters its neighbours' normals too.
		const FTerrainRect Rn = ChangedRect.Expanded(1).Intersect(FTerrainRect(0, 0, HF.Width(), HF.Width()));
		TArray<int32> Ids;
		HF.ChunksRenderOverlappingRect(Rn, Ids);
		const FRealtimeMeshBufferSetKey Key = RmcTerrainGeometry::ChunkBufferSetKey();
		for (const int32 Id : Ids)
		{
			FChunk& C = ChunkData[Id];
			URealtimeMeshSimple* Mesh = C.Mesh.Get();
			UTerrainChunkComponent* Comp = C.Component.Get();
			if (!Mesh || !Comp)
			{
				continue;
			}
			bool bWrote = false;
			float MinZ = 0.0f;
			float MaxZ = 0.0f;
			int64 Elements = 0;
			C.Pending.Add(FPendingUpdate(Mesh->EditMeshInPlaceRanged(Key, [&](FRealtimeMeshStreamSet& Streams) -> TMap<FRealtimeMeshStreamKey, FInt32Range>
			{
				TMap<FRealtimeMeshStreamKey, FInt32Range> Updated;
				FInt32Range Range(0, 0);
				if (RmcTerrainGeometry::WriteRect(HF, Id, Rn, Streams, Range, MinZ, MaxZ, &TangentSeconds))
				{
					bWrote = true;
					Elements = Range.GetUpperBoundValue() - Range.GetLowerBoundValue();
					Updated.Add(FRealtimeMeshStreams::Position, Range);
					Updated.Add(FRealtimeMeshStreams::Tangents, Range);
				}
				return Updated;
			}), true));
			if (bWrote)
			{
				++Stats.RangedEdits;
				Stats.UploadedVertices += Elements;
				VerticesWritten += static_cast<int32>(Elements);
				if (Comp->NotifyTickZRange(MinZ, MaxZ))
				{
					++Stats.BoundsWidenings;
				}
			}
		}
		const double TotalSeconds = FPlatformTime::Seconds() - CallStart;
		LastTiming.NormalsMs = TangentSeconds * 1000.0;
		LastTiming.SubmitMs = FMath::Max(0.0, TotalSeconds - TangentSeconds) * 1000.0;
		LastTiming.Vertices = VerticesWritten;
	}

	void FRmcTerrainRenderer::RecomputeBounds(const FTerrainHeightfield& HF, const FTerrainRect& VertexRect)
	{
		TArray<int32> Ids;
		HF.ChunksRenderOverlappingRect(VertexRect.Expanded(1).Intersect(FTerrainRect(0, 0, HF.Width(), HF.Width())), Ids);
		for (const int32 Id : Ids)
		{
			if (UTerrainChunkComponent* Comp = ChunkData[Id].Component.Get())
			{
				float MinZ = 0.0f;
				float MaxZ = 0.0f;
				HF.GetChunkZRange(Id, MinZ, MaxZ);
				Comp->SetExactZRange(MinZ, MaxZ);
				++Stats.BoundsExactPushes;
			}
		}
	}

	void FRmcTerrainRenderer::RebuildAll(const FTerrainHeightfield& HF)
	{
		check(ChunkData.Num() == HF.NumChunks());
		for (int32 Id = 0; Id < ChunkData.Num(); ++Id)
		{
			BuildChunkMesh(HF, Id, false);
			++Stats.BoundsExactPushes;
		}
	}

	void FRmcTerrainRenderer::SetStrokeOpen(bool bOpen)
	{
		UTerrainChunkComponent::bStrokeOpen = bOpen;
	}

	void FRmcTerrainRenderer::SetVisible(bool bVisible)
	{
		for (const FChunk& C : ChunkData)
		{
			if (UTerrainChunkComponent* Comp = C.Component.Get())
			{
				Comp->SetVisibility(bVisible);
			}
		}
	}

	void FRmcTerrainRenderer::SetMaterial(UMaterialInterface* Material)
	{
		CurrentMaterial = Material;
		for (const FChunk& C : ChunkData)
		{
			if (UTerrainChunkComponent* Comp = C.Component.Get())
			{
				Comp->SetMaterial(0, Material);
			}
		}
	}

	void FRmcTerrainRenderer::PrunePending()
	{
		// Every finished ranged edit is timed on the way out: frames and ms from the submit to the frame it was seen ready.
		const uint64 NowFrame = GFrameCounter;
		const double NowSeconds = FPlatformTime::Seconds();
		for (FChunk& C : ChunkData)
		{
			C.Pending.RemoveAll([&](const FPendingUpdate& U)
			{
				if (!U.IsDone())
				{
					return false;
				}
				if (U.bRanged && U.Future.IsValid())
				{
					FTerrainEditLatency L;
					L.Frames = static_cast<int32>(NowFrame - U.SubmitFrame);
					L.Ms = (NowSeconds - U.SubmitSeconds) * 1000.0;
					EditLatencies.Add(L);
				}
				return true;
			});
		}
	}

	void FRmcTerrainRenderer::PollCompletions()
	{
		PrunePending();
	}

	bool FRmcTerrainRenderer::HasPendingWork() const
	{
		for (const FChunk& C : ChunkData)
		{
			for (const FPendingUpdate& U : C.Pending)
			{
				if (!U.IsDone())
				{
					return true;
				}
			}
		}
		return false;
	}

	FTerrainRenderStats FRmcTerrainRenderer::GetStats() const
	{
		FTerrainRenderStats S = Stats;
		S.ProxyRecreates = 0;
		for (const FChunk& C : ChunkData)
		{
			if (const UTerrainChunkComponent* Comp = C.Component.Get())
			{
				S.ProxyRecreates += FMath::Max(0, Comp->GetProxyCreates() - 1);
			}
		}
		S.ProxyRecreatesDuringStrokes = UTerrainChunkComponent::ProxyCreatesDuringStrokes;
		return S;
	}

	void FRmcTerrainRenderer::GetComponents(TArray<UPrimitiveComponent*>& Out) const
	{
		for (const FChunk& C : ChunkData)
		{
			if (UTerrainChunkComponent* Comp = C.Component.Get())
			{
				Out.Add(Comp);
			}
		}
	}

	UTerrainChunkComponent* FRmcTerrainRenderer::GetChunkComponent(int32 Id) const
	{
		return ChunkData.IsValidIndex(Id) ? ChunkData[Id].Component.Get() : nullptr;
	}

	URealtimeMeshSimple* FRmcTerrainRenderer::GetChunkMesh(int32 Id) const
	{
		return ChunkData.IsValidIndex(Id) ? ChunkData[Id].Mesh.Get() : nullptr;
	}
}

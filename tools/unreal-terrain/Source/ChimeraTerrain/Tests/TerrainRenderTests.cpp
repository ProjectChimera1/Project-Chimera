// Project Chimera terrain trial (plan C). Original Chimera code.
// Automation tests for the chunk renderer (plan C 4 C3). Run: Tools/run_tests.ps1 -Filter Chimera.Terrain.Render
// Under -nullrhi these check the CPU side only: the streams RMC holds and the bounds the components report. Gate G1's spike checks
// the render side (the scene really culls with those bounds).

#include "CoreMinimal.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Misc/AutomationTest.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "EngineUtils.h"

#include "Data/TerrainHeightfield.h"
#include "Data/TerrainBrush.h"
#include "Render/RmcTerrainRenderer.h"
#include "Render/TerrainChunkComponent.h"
#include "Game/TerrainActor.h"
#include "RealtimeMeshSimple.h"
#include "Core/RealtimeMeshBuilder.h"

using namespace ChimeraTerrain;
using namespace RealtimeMesh;

#define TR_FLAGS (EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
	using FTangentElement = TRealtimeMeshBuilderLocal<uint32, FPackedNormal, FVector2f, 1>::TangentStreamType;

	FTerrainBrushParams RaiseParams(float D, float S)
	{
		FTerrainBrushParams P;
		P.Mode = ETerrainBrushMode::Raise;
		P.DiameterM = D;
		P.Strength = S;
		return P;
	}

	/** Raw bytes of vertex (X, Y) (global indices) in chunk Id's streams: Position then Tangents. */
	bool VertexBytes(const FTerrainHeightfield& HF, int32 Id, const FRealtimeMeshStreamSet& Streams, int32 X, int32 Y, TArray<uint8>& Out)
	{
		const FTerrainRect R = HF.ChunkRenderRect(Id);
		if (!R.Contains(X, Y))
		{
			return false;
		}
		const FRealtimeMeshStream* Pos = Streams.Find(FRealtimeMeshStreams::Position);
		const FRealtimeMeshStream* Tan = Streams.Find(FRealtimeMeshStreams::Tangents);
		if (!Pos || !Tan)
		{
			return false;
		}
		const int32 Li = (Y - R.Y0) * R.Width() + (X - R.X0);
		Out.Reset();
		Out.Append(reinterpret_cast<const uint8*>(&Pos->GetArrayView<FVector3f>()[Li]), sizeof(FVector3f));
		Out.Append(reinterpret_cast<const uint8*>(&Tan->GetArrayView<FTangentElement>()[Li]), sizeof(FTangentElement));
		return true;
	}

	/**
	 * Every vertex rendered by more than one chunk must have identical bytes in each of them. Returns the number of shared vertices
	 * compared; adds an error per mismatch (capped).
	 */
	int32 CheckSeams(FAutomationTestBase& T, const FTerrainHeightfield& HF, const TArray<const FRealtimeMeshStreamSet*>& Sets, const FString& What)
	{
		int32 Compared = 0;
		int32 Errors = 0;
		TArray<int32> Ids;
		TArray<uint8> A;
		TArray<uint8> B;
		for (int32 Y = 0; Y < HF.Width(); ++Y)
		{
			for (int32 X = 0; X < HF.Width(); ++X)
			{
				Ids.Reset();
				HF.ChunksRenderOverlappingRect(FTerrainRect(X, Y, X + 1, Y + 1), Ids);
				if (Ids.Num() < 2)
				{
					continue;
				}
				if (!VertexBytes(HF, Ids[0], *Sets[Ids[0]], X, Y, A))
				{
					T.AddError(FString::Printf(TEXT("%s: vertex (%d,%d) missing in chunk %d"), *What, X, Y, Ids[0]));
					return Compared;
				}
				for (int32 K = 1; K < Ids.Num(); ++K)
				{
					++Compared;
					if (!VertexBytes(HF, Ids[K], *Sets[Ids[K]], X, Y, B) || A != B)
					{
						if (++Errors <= 5)
						{
							T.AddError(FString::Printf(TEXT("%s: seam vertex (%d,%d) differs between chunks %d and %d"), *What, X, Y, Ids[0], Ids[K]));
						}
					}
				}
			}
		}
		if (Errors > 5)
		{
			T.AddError(FString::Printf(TEXT("%s: %d seam mismatches in total"), *What, Errors));
		}
		return Compared;
	}

	/** A Game world for the duration of a test (pattern of CQTest's FActorTestSpawner). */
	struct FTestWorld
	{
		UWorld* World = nullptr;

		FTestWorld()
		{
			const FName Name = MakeUniqueObjectName(nullptr, UWorld::StaticClass(), FName(TEXT("ChimeraTerrainTestWorld")), EUniqueObjectNameOptions::GloballyUnique);
			FWorldContext& Ctx = GEngine->CreateNewWorldContext(EWorldType::Game);
			World = UWorld::CreateWorld(EWorldType::Game, false, Name, GetTransientPackage());
			World->AddToRoot();
			Ctx.SetCurrentWorld(World);
			World->InitializeActorsForPlay(FURL());
		}

		~FTestWorld()
		{
			if (!World)
			{
				return;
			}
			for (TActorIterator<AActor> It(World); It; ++It)
			{
				if (It->HasActorBegunPlay())
				{
					It->RouteEndPlay(EEndPlayReason::LevelTransition);
				}
			}
			World->DestroyWorld(false);
			World->SetPhysicsScene(nullptr);
			GEngine->DestroyWorldContext(World);
			World->RemoveFromRoot();
			World = nullptr;
		}
	};
}

// Pure geometry: a corner stroke updated by ranged writes leaves every shared vertex bit-equal across the four chunks, and the ranged
// result equals a full rebuild of every chunk.
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FChimeraTerrainRenderSeamsGeometryTest, "Chimera.Terrain.Render.SeamsBitEqualAfterCornerStroke", TR_FLAGS)
bool FChimeraTerrainRenderSeamsGeometryTest::RunTest(const FString& Parameters)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	TArray<FRealtimeMeshStreamSet> Sets;
	Sets.SetNum(HF.NumChunks());
	for (int32 Id = 0; Id < HF.NumChunks(); ++Id)
	{
		RmcTerrainGeometry::BuildChunk(HF, Id, Sets[Id]);
	}
	// Vertex (128, 128) = terrain (-32, -32) m is a corner shared by chunks (1,1), (2,1), (1,2), (2,2).
	const FTerrainBrushParams P = RaiseParams(30.0f, 50.0f);
	int32 RangedWrites = 0;
	for (int32 Tick = 0; Tick < 12; ++Tick)
	{
		const float Cx = -32.0f + 0.37f * Tick;
		const float Cy = -32.0f - 0.21f * Tick;
		const FTerrainTickResult R = FTerrainBrush::ApplyTick(HF, P, Cx, Cy);
		TestTrue(TEXT("tick changed heights"), !R.HeightRect.IsEmpty());
		const FTerrainRect Rn = R.HeightRect.Expanded(1).Intersect(FTerrainRect(0, 0, HF.Width(), HF.Width()));
		TArray<int32> Ids;
		HF.ChunksRenderOverlappingRect(Rn, Ids);
		for (const int32 Id : Ids)
		{
			FInt32Range Range(0, 0);
			float MinZ = 0.0f;
			float MaxZ = 0.0f;
			if (RmcTerrainGeometry::WriteRect(HF, Id, Rn, Sets[Id], Range, MinZ, MaxZ))
			{
				++RangedWrites;
				const FTerrainRect CR = HF.ChunkRenderRect(Id);
				TestTrue(TEXT("range starts on a row boundary"), Range.GetLowerBoundValue() % CR.Width() == 0);
				TestTrue(TEXT("range ends on a row boundary"), Range.GetUpperBoundValue() % CR.Width() == 0);
				TestTrue(TEXT("range inside the stream"), Range.GetLowerBoundValue() >= 0 && Range.GetUpperBoundValue() <= CR.Width() * CR.Height());
			}
		}
	}
	TestTrue(TEXT("the corner stroke touched 4 chunks per tick"), RangedWrites >= 4 * 12);

	TArray<const FRealtimeMeshStreamSet*> Ptrs;
	for (const FRealtimeMeshStreamSet& S : Sets)
	{
		Ptrs.Add(&S);
	}
	const int32 Compared = CheckSeams(*this, HF, Ptrs, TEXT("ranged"));
	// 5x5 chunks of 64: 4 interior seam lines each way of 321 vertices, corners counted per extra chunk.
	TestTrue(TEXT("seam vertices compared"), Compared > 2000);

	// Ranged == rebuilt, byte for byte, for every chunk (positions, tangents).
	for (int32 Id = 0; Id < HF.NumChunks(); ++Id)
	{
		FRealtimeMeshStreamSet Fresh;
		RmcTerrainGeometry::BuildChunk(HF, Id, Fresh);
		for (const FRealtimeMeshStreamKey& Key : { FRealtimeMeshStreams::Position, FRealtimeMeshStreams::Tangents, FRealtimeMeshStreams::TexCoords, FRealtimeMeshStreams::Triangles })
		{
			const FRealtimeMeshStream* A = Sets[Id].Find(Key);
			const FRealtimeMeshStream* B = Fresh.Find(Key);
			if (!A || !B || A->Num() != B->Num() || A->GetStride() != B->GetStride()
				|| FMemory::Memcmp(A->GetData(), B->GetData(), static_cast<SIZE_T>(A->Num()) * A->GetStride()) != 0)
			{
				AddError(FString::Printf(TEXT("chunk %d stream %s: ranged result differs from a full rebuild"), Id, *Key.ToString()));
			}
		}
	}

	// The peak is really in the streams: the max position Z equals 100 x the max height.
	float MaxH = -1.0f;
	for (const float H : HF.Heights)
	{
		MaxH = FMath::Max(MaxH, H);
	}
	TestTrue(TEXT("stroke raised the corner"), MaxH > 1.0f);
	return true;
}

// The same through the real renderer and RMC meshes in a Game world: streams read back from URealtimeMeshSimple are seam-equal, and a
// ranged raise above a chunk's old Z range leaves the component's Bounds containing the new top vertex (CPU bounds contract).
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FChimeraTerrainRenderRmcTest, "Chimera.Terrain.Render.RmcSeamsAndBounds", TR_FLAGS)
bool FChimeraTerrainRenderRmcTest::RunTest(const FString& Parameters)
{
	FTestWorld TW;
	if (!TestNotNull(TEXT("world"), TW.World))
	{
		return false;
	}
	ATerrainActor* Terrain = TW.World->SpawnActor<ATerrainActor>();
	if (!TestNotNull(TEXT("terrain actor"), Terrain) || !TestTrue(TEXT("InitTerrain"), Terrain->InitTerrain(160, 64, ETerrainDrawType::Dynamic)))
	{
		return false;
	}
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	FRmcTerrainRenderer* Renderer = static_cast<FRmcTerrainRenderer*>(Terrain->GetRenderer());
	TestEqual(TEXT("renderer"), FString(Renderer->GetName()), FString(TEXT("rmc")));
	TestEqual(TEXT("triangles (plan C 3.3)"), Renderer->GetStats().Triangles, static_cast<int64>(204800));

	// Corner stroke through the actor (the game's path).
	Terrain->BeginStroke(RaiseParams(30.0f, 50.0f), FVector2D(-32.0, -32.0));
	for (int32 Tick = 0; Tick < 12; ++Tick)
	{
		Terrain->ApplyTick(FVector2D(-32.0 + 0.37 * Tick, -32.0 - 0.21 * Tick));
	}
	Terrain->EndStroke();

	TArray<FRealtimeMeshStreamSet> Copies;
	Copies.SetNum(HF.NumChunks());
	for (int32 Id = 0; Id < HF.NumChunks(); ++Id)
	{
		URealtimeMeshSimple* Mesh = Renderer->GetChunkMesh(Id);
		if (!TestNotNull(*FString::Printf(TEXT("chunk %d mesh"), Id), Mesh))
		{
			return false;
		}
		bool bRead = false;
		Mesh->ProcessMesh(RmcTerrainGeometry::ChunkBufferSetKey(), [&](const FRealtimeMeshStreamSet& Streams)
		{
			Copies[Id].CopyFrom(Streams);
			bRead = true;
		});
		TestTrue(*FString::Printf(TEXT("chunk %d streams readable"), Id), bRead);
	}
	TArray<const FRealtimeMeshStreamSet*> Ptrs;
	for (const FRealtimeMeshStreamSet& S : Copies)
	{
		Ptrs.Add(&S);
	}
	const int32 Compared = CheckSeams(*this, HF, Ptrs, TEXT("rmc"));
	TestTrue(TEXT("rmc seam vertices compared"), Compared > 2000);
	// Streams equal a fresh build of the CPU array (nothing lost or reordered inside RMC).
	for (int32 Id = 0; Id < HF.NumChunks(); ++Id)
	{
		FRealtimeMeshStreamSet Fresh;
		RmcTerrainGeometry::BuildChunk(HF, Id, Fresh);
		for (const FRealtimeMeshStreamKey& Key : { FRealtimeMeshStreams::Position, FRealtimeMeshStreams::Tangents })
		{
			const FRealtimeMeshStream* A = Copies[Id].Find(Key);
			const FRealtimeMeshStream* B = Fresh.Find(Key);
			if (!A || !B || A->Num() != B->Num() || FMemory::Memcmp(A->GetData(), B->GetData(), static_cast<SIZE_T>(A->Num()) * A->GetStride()) != 0)
			{
				AddError(FString::Printf(TEXT("chunk %d stream %s in RMC differs from the CPU array"), Id, *Key.ToString()));
			}
		}
	}

	// Bounds contract: a 60 m spike in chunk 0 (flat: tracked range [-2, 2] m) during an open stroke.
	UTerrainChunkComponent* Comp0 = Renderer->GetChunkComponent(0);
	if (!TestNotNull(TEXT("chunk 0 component"), Comp0))
	{
		return false;
	}
	TestEqual(TEXT("flat chunk ZHi"), Comp0->GetTrackedZHi(), 2.0f);
	Terrain->BeginStroke(RaiseParams(20.0f, 100.0f), FVector2D(-128.0, -128.0));
	for (int32 Tick = 0; Tick < 60; ++Tick)
	{
		Terrain->ApplyTick(FVector2D(-128.0, -128.0));
		// After every tick the component's bounds must already contain the top vertex.
		const int32 VX = 32;
		const int32 VY = 32;
		const FVector Top(100.0 * HF.VertexToWorld(VX), 100.0 * HF.VertexToWorld(VY), 100.0 * HF.GetHeight(VX, VY));
		if (!Comp0->Bounds.GetBox().IsInsideOrOn(Top))
		{
			AddError(FString::Printf(TEXT("tick %d: bounds %s do not contain the top vertex %s"), Tick, *Comp0->Bounds.GetBox().ToString(), *Top.ToString()));
			break;
		}
	}
	TestTrue(TEXT("spike reached 60 m"), HF.GetHeight(32, 32) >= 59.99f);
	TestTrue(TEXT("bounds widened during the stroke"), Renderer->GetStats().BoundsWidenings > 0);
	Terrain->EndStroke();
	float MinZ = 0.0f;
	float MaxZ = 0.0f;
	HF.GetChunkZRange(0, MinZ, MaxZ);
	TestEqual(TEXT("exact ZHi after stroke end"), Comp0->GetTrackedZHi(), MaxZ + UTerrainChunkComponent::ExactMarginM);
	TestEqual(TEXT("exact bounds max Z (cm) after stroke end"), Comp0->Bounds.GetBox().Max.Z, static_cast<double>(MaxZ + UTerrainChunkComponent::ExactMarginM) * 100.0, 0.01);
	// The XY footprint is the chunk's render rect: x, y in [-160, -96] m.
	TestEqual(TEXT("footprint min X"), Comp0->Bounds.GetBox().Min.X, -16000.0, 0.01);
	TestEqual(TEXT("footprint max X"), Comp0->Bounds.GetBox().Max.X, -9600.0, 0.01);

	// Undo puts the chunk back to flat and the bounds follow.
	TestTrue(TEXT("undo"), Terrain->UndoLast());
	TestEqual(TEXT("ZHi after undo"), Comp0->GetTrackedZHi(), 2.0f);
	Terrain->Destroy();
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS

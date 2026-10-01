// Project Chimera terrain trial (plan C). Original Chimera code.
// Automation tests for the collision path (plan C 4 C5, 3.6). Run: Tools/run_tests.ps1 -Filter Chimera.Terrain.Collision
//
// Chimera.Terrain.Collision.CustomOnly proves VENDOR.md patch 2: a mesh whose render sections carry no collision gets a trimesh body from
// SetCustomComplexMeshGeometry alone. On unpatched RMC b8669a0, FRealtimeMeshManaged::GenerateComplexCollision returns LOD0's false and the
// caller drops the custom geometry (RealtimeMeshManaged.cpp:462-471, :719-722), so the update still resolves Updated but the new body has no
// trimesh (RealtimeMesh.cpp:373-421): the "custom geometry" step below fails there.

#include "CoreMinimal.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Misc/AutomationTest.h"
#include "Async/TaskGraphInterfaces.h"
#include "CollisionQueryParams.h"
#include "Engine/Engine.h"
#include "Engine/HitResult.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "HAL/PlatformTime.h"
#include "PhysicsEngine/BodySetup.h"

#include "Data/TerrainHeightfield.h"
#include "Render/RmcTerrainRenderer.h"
#include "Render/TerrainChunkComponent.h"
#include "RealtimeMeshSimple.h"
#include "Core/RealtimeMeshBufferSetConfig.h"
#include "Core/RealtimeMeshCollision.h"

using namespace ChimeraTerrain;
using namespace RealtimeMesh;

#define TC_FLAGS (EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

namespace
{
	/** A Game world for the duration of a latent test (the pattern of TerrainRenderTests.cpp FTestWorld). */
	struct FCollisionTestWorld
	{
		UWorld* World = nullptr;

		FCollisionTestWorld()
		{
			const FName Name = MakeUniqueObjectName(nullptr, UWorld::StaticClass(), FName(TEXT("ChimeraTerrainCollisionTestWorld")), EUniqueObjectNameOptions::GloballyUnique);
			FWorldContext& Ctx = GEngine->CreateNewWorldContext(EWorldType::Game);
			World = UWorld::CreateWorld(EWorldType::Game, false, Name, GetTransientPackage());
			World->AddToRoot();
			Ctx.SetCurrentWorld(World);
			World->InitializeActorsForPlay(FURL());
			World->BeginPlay();
		}

		~FCollisionTestWorld()
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

		/** One world frame: actor ticks, physics, and FWorldDelegates::OnWorldPostActorTick, where RMC runs its end-of-frame updates
		 *  (RealtimeMeshSubsystem.cpp:163-217, :266); then the game-thread tasks the collision future posts. */
		void Step()
		{
			World->Tick(LEVELTICK_All, 1.0f / 60.0f);
			FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
		}
	};

	struct FCustomOnlyState
	{
		TUniquePtr<FCollisionTestWorld> TW;
		TWeakObjectPtr<UTerrainChunkComponent> Comp;
		TWeakObjectPtr<URealtimeMeshSimple> Mesh;
		FTerrainHeightfield HF;
		int32 ChunkId = 0;
		/** 0 = waiting for the config-only update, 1 = waiting for the custom-geometry update, 2 = trace, 3 = done. */
		int32 Stage = 0;
		TSharedPtr<TOptional<ERealtimeMeshCollisionUpdateResult>> Result;
		double StageStart = 0.0;
		int32 Frames = 0;
	};

	const TCHAR* ResultName(ERealtimeMeshCollisionUpdateResult R)
	{
		switch (R)
		{
		case ERealtimeMeshCollisionUpdateResult::Updated: return TEXT("Updated");
		case ERealtimeMeshCollisionUpdateResult::Ignored: return TEXT("Ignored");
		case ERealtimeMeshCollisionUpdateResult::Error: return TEXT("Error");
		default: return TEXT("Unknown");
		}
	}

	TSharedPtr<TOptional<ERealtimeMeshCollisionUpdateResult>> Track(TFuture<ERealtimeMeshCollisionUpdateResult>&& Future)
	{
		TSharedPtr<TOptional<ERealtimeMeshCollisionUpdateResult>> Box = MakeShared<TOptional<ERealtimeMeshCollisionUpdateResult>>();
		Future.Next([Box](ERealtimeMeshCollisionUpdateResult R) { *Box = R; });
		return Box;
	}

	int32 TriMeshes(const URealtimeMeshSimple* Mesh)
	{
		const UBodySetup* Body = Mesh ? Mesh->GetBodySetup() : nullptr;
		return Body ? Body->TriMeshGeometries.Num() : -1;
	}
}

// Collision-free render sections + SetCustomComplexMeshGeometry -> Updated with a trimesh body, and a physics trace hits it.
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FChimeraTerrainCollisionCustomOnlyTest, "Chimera.Terrain.Collision.CustomOnly", TC_FLAGS)
bool FChimeraTerrainCollisionCustomOnlyTest::RunTest(const FString& Parameters)
{
	TSharedRef<FCustomOnlyState> S = MakeShared<FCustomOnlyState>();
	S->TW = MakeUnique<FCollisionTestWorld>();
	UWorld* World = S->TW->World;
	if (!TestNotNull(TEXT("world"), World) || !TestNotNull(TEXT("physics scene"), World->GetPhysicsScene()))
	{
		return false;
	}
	AActor* Owner = World->SpawnActor<AActor>();
	if (!TestNotNull(TEXT("owner actor"), Owner))
	{
		return false;
	}
	USceneComponent* Root = NewObject<USceneComponent>(Owner, TEXT("Root"));
	Owner->SetRootComponent(Root);
	Root->RegisterComponent();

	// One terrain chunk with a raised corner, built by the renderer's own geometry functions.
	S->HF.Init(160, 64);
	S->ChunkId = S->HF.ChunkId(2, 2);
	const FTerrainRect R = S->HF.ChunkRenderRect(S->ChunkId);
	for (int32 Y = R.Y0; Y < R.Y1; ++Y)
	{
		for (int32 X = R.X0; X < R.X1; ++X)
		{
			S->HF.SetHeight(X, Y, 0.05f * static_cast<float>(X - R.X0) + 0.02f * static_cast<float>(Y - R.Y0));
		}
	}
	UTerrainChunkComponent* Comp = NewObject<UTerrainChunkComponent>(Owner, TEXT("CollisionTestChunk"));
	Comp->SetupAttachment(Root);
	Comp->RegisterComponent();
	URealtimeMeshSimple* Mesh = Comp->InitializeRealtimeMesh<URealtimeMeshSimple>();
	if (!TestNotNull(TEXT("mesh"), Mesh))
	{
		return false;
	}
	S->Comp = Comp;
	S->Mesh = Mesh;
	FRealtimeMeshStreamSet Streams;
	RmcTerrainGeometry::BuildChunk(S->HF, S->ChunkId, Streams);
	// CreateBufferSet auto-creates the sections, without collision (RealtimeMeshManaged.cpp:24-27).
	Mesh->CreateBufferSet(RmcTerrainGeometry::ChunkBufferSetKey(), MoveTemp(Streams), FRealtimeMeshBufferSetConfig(ERealtimeMeshSectionDrawType::Dynamic));
	FRealtimeMeshCollisionConfiguration Cfg;
	Cfg.bUseComplexAsSimpleCollision = true;
	Cfg.bUseAsyncCook = true;
	Cfg.bShouldFastCookMeshes = true;
	// Stage 0: the config alone. The sections carry no collision, so the body this update builds has no trimesh.
	S->Result = Track(Mesh->SetCollisionConfig(Cfg));
	S->StageStart = FPlatformTime::Seconds();

	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([this, S]() -> bool
	{
		constexpr double StageTimeoutS = 20.0;
		URealtimeMeshSimple* M = S->Mesh.Get();
		UTerrainChunkComponent* C = S->Comp.Get();
		if (!M || !C)
		{
			AddError(TEXT("mesh or component destroyed"));
			S->TW.Reset();
			return true;
		}
		S->TW->Step();
		++S->Frames;
		if (S->Stage < 2 && !S->Result->IsSet())
		{
			if (FPlatformTime::Seconds() - S->StageStart > StageTimeoutS)
			{
				AddError(FString::Printf(TEXT("stage %d: collision future did not resolve in %.0f s (%d frames)"), S->Stage, StageTimeoutS, S->Frames));
				S->TW.Reset();
				return true;
			}
			return false;
		}
		if (S->Stage == 0)
		{
			const ERealtimeMeshCollisionUpdateResult Res = S->Result->GetValue();
			AddInfo(FString::Printf(TEXT("config-only update: %s, trimeshes=%d after %d frames"), ResultName(Res), TriMeshes(M), S->Frames));
			TestEqual(TEXT("collision-free sections give a body without a trimesh"), TriMeshes(M) > 0 ? 1 : 0, 0);
			// Stage 1: custom complex geometry only (RealtimeMeshManaged.h:382).
			FRealtimeMeshCollisionMesh CollisionMesh;
			RmcTerrainGeometry::BuildCollisionMesh(S->HF, S->ChunkId, CollisionMesh);
			const FTerrainRect Rr = S->HF.ChunkRenderRect(S->ChunkId);
			TestEqual(TEXT("collision vertices = render vertices"), CollisionMesh.GetVertices().Num(), Rr.Width() * Rr.Height());
			TestEqual(TEXT("collision triangles = render triangles"), CollisionMesh.GetTriangles().Num(), (Rr.Width() - 1) * (Rr.Height() - 1) * 2);
			FRealtimeMeshComplexGeometry Geometry;
			Geometry.Add(MoveTemp(CollisionMesh));
			S->Result = Track(M->SetCustomComplexMeshGeometry(MoveTemp(Geometry)));
			S->Stage = 1;
			S->StageStart = FPlatformTime::Seconds();
			S->Frames = 0;
			return false;
		}
		if (S->Stage == 1)
		{
			const ERealtimeMeshCollisionUpdateResult Res = S->Result->GetValue();
			const int32 Tri = TriMeshes(M);
			AddInfo(FString::Printf(TEXT("custom-geometry update: %s, trimeshes=%d after %d frames"), ResultName(Res), Tri, S->Frames));
			TestTrue(TEXT("custom geometry update resolves Updated"), Res == ERealtimeMeshCollisionUpdateResult::Updated);
			TestTrue(TEXT("custom geometry alone gives the body a trimesh (fails on unpatched RMC b8669a0)"), Tri > 0);
			TestTrue(TEXT("component body instance uses the new body"), C->BodyInstance.IsValidBodyInstance() && C->BodyInstance.GetBodySetup() == M->GetBodySetup());
			S->Stage = 2;
			S->Frames = 0;
			return false;
		}
		if (S->Stage == 2)
		{
			// A vertical trace at a vertex inside the chunk hits at the vertex height (the scene query structure updates with the world tick).
			const FTerrainRect Rr = S->HF.ChunkRenderRect(S->ChunkId);
			const int32 VX = Rr.X0 + 10;
			const int32 VY = Rr.Y0 + 20;
			const FVector XY(100.0 * (S->HF.VertexToWorld(VX) + 0.25), 100.0 * (S->HF.VertexToWorld(VY) + 0.25), 0.0);
			FHitResult Hit;
			FCollisionQueryParams Params(SCENE_QUERY_STAT(ChimeraTerrainCollisionTest), true);
			const bool bHit = S->TW->World->LineTraceSingleByChannel(Hit, XY + FVector(0, 0, 20000.0), XY - FVector(0, 0, 20000.0), ECC_Visibility, Params);
			if (!bHit && S->Frames < 10)
			{
				return false;
			}
			// Expected height of the plane z = 0.05 lx + 0.02 ly at (lx, ly) = (10.25, 20.25): exact on either triangle of a plane.
			const double Expected = 100.0 * (0.05 * 10.25 + 0.02 * 20.25);
			TestTrue(TEXT("vertical Visibility trace hits the chunk"), bHit && Hit.GetComponent() == C);
			if (bHit)
			{
				AddInfo(FString::Printf(TEXT("trace hit z=%.4f cm, expected %.4f cm"), Hit.ImpactPoint.Z, Expected));
				TestEqual(TEXT("hit height (cm)"), Hit.ImpactPoint.Z, Expected, 0.5);
			}
			S->Stage = 3;
			S->TW.Reset();
			return true;
		}
		S->TW.Reset();
		return true;
	}));
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS

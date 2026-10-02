// Project Chimera terrain trial (plan C scatter 4 S4). Original Chimera code.
// Chimera.Terrain.Scatter.Render: the runtime scatter actor in a Game world under -nullrhi (FApp::CanEverRender() is false there, so no
// component is created, the splat texture's precedent), driven frame by frame as a latent test: the enable fill, a raise stroke, an undo.
// After each settles: live == reference, no tile pending, counters balanced, and no component exists. Last, teardown with jobs in flight: a
// stroke, one frame (its tiles dispatched), then Shutdown (the EndPlay path) must wait on every job, leave nothing in flight and keep the
// counters balanced (FAILX's forced exit never reaches EndPlay). The engine-side readback is SX1's
// scatter_verify in a -game run (SXSMOKE). Kept out of TerrainScatterTests.cpp, which also compiles in the standalone cl.exe harness.
// Run: Tools/run_tests.ps1 -Filter Chimera.Terrain.Scatter

#include "CoreMinimal.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Misc/AutomationTest.h"
#include "Misc/App.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "HAL/PlatformTime.h"
#include "Dom/JsonObject.h"
#include "Engine/Texture2D.h"
#include "UObject/UObjectGlobals.h"
#include "UObject/Package.h"

#include "Data/TerrainBrush.h"
#include "Data/TerrainScatter.h"
#include "Data/TerrainScatterPalette.h"
#include "Game/TerrainActor.h"
#include "Game/TerrainScatterActor.h"

using namespace ChimeraTerrain;

namespace
{
	/** A Game world for the duration of a latent test (the pattern of TerrainRenderTests.cpp FTestWorld). */
	struct FScatterTestWorld
	{
		UWorld* World = nullptr;

		FScatterTestWorld()
		{
			const FName Name = MakeUniqueObjectName(nullptr, UWorld::StaticClass(), FName(TEXT("ChimeraScatterTestWorld")), EUniqueObjectNameOptions::GloballyUnique);
			FWorldContext& Ctx = GEngine->CreateNewWorldContext(EWorldType::Game);
			World = UWorld::CreateWorld(EWorldType::Game, false, Name, GetTransientPackage());
			World->AddToRoot();
			Ctx.SetCurrentWorld(World);
			World->InitializeActorsForPlay(FURL());
		}

		~FScatterTestWorld()
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

	struct FScatterRenderState
	{
		TUniquePtr<FScatterTestWorld> TW;
		TWeakObjectPtr<ATerrainActor> Terrain;
		TWeakObjectPtr<ATerrainScatter> Scatter;
		/** 0 = enable fill, 1 = after the stroke, 2 = after the undo, 3 = teardown with jobs in flight, 4 = done. */
		int32 Stage = 0;
		int32 Frames = 0;
		double StageStart = 0.0;
		uint64 FirstLive = 0;
		uint64 StrokeLive = 0;
	};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FChimeraTerrainScatterRenderTest, "Chimera.Terrain.Scatter.Render", EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)
bool FChimeraTerrainScatterRenderTest::RunTest(const FString& Parameters)
{
	// FTerrainSplatTexture names its transient texture uniquely (MakeUniqueObjectName), so a terrain left by an earlier test in this process
	// (Chimera.Terrain.Render.RmcSeamsAndBounds) no longer collides with this one (it asserted in UObjectArray.cpp:405 under a fixed name).
	TSharedRef<FScatterRenderState> S = MakeShared<FScatterRenderState>();
	S->TW = MakeUnique<FScatterTestWorld>();
	UWorld* World = S->TW->World;
	if (!TestNotNull(TEXT("world"), World))
	{
		return false;
	}
	ATerrainActor* Terrain = World->SpawnActor<ATerrainActor>();
	if (!TestNotNull(TEXT("terrain actor"), Terrain) || !TestTrue(TEXT("InitTerrain"), Terrain->InitTerrain(64, 32, ETerrainDrawType::Dynamic)))
	{
		return false;
	}
	ATerrainScatter* Scatter = World->SpawnActor<ATerrainScatter>();
	if (!TestNotNull(TEXT("scatter actor"), Scatter))
	{
		return false;
	}
	const FScatterOptions Opt = FScatterOptions::FromCommandLine(TEXT("-ChimeraTerrainScatter=1"));
	Scatter->Setup(Terrain, Opt, true);
	TestTrue(TEXT("scatter requested and enabled"), Scatter->WasRequested() && Scatter->IsEnabled());
	TestTrue(TEXT("the enable dirtied the map (pending work)"), Scatter->HasPendingWork() && Terrain->HasPendingWork());
	S->Terrain = Terrain;
	S->Scatter = Scatter;
	S->StageStart = FPlatformTime::Seconds();

	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([this, S]() -> bool
	{
		ATerrainScatter* Sc = S->Scatter.Get();
		ATerrainActor* T = S->Terrain.Get();
		if (!Sc || !T)
		{
			AddError(TEXT("actors destroyed"));
			S->TW.Reset();
			return true;
		}
		if (S->Stage == 3)
		{
			// The stroke was issued last frame; this frame dispatches its tiles, then teardown runs with the jobs still in flight.
			Sc->StepFrame();
			const int32 InFlight = Sc->NumInFlight();
			const int32 Waited = Sc->Shutdown();
			TestTrue(TEXT("teardown: jobs were in flight when Shutdown ran"), InFlight > 0 && Waited == InFlight);
			TestEqual(TEXT("teardown: nothing in flight after Shutdown"), Sc->NumInFlight(), 0);
			const TSharedPtr<FJsonObject> C = Sc->ResultsJson()->GetObjectField(TEXT("counters"));
			const double Dispatched = C->GetNumberField(TEXT("dispatched"));
			const double Ends = C->GetNumberField(TEXT("applied")) + C->GetNumberField(TEXT("skipped_identical")) + C->GetNumberField(TEXT("discarded_epoch")) + C->GetNumberField(TEXT("cancelled"));
			TestEqual(TEXT("teardown: dispatched == applied + skipped + discarded + cancelled"), Dispatched, Ends);
			TestTrue(TEXT("teardown: the in-flight jobs were cancelled"), C->GetNumberField(TEXT("cancelled")) >= InFlight);
			AddInfo(FString::Printf(TEXT("teardown: %d jobs in flight, waited %d, cancelled %.0f, dispatched %.0f"), InFlight, Waited, C->GetNumberField(TEXT("cancelled")), Dispatched));
			S->Stage = 4;
			S->TW.Reset();
			return true;
		}
		Sc->StepFrame();
		++S->Frames;
		if (Sc->HasPendingWork())
		{
			if (FPlatformTime::Seconds() - S->StageStart > 60.0)
			{
				AddError(FString::Printf(TEXT("stage %d: scatter still pending after 60 s (%d frames)"), S->Stage, S->Frames));
				// Worker jobs capture the actor's scheduler: wait on them before the world (and the actor) goes away.
				Sc->Shutdown();
				S->TW.Reset();
				return true;
			}
			return false;
		}
		// Settled: the checks every stage shares.
		bool bPass = false;
		const TSharedRef<FJsonObject> V = Sc->Verify(FString::Printf(TEXT("stage%d"), S->Stage), bPass);
		TestTrue(*FString::Printf(TEXT("stage %d: verify (live == reference, nothing pending)"), S->Stage), bPass);
		const TSharedRef<FJsonObject> R = Sc->ResultsJson();
		if (!FApp::CanEverRender())
		{
			TestEqual(*FString::Printf(TEXT("stage %d: no component under nullrhi"), S->Stage), static_cast<int32>(R->GetNumberField(TEXT("components"))), 0);
		}
		const TSharedPtr<FJsonObject> C = R->GetObjectField(TEXT("counters"));
		const double Dispatched = C->GetNumberField(TEXT("dispatched"));
		const double Ends = C->GetNumberField(TEXT("applied")) + C->GetNumberField(TEXT("skipped_identical")) + C->GetNumberField(TEXT("discarded_epoch")) + C->GetNumberField(TEXT("cancelled"));
		TestEqual(*FString::Printf(TEXT("stage %d: dispatched == applied + skipped + discarded + cancelled"), S->Stage), Dispatched, Ends);
		const uint64 Live = Sc->ScatterFnvOf(Sc->LiveHash());
		const int64 Count = Sc->LiveHash().TotalCount();
		AddInfo(FString::Printf(TEXT("stage %d settled after %d frames: %lld records, live fnv 0x%016llx, dispatched %.0f"), S->Stage, S->Frames, Count,
			static_cast<unsigned long long>(Live), Dispatched));
		if (S->Stage == 0)
		{
			TestTrue(TEXT("the fill made records"), Count > 1000);
			S->FirstLive = Live;
			// A raise stroke through the actor (the event path, S4a).
			FTerrainBrushParams P;
			P.Mode = ETerrainBrushMode::Raise;
			P.DiameterM = 24.0f;
			P.Strength = 60.0f;
			T->BeginStroke(P, FVector2D(-10.0, 12.0));
			for (int32 K = 0; K < 12; ++K)
			{
				T->ApplyTick(FVector2D(-10.0 + 1.5 * K, 12.0 - 0.5 * K));
			}
			T->EndStroke();
			TestTrue(TEXT("the stroke dirtied tiles"), Sc->HasPendingWork());
			S->Stage = 1;
		}
		else if (S->Stage == 1)
		{
			TestNotEqual(TEXT("the stroke changed the scatter"), Live, S->FirstLive);
			S->StrokeLive = Live;
			TestTrue(TEXT("undo"), T->UndoLast());
			TestTrue(TEXT("the undo dirtied tiles"), Sc->HasPendingWork());
			S->Stage = 2;
		}
		else
		{
			TestEqual(TEXT("undo returns the scatter to the first state"), Live, S->FirstLive);
			// Teardown coverage: a stroke whose tiles are dispatched next frame (stage 3), then Shutdown with them in flight.
			FTerrainBrushParams P;
			P.Mode = ETerrainBrushMode::Raise;
			P.DiameterM = 24.0f;
			P.Strength = 60.0f;
			T->BeginStroke(P, FVector2D(8.0, -6.0));
			for (int32 K = 0; K < 6; ++K)
			{
				T->ApplyTick(FVector2D(8.0 + 1.5 * K, -6.0));
			}
			T->EndStroke();
			S->Stage = 3;
			return false;
		}
		S->Frames = 0;
		S->StageStart = FPlatformTime::Seconds();
		return false;
	}));
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS

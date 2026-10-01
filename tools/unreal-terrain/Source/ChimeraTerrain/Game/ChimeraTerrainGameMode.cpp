// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Game/ChimeraTerrainGameMode.h"

#include "ChimeraTerrain.h"
#include "Engine/World.h"
#include "HAL/IConsoleManager.h"
#include "Game/RtsCameraPawn.h"
#include "Game/TerrainActor.h"
#include "Game/TerrainHud.h"
#include "Game/TerrainLighting.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Test/TerrainScriptDirector.h"

FChimeraTerrainOptions FChimeraTerrainOptions::FromCommandLine(const TCHAR* Cmd)
{
	FChimeraTerrainOptions O;
	FParse::Value(Cmd, TEXT("ChimeraTerrainHalf="), O.HalfExtentM);
	FParse::Value(Cmd, TEXT("ChimeraTerrainChunk="), O.ChunkQuads);
	O.HalfExtentM = FMath::Clamp(O.HalfExtentM, 16, 512);
	O.ChunkQuads = FMath::Clamp(O.ChunkQuads, 8, 256);
	FString Draw;
	if (FParse::Value(Cmd, TEXT("ChimeraTerrainDrawType="), Draw, false))
	{
		O.DrawType = Draw.Equals(TEXT("Static"), ESearchCase::IgnoreCase) ? ChimeraTerrain::ETerrainDrawType::Static : ChimeraTerrain::ETerrainDrawType::Dynamic;
	}
	FParse::Value(Cmd, TEXT("ChimeraTerrainScript="), O.ScriptPath, false);
	FParse::Value(Cmd, TEXT("ChimeraTerrainOut="), O.OutDir, false);
	O.bCompareAtStart = FParse::Param(Cmd, TEXT("ChimeraTerrainCompare"));
	FParse::Value(Cmd, TEXT("ChimeraTerrainExposure="), O.CompareEV100);
	FParse::Value(Cmd, TEXT("ChimeraTerrainSettleTimeoutS="), O.SettleTimeoutS);
	O.SettleTimeoutS = FMath::Max(600.0, O.SettleTimeoutS);
	FParse::Value(Cmd, TEXT("ChimeraTerrainHitchMs="), O.HitchMs);
	O.HitchMs = FMath::Clamp(O.HitchMs, 0, 10000);
	FParse::Value(Cmd, TEXT("ChimeraTerrainLoad="), O.LoadDir, false);
	int32 FastCook = 1;
	if (FParse::Value(Cmd, TEXT("ChimeraTerrainFastCook="), FastCook))
	{
		O.bFastCook = FastCook != 0;
	}
	if (FParse::Value(Cmd, TEXT("ChimeraTerrainCollisionDuringStroke="), O.CollisionDuringStrokeMs))
	{
		O.CollisionDuringStrokeMs = FMath::Clamp(O.CollisionDuringStrokeMs, 0, 60000);
	}
	return O;
}

AChimeraTerrainGameMode::AChimeraTerrainGameMode()
{
	DefaultPawnClass = ARtsCameraPawn::StaticClass();
	HUDClass = ATerrainHud::StaticClass();
}

void AChimeraTerrainGameMode::RestartPlayer(AController* NewPlayer)
{
	if (NewPlayer == nullptr || NewPlayer->IsPendingKillPending())
	{
		return;
	}
	FTerrainCameraPose Pose;
	FTerrainCameraPose::Find(TEXT("rts80"), Pose);
	RestartPlayerAtTransform(NewPlayer, FTransform(Pose.Rotation, Pose.LocationCm));
	if (ARtsCameraPawn* Pawn = Cast<ARtsCameraPawn>(NewPlayer->GetPawn()))
	{
		Pawn->ApplyPose(Pose);
	}
}

void AChimeraTerrainGameMode::StartPlay()
{
	Options = FChimeraTerrainOptions::FromCommandLine(FCommandLine::Get());
	UE_LOG(LogChimeraTerrain, Display, TEXT("ChimeraTerrain start: half=%d chunk=%d draw=%s script='%s' out='%s' compare=%d ev100=%.2f fast_cook=%d collision_during_stroke_ms=%d"),
		Options.HalfExtentM, Options.ChunkQuads, Options.DrawType == ChimeraTerrain::ETerrainDrawType::Dynamic ? TEXT("Dynamic") : TEXT("Static"),
		*Options.ScriptPath, *Options.OutDir, Options.bCompareAtStart ? 1 : 0, Options.CompareEV100, Options.bFastCook ? 1 : 0, Options.CollisionDuringStrokeMs);

	FActorSpawnParameters SP;
	SP.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	Lighting = GetWorld()->SpawnActor<ATerrainLighting>(ATerrainLighting::StaticClass(), FTransform::Identity, SP);
	Terrain = GetWorld()->SpawnActor<ATerrainActor>(ATerrainActor::StaticClass(), FTransform::Identity, SP);
	if (Options.CollisionDuringStrokeMs >= 0)
	{
		if (IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(TEXT("chimera.terrain.CollisionDuringStroke")))
		{
			Cv->Set(Options.CollisionDuringStrokeMs, ECVF_SetByCommandline);
		}
	}
	if (Terrain)
	{
		ChimeraTerrain::FTerrainCollisionOptions Col;
		Col.bFastCook = Options.bFastCook;
		Terrain->SetCollisionOptions(Col);
	}
	const bool bTerrainOk = Terrain && Terrain->InitTerrain(Options.HalfExtentM, Options.ChunkQuads, Options.DrawType);
	if (!bTerrainOk)
	{
		UE_LOG(LogChimeraTerrain, Error, TEXT("terrain initialisation failed"));
	}
	if (Lighting)
	{
		Lighting->SetCompareMode(Options.bCompareAtStart, Options.CompareEV100);
	}

	Super::StartPlay();

	if (!Options.ScriptPath.IsEmpty())
	{
		Director = GetWorld()->SpawnActor<ATerrainScriptDirector>(ATerrainScriptDirector::StaticClass(), FTransform::Identity, SP);
		Director->Start(Options, Terrain, Lighting, bTerrainOk);
	}
}

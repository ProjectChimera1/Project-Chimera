// Copyright Epic Games, Inc. All Rights Reserved.

#include "Game/ChimeraHudShot.h"
#include "ChimeraHud.h"
#include "AssetCompilingManager.h"
#include "Containers/Ticker.h"
#include "GenericPlatform/GenericPlatformMisc.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformTime.h"
#include "Misc/CommandLine.h"
#include "Misc/DateTime.h"
#include "Misc/OutputDeviceRedirector.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "ShaderCompiler.h"
#include "UnrealClient.h"

namespace
{
	enum class EPhase { Compile, Frames, Waiting, Done };

	struct FShotState
	{
		FString Path;
		int32 Frames = 90;
		double CompileWait = 1800.0;
		double ShotTimeout = 120.0;
		bool bWarmup = false;
		EPhase Phase = EPhase::Compile;
		double StartTime = 0.0;
		double IdleTime = 0.0;
		double RequestTime = 0.0;
		FDateTime RequestStamp;
		uint64 FrameAtIdle = 0;
	};

	TSharedPtr<FShotState> GState;

	/** Failures exit forced: only the forced path returns the code (non-forced always returns 0). */
	void Fail(const TCHAR* What, uint8 Code)
	{
		UE_LOG(LogChimeraHud, Error, TEXT("%s"), What);
		GLog->Flush();
		FPlatformMisc::RequestExitWithStatus(true, Code);
	}

	bool CompileIdle()
	{
		const bool bShaders = (GShaderCompilingManager == nullptr) || (GShaderCompilingManager->GetNumRemainingJobs() == 0);
		return bShaders && FAssetCompilingManager::Get().GetNumRemainingAssets() == 0;
	}

	void OnProcessed()
	{
		if (!GState.IsValid() || GState->Phase != EPhase::Waiting)
		{
			return;
		}
		GState->Phase = EPhase::Done;
		// The delegate also fires when nothing was written (GameViewportClient.cpp:2539): check the file itself.
		IFileManager& FM = IFileManager::Get();
		const int64 Size = FM.FileSize(*GState->Path);
		const FDateTime Stamp = FM.GetTimeStamp(*GState->Path);
		if (Size <= 0 || Stamp < GState->RequestStamp)
		{
			Fail(*FString::Printf(TEXT("SHOT MISSING %s size=%lld"), *GState->Path, Size), 4);
			return;
		}
		UE_LOG(LogChimeraHud, Display, TEXT("shot written %lld"), Size);
		GLog->Flush();
		FPlatformMisc::RequestExitWithStatus(false, 0);
	}

	bool Tick(float)
	{
		FShotState& S = *GState;
		const double Now = FPlatformTime::Seconds();
		switch (S.Phase)
		{
		case EPhase::Compile:
			if (CompileIdle())
			{
				S.IdleTime = Now;
				S.FrameAtIdle = GFrameCounter;
				S.Phase = EPhase::Frames;
				UE_LOG(LogChimeraHud, Display, TEXT("compile idle after %.1f s"), Now - S.StartTime);
			}
			else if (Now - S.StartTime > S.CompileWait)
			{
				S.Phase = EPhase::Done;
				Fail(TEXT("SHOT COMPILE TIMEOUT"), 6);
			}
			break;
		case EPhase::Frames:
			if ((int64)(GFrameCounter - S.FrameAtIdle) >= S.Frames)
			{
				if (S.bWarmup)
				{
					S.Phase = EPhase::Done;
					UE_LOG(LogChimeraHud, Display, TEXT("WARMUP DONE compile_s=%.0f"), S.IdleTime - S.StartTime);
					GLog->Flush();
					FPlatformMisc::RequestExitWithStatus(false, 0);
				}
				else
				{
					S.RequestStamp = FDateTime::Now();
					S.RequestTime = Now;
					S.Phase = EPhase::Waiting;
					// Remove any stale file so "exists" cannot be satisfied by an old run, then request UI-inclusive capture.
					IFileManager::Get().Delete(*S.Path, false, true, true);
					FScreenshotRequest::OnScreenshotRequestProcessed().AddStatic(&OnProcessed);
					FScreenshotRequest::RequestScreenshot(S.Path, true, false, false, FIntRect(), true);
					UE_LOG(LogChimeraHud, Display, TEXT("screenshot requested %s after %d frames"), *S.Path, S.Frames);
				}
			}
			else if (!S.bWarmup && Now - S.IdleTime > S.ShotTimeout)
			{
				S.Phase = EPhase::Done;
				Fail(TEXT("SHOT TIMEOUT"), 3);
			}
			break;
		case EPhase::Waiting:
			if (Now - S.RequestTime > 30.0)
			{
				S.Phase = EPhase::Done;
				Fail(TEXT("SHOT NO CALLBACK"), 5);
			}
			break;
		case EPhase::Done:
			return false;
		}
		return true;
	}
}

void FChimeraHudShot::StartIfRequested()
{
	const TCHAR* Cmd = FCommandLine::Get();
	FString Path;
	const bool bShot = FParse::Value(Cmd, TEXT("-HudShot="), Path, false);
	const bool bWarmup = FParse::Param(Cmd, TEXT("HudWarmup"));
	if (!bShot && !bWarmup)
	{
		return;
	}
	if (GState.IsValid())
	{
		return; // already running
	}
	GState = MakeShared<FShotState>();
	GState->Path = Path;
	GState->bWarmup = bWarmup;
	FParse::Value(Cmd, TEXT("-HudShotFrames="), GState->Frames);
	FParse::Value(Cmd, TEXT("-HudCompileWait="), GState->CompileWait);
	FParse::Value(Cmd, TEXT("-HudShotTimeout="), GState->ShotTimeout);
	GState->StartTime = FPlatformTime::Seconds();
	UE_LOG(LogChimeraHud, Display, TEXT("shot automation: path=%s frames=%d compileWait=%.0f shotTimeout=%.0f warmup=%d"),
		*Path, GState->Frames, GState->CompileWait, GState->ShotTimeout, bWarmup ? 1 : 0);
	FTSTicker::GetCoreTicker().AddTicker(TEXT("ChimeraHudShot"), 0.0f, &Tick);
}

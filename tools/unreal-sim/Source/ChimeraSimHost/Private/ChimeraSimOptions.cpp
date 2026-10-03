// Copyright Chimera. See ChimeraSimOptions.h.
#include "ChimeraSimOptions.h"

#include "ChimeraSimLog.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
	/** Default content: the Godot project folder that holds resources/data (what the sim calls res://). */
	const TCHAR* DefaultRepoGodot = TEXT("D:/Projects/Project_Chimera/godot");

	bool ReadString(const TCHAR* Cmd, const TCHAR* Key, FString& Out)
	{
		FString V;
		if (FParse::Value(Cmd, Key, V, /*bShouldStopOnSeparator*/ false))
		{
			Out = V.TrimStartAndEnd();
			return true;
		}
		return false;
	}

	/** Integer option, whole string must be digits (optionally signed). */
	bool ReadInt(const TCHAR* Cmd, const TCHAR* Key, int32& Out, FString& OutError)
	{
		FString V;
		if (!ReadString(Cmd, Key, V))
		{
			return true;
		}
		bool bDigits = !V.IsEmpty();
		for (int32 i = 0; i < V.Len(); ++i)
		{
			if (!(FChar::IsDigit(V[i]) || (i == 0 && V[i] == TEXT('-') && V.Len() > 1)))
			{
				bDigits = false;
			}
		}
		if (!bDigits)
		{
			OutError = FString::Printf(TEXT("%s'%s' is not an integer"), Key, *V);
			return false;
		}
		Out = FCString::Atoi(*V);
		return true;
	}
}

bool FChimeraSimOptions::Parse(FString& OutError)
{
	const TCHAR* Cmd = FCommandLine::Get();

	ContentRoot = DefaultRepoGodot;
	ReadString(Cmd, TEXT("-ChimeraSimContent="), ContentRoot);
	Scenario = ContentRoot / TEXT("ProjectChimera.Sim.Tests/Trial/trial_1000.json");
	Orders = ContentRoot / TEXT("ProjectChimera.Sim.Tests/Trial/trial_1000.orders.csv");
	ReadString(Cmd, TEXT("-ChimeraSimScenario="), Scenario);
	ReadString(Cmd, TEXT("-ChimeraSimOrders="), Orders);

	SeedText = TEXT("0xC0FFEE1234567890");
	ReadString(Cmd, TEXT("-ChimeraSimSeed="), SeedText);
	{
		// Base 0: "0x..." is hex, anything else decimal (F26). The whole string must be consumed.
		TCHAR* End = nullptr;
		const uint64 V = FCString::Strtoui64(*SeedText, &End, 0);
		if (SeedText.IsEmpty() || End == nullptr || *End != 0 || SeedText.StartsWith(TEXT("-")))
		{
			OutError = FString::Printf(TEXT("-ChimeraSimSeed='%s' is not a 64-bit decimal or 0x-hex number"), *SeedText);
			return false;
		}
		Seed = V;
	}

	if (!ReadInt(Cmd, TEXT("-ChimeraSimTicks="), Ticks, OutError)) { return false; }
	if (Ticks < 1 || Ticks > 1000000)
	{
		OutError = FString::Printf(TEXT("-ChimeraSimTicks=%d out of range"), Ticks);
		return false;
	}
	bAi = FParse::Param(Cmd, TEXT("ChimeraSimAi"));

	OutDir = FPaths::ConvertRelativePathToFull(FPaths::ProjectSavedDir() / TEXT("ChimeraSim"));
	ReadString(Cmd, TEXT("-ChimeraSimOut="), OutDir);

	const FString DefaultShots = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() / TEXT("SimTrial/shots.json"));
	ShotsPath = FPaths::FileExists(DefaultShots) ? DefaultShots : FString();
	ReadString(Cmd, TEXT("-ChimeraSimShots="), ShotsPath);
	if (ShotsPath.Equals(TEXT("none"), ESearchCase::IgnoreCase))
	{
		ShotsPath.Reset(); // an explicit "no shots" run (A11's rendered ai run)
	}
	MeshesPath = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() / TEXT("SimTrial/unit_meshes.json"));
	ReadString(Cmd, TEXT("-ChimeraSimMeshes="), MeshesPath);
	bDeadUnderGround = FParse::Param(Cmd, TEXT("ChimeraSimDeadUnderGround"));
	bShotFreeze = !FParse::Param(Cmd, TEXT("ChimeraSimNoShotFreeze"));
	if (!ReadInt(Cmd, TEXT("-ChimeraSimShotSamples="), ShotSamples, OutError)) { return false; }
	if (ShotSamples < 1)
	{
		OutError = TEXT("-ChimeraSimShotSamples must be >= 1");
		return false;
	}
	if (!ReadInt(Cmd, TEXT("-ChimeraSimVerifyEvery="), VerifyEvery, OutError)) { return false; }
	if (VerifyEvery < 0)
	{
		OutError = TEXT("-ChimeraSimVerifyEvery must be >= 0");
		return false;
	}

	bHideUnits = FParse::Param(Cmd, TEXT("ChimeraSimHideUnits"));
	if (!ReadInt(Cmd, TEXT("-ChimeraSimFilmEvery="), FilmEvery, OutError)) { return false; }
	if (!ReadInt(Cmd, TEXT("-ChimeraSimHitchMs="), HitchMs, OutError)) { return false; }
	if (!ReadInt(Cmd, TEXT("-ChimeraSimHitchEvery="), HitchEvery, OutError)) { return false; }
	if (HitchMs < 0 || HitchEvery < 0 || FilmEvery < 0)
	{
		OutError = TEXT("-ChimeraSimHitchMs/-ChimeraSimHitchEvery/-ChimeraSimFilmEvery must be >= 0");
		return false;
	}
	bNoArena = FParse::Param(Cmd, TEXT("ChimeraSimNoArena"));
	bExitWhenDone = FParse::Param(Cmd, TEXT("ChimeraSimExitWhenDone"));
	{
		int32 Warm = (int32)WarmupMaxSec;
		if (!ReadInt(Cmd, TEXT("-ChimeraSimWarmupMaxSec="), Warm, OutError)) { return false; }
		WarmupMaxSec = FMath::Max(1, Warm);
	}
	ReadString(Cmd, TEXT("-ChimeraSimLeg="), Leg);
	ReadString(Cmd, TEXT("-ChimeraSimPCommit="), PCommit);
	ReadString(Cmd, TEXT("-ChimeraSimPDirty="), PDirty);

	// Shots: a JSON list of {tick, camera}; no comma lists on the command line (plan A 3.7).
	Shots.Reset();
	if (!ShotsPath.IsEmpty())
	{
		FString Text;
		if (!FFileHelper::LoadFileToString(Text, *ShotsPath))
		{
			OutError = FString::Printf(TEXT("cannot read shots file %s"), *ShotsPath);
			return false;
		}
		TArray<TSharedPtr<FJsonValue>> Items;
		TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
		if (!FJsonSerializer::Deserialize(Reader, Items))
		{
			OutError = FString::Printf(TEXT("shots file %s is not a JSON list"), *ShotsPath);
			return false;
		}
		for (const TSharedPtr<FJsonValue>& Item : Items)
		{
			const TSharedPtr<FJsonObject>* Obj = nullptr;
			double T = 0.0;
			FString Cam;
			if (!Item.IsValid() || !Item->TryGetObject(Obj) || Obj == nullptr || !(*Obj)->TryGetNumberField(TEXT("tick"), T)
				|| !(*Obj)->TryGetStringField(TEXT("camera"), Cam) || T < 0.0 || T > (double)Ticks || T != FMath::FloorToDouble(T))
			{
				OutError = FString::Printf(TEXT("shots file %s: every entry must be {\"tick\": 0..%d, \"camera\": \"name\"}"), *ShotsPath, Ticks);
				return false;
			}
			FChimeraSimShot S;
			S.Tick = (int32)T;
			S.Camera = Cam;
			Shots.Add(S);
		}
	}
	return true;
}

FString FChimeraSimOptions::SeedHex() const
{
	return FString::Printf(TEXT("0x%016llX"), (unsigned long long)Seed);
}

FString FChimeraSimOptions::ShotList() const
{
	TArray<FString> Parts;
	for (const FChimeraSimShot& S : Shots)
	{
		Parts.Add(FString::Printf(TEXT("%d:%s"), S.Tick, *S.Camera));
	}
	return Parts.Num() > 0 ? FString::Join(Parts, TEXT(",")) : FString(TEXT("none"));
}

void FChimeraSimOptions::Echo() const
{
	UE_LOG(LogChimeraSim, Display, TEXT("option content=%s"), *ContentRoot);
	UE_LOG(LogChimeraSim, Display, TEXT("option scenario=%s"), *Scenario);
	UE_LOG(LogChimeraSim, Display, TEXT("option orders=%s"), *Orders);
	UE_LOG(LogChimeraSim, Display, TEXT("option seed_text=%s"), *SeedText);
	UE_LOG(LogChimeraSim, Display, TEXT("option ticks=%d ai=%d out=%s"), Ticks, bAi ? 1 : 0, *OutDir);
	UE_LOG(LogChimeraSim, Display, TEXT("option shots_file=%s"), ShotsPath.IsEmpty() ? TEXT("none") : *ShotsPath);
	UE_LOG(LogChimeraSim, Display, TEXT("option hide_units=%d film_every=%d hitch_ms=%d hitch_every=%d no_arena=%d exit_when_done=%d warmup_max_s=%.0f"),
		bHideUnits ? 1 : 0, FilmEvery, HitchMs, HitchEvery, bNoArena ? 1 : 0, bExitWhenDone ? 1 : 0, WarmupMaxSec);
	UE_LOG(LogChimeraSim, Display, TEXT("option leg=%s p_commit=%s p_dirty=%s"), *Leg, *PCommit, *PDirty);
	UE_LOG(LogChimeraSim, Display, TEXT("option meshes=%s dead_under_ground=%d shot_freeze=%d shot_samples=%d verify_every=%d"), *MeshesPath,
		bDeadUnderGround ? 1 : 0, bShotFreeze ? 1 : 0, ShotSamples, VerifyEvery);
	// The two lines run_sim.ps1 asserts (the seed as parsed, and the shot list as parsed).
	UE_LOG(LogChimeraSim, Display, TEXT("seed=%s"), *SeedHex());
	UE_LOG(LogChimeraSim, Display, TEXT("shots=%s"), *ShotList());
}

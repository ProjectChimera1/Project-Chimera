// Copyright Chimera. Command-line options of a sim run (plan A 3.7 "Options"), parsed once and echoed to the log and the trace.
#pragma once

#include "CoreMinimal.h"

/** One planned screenshot (P/SimTrial/shots.json is a list of {tick, camera}). Taken by A11's renderer; A10 parses and echoes. */
struct FChimeraSimShot
{
	int32 Tick = 0;
	FString Camera;
};

/**
 * -ChimeraSimContent= -ChimeraSimScenario= -ChimeraSimOrders= -ChimeraSimSeed=<decimal or 0x hex> -ChimeraSimTicks=1440
 * -ChimeraSimAi -ChimeraSimOut= -ChimeraSimShots=<shots.json> -ChimeraSimHideUnits -ChimeraSimFilmEvery=N -ChimeraSimHitchMs=
 * -ChimeraSimHitchEvery= -ChimeraSimNoArena -ChimeraSimExitWhenDone -ChimeraSimWarmupMaxSec= -ChimeraSimLeg= -ChimeraSimPCommit=
 * -ChimeraSimPDirty=. Strings use FParse::Value(..., bShouldStopOnSeparator=false) and the seed FCString::Strtoui64(S, End, 0)
 * (plan A F26: the uint64 overload reads decimal only and the default string parse stops at ',' or ')').
 */
struct FChimeraSimOptions
{
	FString ContentRoot;
	FString Scenario;
	FString Orders;
	FString SeedText;
	uint64 Seed = 0xC0FFEE1234567890ull;
	int32 Ticks = 1440;
	bool bAi = false;
	FString OutDir;
	FString ShotsPath;
	TArray<FChimeraSimShot> Shots;
	bool bHideUnits = false;
	int32 FilmEvery = 0;
	int32 HitchMs = 0;
	int32 HitchEvery = 0;
	bool bNoArena = false;
	bool bExitWhenDone = false;
	double WarmupMaxSec = 900.0;
	FString Leg = TEXT("unreal");
	FString PCommit = TEXT("unknown");
	FString PDirty = TEXT("?");

	/** Parses FCommandLine::Get(); false with OutError on a malformed value (missing optional values keep defaults). */
	bool Parse(FString& OutError);

	/** "0x" + 16 upper-case hex digits of the parsed seed (the trace header form). */
	FString SeedHex() const;

	/** "0:overview,0:wide,60:close,..." (log and trace form; never passed back on a command line). */
	FString ShotList() const;

	/** Logs every parsed option as "LogChimeraSim: option <name>=<value>" plus the "seed=" and "shots=" lines run_sim.ps1 asserts. */
	void Echo() const;
};

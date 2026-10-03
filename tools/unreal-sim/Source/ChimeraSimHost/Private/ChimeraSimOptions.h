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
 * -ChimeraSimPDirty=; A11: -ChimeraSimShots=none (no shots), -ChimeraSimMeshes=<unit_meshes.json> -ChimeraSimDeadUnderGround
 * -ChimeraSimNoShotFreeze -ChimeraSimShotSamples=200 -ChimeraSimVerifyEvery=N. Strings use FParse::Value(..., bShouldStopOnSeparator=false) and the seed FCString::Strtoui64(S, End, 0)
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
	/** Mesh table of the renderer (default P/SimTrial/unit_meshes.json). */
	FString MeshesPath;
	/** Plan A 3.7 fallback: dead/phased instances go under the ground plane instead of scale 0. */
	bool bDeadUnderGround = false;
	/** Shot pairs hold exposure and pin the TSR sequence (ChimeraTerrain S5's temporal_freeze) so shot and hidden differ only by units. */
	bool bShotFreeze = true;
	/** Projected units per army per shot (plan A 3.7: 200). */
	int32 ShotSamples = 200;
	/** Extra verify holds every N ticks (0 = only the digest and shot ticks); for diagnostic runs such as a long ai run. */
	int32 VerifyEvery = 0;

	/** Parses FCommandLine::Get(); false with OutError on a malformed value (missing optional values keep defaults). */
	bool Parse(FString& OutError);

	/** "0x" + 16 upper-case hex digits of the parsed seed (the trace header form). */
	FString SeedHex() const;

	/** "0:overview,0:wide,60:close,..." (log and trace form; never passed back on a command line). */
	FString ShotList() const;

	/** Logs every parsed option as "LogChimeraSim: option <name>=<value>" plus the "seed=" and "shots=" lines run_sim.ps1 asserts. */
	void Echo() const;
};

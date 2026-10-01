// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * Capture automation. Switches: -HudShot=<abs.png> -HudShotFrames=N (90) -HudCompileWait=S (1800) -HudShotTimeout=S (120) -HudWarmup.
 * Phase 1 waits for shader and asset compiles to go idle; phase 2 counts N frames and requests a UI-inclusive screenshot.
 * Every failure logs "LogChimeraHud: Error: SHOT TIMEOUT|SHOT MISSING|SHOT NO CALLBACK|SHOT COMPILE TIMEOUT" and exits FORCED with 3|4|5|6.
 */
class FChimeraHudShot
{
public:
	/** Starts the automation when -HudShot or -HudWarmup is on the command line; otherwise does nothing. */
	static void StartIfRequested();
};

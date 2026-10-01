// Copyright Chimera. AChimeraSimDirector: drives one scripted sim run inside -game (plan A 3.7 "Warm-up", "Loop", "Timing";
// EXECUTION 2.2 exits). It owns the session, replays the frozen order CSV through chimera_submit_order (the one way in), steps
// at a fixed 30 Hz from wall-clock pacing that never reaches the sim, and writes the trace of plan A 3.5.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "ChimeraSimOptions.h"
#include "ChimeraSimDirector.generated.h"

/** One row of the frozen order CSV: tick,faction,unit_ref,cmd,x_raw,z_raw,slot. */
struct FChimeraOrderRow
{
	int32 V[7] = {};
	int32 Tick() const { return V[0]; }
};

/** Per-frame record of frames.csv. */
struct FChimeraFrameRow
{
	int32 Frame = 0;
	double DtMs = 0.0;
	int32 Steps = 0;
	double SimMs = 0.0;
	int32 Tick = 0;
	int64 Alive = 0;
	double Alpha = 0.0;
	bool bHitch = false;
};

/**
 * Exit codes (forced, after closing files; EXECUTION 2.2): 3 DLL load / ABI (module), 4 warm-up timeout, 5 session create
 * failed (-3/-4/-6), 6 submit/step/checksum failure, 7 an output file could not be written, 8 bad options or order CSV.
 * Success logs "LogChimeraSim: RESULT ..." and, with -ChimeraSimExitWhenDone, exits non-forced with 0.
 */
UCLASS(NotBlueprintable)
class AChimeraSimDirector : public AActor
{
	GENERATED_BODY()

public:
	AChimeraSimDirector();

	/** Called by the game mode right after spawning, before BeginPlay runs the setup. */
	void Configure(const FChimeraSimOptions& InOptions) { Options = InOptions; }

	virtual void Tick(float DeltaSeconds) override;

	/** Interpolation alpha of the last frame (acc * 30, in [0, 1)); A11's renderer lerps prev -> pos by it. */
	double GetAlpha() const { return Alpha; }
	int32 GetSession() const { return Session; }
	int32 GetSimTick() const { return SimTick; }

	static constexpr double TickSeconds = 1.0 / 30.0;
	static constexpr double MaxFrameDelta = 0.25;
	static constexpr int32 MaxStepsPerFrame = 8;
	static constexpr int32 IdleFramesNeeded = 60;
	static constexpr double SettleSeconds = 8.0;

protected:
	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type Reason) override;

private:
	enum class EState : uint8 { Setup, Warmup, Settle, Run, Done, Failed };

	bool Setup();
	bool LoadOrders(FString& OutError);
	void TickWarmup();
	void TickRun(double Dt);
	bool StepOnce();
	void RecordDigest(int32 T);
	void Finish();
	void Fail(uint8 Code, const FString& Message);
	FString BuildTrace(bool bComplete) const;
	bool WriteOutputs(bool bComplete, FString& OutError) const;
	static uint64 OrdersDigest(const TArray<FChimeraOrderRow>& Rows);
	int64 AliveNow() const;

	FChimeraSimOptions Options;
	EState State = EState::Setup;
	int32 Session = 0;

	// Order replay.
	TArray<FChimeraOrderRow> Rows;
	int32 Cursor = 0;
	int32 Applied = 0;
	int32 Dropped = 0;

	// Trace.
	TArray<uint32> Hashes;
	TMap<int32, TPair<uint64, uint64>> Digests;
	TArray<int32> DigestTicks;
	uint64 Pre[6] = {};
	int64 UnitsAtStart = 0;
	int32 SimTick = 0;
	FString ScenarioSha, OrdersSha, DllSha, UeModuleSha, UeModulePath;
	int64 AliveEnd = 0;
	int32 Verdict = -1;

	// Pacing and stats.
	double Acc = 0.0;
	double Alpha = 0.0;
	int32 MaxStepsSeen = 0;
	int32 RunFrame = 0;
	int32 Hitches = 0;
	TArray<FChimeraFrameRow> Frames;
	TArray<double> StepMs;

	// Warm-up.
	double SetupTime = 0.0;
	double WarmupDoneTime = 0.0;
	int32 IdleFrames = 0;
	double SettleStart = 0.0;
	double WarmupWaitS = 0.0;
	double RunStartTime = 0.0;
};

// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.8: the unattended script director. Runs the ops of -ChimeraTerrainScript=<json> one after another (an op may span frames),
// writes everything to -ChimeraTerrainOut and exits with the EXECUTION 2.2 contract: results.json is written and closed first;
// failure = forced exit with the code (2 op failed, 3 op timeout); success = results.json "completed": true and a normal exit 0.
//
// Script: {"name": "<id>", "ops": [ {"op": "<name>", ...}, ... ]}. C3 ops:
//   camera {pose}                         rts80 | oblique | spike (FTerrainCameraPose)
//   look {mode}                           compare | full (plan C 3.5)
//   visible {value}                       0 | 1: hide or show every terrain chunk
//   settle {frames=30}                    shader + asset compile queues empty and no mesh/splat work in flight, then N frames (600 s)
//   idle {frames | seconds}
//   stroke {mode, d, s, layer, path, ticks, per_frame=1}
//                                         exactly `ticks` ApplyTick calls, `per_frame` per frame, independent of DeltaTime (plan C 3.4);
//                                         path = [[x, y], ...] terrain metres (= Unreal cm / 100); the centre moves along it by arc length
//   hash {name}                           height and splat FNV-1a into results.json
//   shot {name}                           wait for in-flight work + 2 frames, HighResShot 1920x1080 to <out>/<name>.png, wait for the PNG (60 s)
//   g1_regions {pose, hill:[x,y,r], ring:[a,b]} | {pose, spike:[x,y,r]}
//                                         analytic pixel sets for gate G1 into footprints.json (sun and shadow sides, cast shadow, lit ground;
//                                         the spike's projected area)
//   fail                                  fail the run (exit 2): proves the exit-code contract
//   exit                                  finish successfully now
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Dom/JsonObject.h"
#include "Game/ChimeraTerrainGameMode.h"
#include "Test/TerrainMetrics.h"
#include "TerrainScriptDirector.generated.h"

class ATerrainActor;
class ATerrainLighting;
class APlayerController;

UCLASS()
class CHIMERATERRAIN_API ATerrainScriptDirector : public AActor
{
	GENERATED_BODY()

public:
	ATerrainScriptDirector();

	/** Exit codes (plan C 3.8). */
	static constexpr uint8 ExitOk = 0;
	static constexpr uint8 ExitOpFailed = 2;
	static constexpr uint8 ExitOpTimeout = 3;

	void Start(const FChimeraTerrainOptions& InOptions, ATerrainActor* InTerrain, ATerrainLighting* InLighting, bool bTerrainOk);

	virtual void Tick(float DeltaSeconds) override;

private:
	enum class EStep : uint8 { Running, Done, Failed, TimedOut };

	UPROPERTY(Transient)
	TObjectPtr<ATerrainActor> Terrain;
	UPROPERTY(Transient)
	TObjectPtr<ATerrainLighting> Lighting;

	FChimeraTerrainOptions Options;
	FString ScriptName;
	TArray<TSharedPtr<FJsonObject>> Ops;
	bool bStarted = false;
	bool bFinished = false;
	FString StartError;

	int32 OpIndex = 0;
	int32 OpFrame = 0;
	int32 OpPhase = 0;
	double OpStartSeconds = 0.0;
	double OpPhaseSeconds = 0.0;
	FString OpError;

	// Per-op scratch.
	int32 StrokeTicksDone = 0;
	int64 ShotLastSize = -1;
	int32 ShotStableFrames = 0;
	FDateTime ShotRequestUtc;
	FString ShotPath;

	FTerrainMetrics Metrics;
	FString CurrentPose = TEXT("rts80");
	TSharedRef<FJsonObject> Results = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Hashes = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Shots = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Footprints = MakeShared<FJsonObject>();
	TArray<TSharedPtr<FJsonValue>> Strokes;
	TArray<TSharedPtr<FJsonValue>> Settles;
	TArray<TSharedPtr<FJsonValue>> Timeline;

	bool LoadScript(FString& OutError);
	APlayerController* GetPC() const;

	/** Run the current op for this frame. */
	EStep StepOp(const FJsonObject& Op, const FString& Name);
	double OpTimeoutSeconds(const FString& Name, const FJsonObject& Op) const;

	EStep StepCamera(const FJsonObject& Op);
	EStep StepLook(const FJsonObject& Op);
	EStep StepVisible(const FJsonObject& Op);
	EStep StepSettle(const FJsonObject& Op);
	EStep StepIdle(const FJsonObject& Op);
	EStep StepStroke(const FJsonObject& Op);
	EStep StepHash(const FJsonObject& Op);
	EStep StepShot(const FJsonObject& Op);
	EStep StepG1Regions(const FJsonObject& Op);

	static bool CompileQueuesIdle();
	bool ApplyPose(const FString& Pose);
	void Finish(uint8 Code, const FString& Reason);
	bool WriteJsonFile(const FString& FileName, const TSharedRef<FJsonObject>& Obj) const;
	FString OutPath(const FString& FileName) const;
};

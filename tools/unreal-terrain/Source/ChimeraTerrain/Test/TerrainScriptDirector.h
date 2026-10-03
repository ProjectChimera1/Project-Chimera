// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.8: the unattended script director. Runs the ops of -ChimeraTerrainScript=<json> one after another (an op may span frames),
// writes everything to -ChimeraTerrainOut and exits with the EXECUTION 2.2 contract: results.json is written and closed first;
// failure = forced exit with the code (2 op failed, 3 op timeout); success = results.json "completed": true and a normal exit 0.
//
// Script: {"name": "<id>", "ops": [ {"op": "<name>", ...}, ... ]}. Any op may carry "phase": "<label>": the label is stored with every
// frame sample and stroke tick from then on (results.json metrics.phases, ticks.csv).
//
// Ops (C3):
//   camera {pose}                         rts80 | oblique | closeup | spike (FTerrainCameraPose)
//   look {mode}                           compare | full (plan C 3.5)
//   visible {value}                       0 | 1: hide or show every terrain chunk
//   settle {frames=30}                    shader + asset compile queues empty and no mesh/splat work in flight, then N frames (600 s)
//   idle {frames | seconds}
//   stroke {mode, d, s, layer, path, ticks, per_frame=1, hitch=false, fp="<set>"|["<set>",...]}
//                                         exactly `ticks` ApplyTick calls, `per_frame` per frame, independent of DeltaTime (plan C 3.4);
//                                         path = [[x, y], ...] terrain metres (= Unreal cm / 100); the centre moves along it by arc length.
//                                         hitch=true: with -ChimeraTerrainHitchMs=<ms> the game thread sleeps that long at the middle tick (P8).
//                                         fp: adds the stroke's footprint discs to the named set(s) for project_footprint.
//   hash {name}                           height, splat and sim-grid FNV-1a into results.json
//   shot {name}                           wait for in-flight work + 2 frames, HighResShot 1920x1080 to <out>/<name>.png, wait for the PNG (60 s)
//   g1_regions {pose, hill:[x,y,r], ring:[a,b]} | {pose, spike:[x,y,r]}
//                                         analytic pixel sets for gate G1 into footprints.json
//   fail                                  fail the run (exit 2): proves the exit-code contract
//   exit                                  finish successfully now
// Ops (C4):
//   paint {layer, d, s, path, ticks, ...} a stroke with mode paint
//   undo {n=1} | redo {n=1}               undo / redo n strokes (fails when there is nothing left)
//   hitch {ms}                            sleep the game thread
//   save                                  terrain.json (sim_grid_fnv + 16 probes), height.r32, splat.rgba8 into the out dir
//   load {dir}                            replace the terrain with the saved files (dir, else -ChimeraTerrainLoad=), rebuild, re-upload
//   random_walk {seconds=60, seed=1, cycle_s=5, diameters=[5,20,60,100], s=10, speed=8, bounds=100, hz=30}
//                                         seeded 30 Hz brush walk: one mode per cycle_s (the five in turn), each diameter in turn inside it
//                                         (cycle_s / N each), painting layers 1..3; ticks beyond 15 per frame are dropped and counted
//                                         (plan C 3.4 mouse rule)
//   soak {minutes, ...random_walk args}   random_walk for minutes*60 s sampling UsedPhysical and live UBodySetups (total, RMC-owned) every 2 s,
//                                         then one blocking gc and a final sample (memory.after_gc_index; P6 reads its after-GC figures there)
//   gc                                    blocking garbage collection, logs ms and memory before/after
//   csv {mode: start|stop}                CsvProfiler capture into the out dir (stop waits for the file)
//   movie {frames}                        GIsDumpingMovie = frames; waits for the MovieFrame PNGs (UI excluded); results.json movies[] lists
//                                         the exact files this op wrote (older MovieFrame files in the folder are not the run's)
//   depthcheck {pose, name, n=10000, seed=1234}
//                                         SceneCapture2D SCS_SceneDepth R32f of the terrain at the pose vs the analytic ray's view depth
//                                         at n seeded pixels; with fp=<stroke set> also fp_n (2000) seeded pixels drawn from that set's
//                                         projected footprint (results: depthchecks[].footprint), so a small stale region cannot hide (P11); results.json depthchecks[]
//   project_footprint {pose, name, as}    project the named stroke-footprint set to screen pixels at the pose into footprints.json[pose][as]
//                                         (as defaults to name; the same set can be projected in several terrain states)
//   await_mouse {n, pose=rts80, tail=0, hitch_ms=0, hitch_stroke=0}
//                                         writes mouse_targets.json, arms the mouse controller, then waits (120 s) until it recorded n mouse
//                                         strokes and `tail` applied key actions after them, and disarms it (C8). Outside this op a scripted
//                                         run's controller ignores the mouse and the keys. hitch_ms: one game-thread sleep inside stroke
//                                         hitch_stroke after 20 of its ticks (the 15-tick catch-up cap, ticks_dropped).
// Ops (C5, plan C 3.6 / 3.8):
//   wait_collision {name}                 (30 s) every collision future resolved, and every chunk's body has TriMeshGeometries > 0, is the
//                                         body its component's physics state uses, and a vertical probe ray at the chunk centre hits it
//   verify_collision {name, pose=rts80, n=2000, seed=4242, vertex_cap=4000}
//                                         n seeded rays from the pose's frustum plus a vertical ray at every vertex inside the vertex rects
//                                         whose collision was rewritten since the last verify (cap: evenly strided), each cast twice:
//                                         LineTraceSingleByChannel(ECC_Visibility) (World.h:2161) and the analytic pick; disagreements
//                                         counted both ways, |dz| (cm) of the two hit points over the rays both hit (results collision.verifies[])
//   residue                               REPORTED only, after the gated soak samples: clear the undo history, blocking gc, FMemory::Trim, then
//                                         a sample with bodies (results memory.residue_*); P6's gated after-GC figures never read it
// Ops (scatter S4, plan C scatter 3.8; each fails with "needs -ChimeraTerrainScatter=1" unless the run started with scatter):
//   scatter {value}                       1: enable (load, dirty all, bump the epoch, fill); 0: disable (bump the epoch, cancel, clear, reset)
//   scatter_wait {timeout_s=120}          until scatter has no pending work, then 2 frames (results scatter.waits[].ms)
//   scatter_visible {value, layers="all"} hide or show layers (grass, groundcover, shrubs, trees, rocks) by cull distance; no proxy change
//   scatter_verify {name}                 live == reference, ISM readback == BuildInstance of the unit states, z and up axis from HF, no tile
//                                         pending (results scatter.verifies[]; recorded, the parser gates it)
//   scatter_fresh {name}                  rebuild every scatter proxy from the CPU arrays, force-invalidate the directional VSM for 10 frames,
//                                         settle 150, shot <name>_fresh (SX18's oracle)
//   hash gains scatter_fnv, scatter_live_fnv, scatter_count while scatter is enabled.
// Ops (scatter S5, plan C scatter 3.8; implemented in TerrainScatterOps.cpp, same "needs -ChimeraTerrainScatter=1" rule):
//   scatter_dump {name}                   every applied record in canonical LE binary (HashRecord field order, 45 bytes each, sorted by key) to
//                                         <out>/scatter_<name>.bin; results scatter.dumps[] {path, records, sha256, per-class counts}
//   scatter_check {name, fp}              per class: instances in the inner half radius (core) of every disc of footprint set fp, what the
//                                         generator yields there with the splat forced to pure grass (same heights, seed), and the outer band
//                                         (0.5R .. R + 2 m); results scatter.checks[]
//   scatter_counts {name}                 per tile and class counts into <out>/scatter_counts.json (object keyed by name)
//   scatter_view_counts {pose, name}      analytic: per class and mesh, instances inside the frustum and inside their cull end (not what the GPU drew)
//   scatter_target {class, near:[x,y], as, zone}   stores the nearest instance's XY and key; zone = core|edge|grove|lone (trees); a stroke path
//                                         may then be "@<as>" and scatter_verify reports each stored key's existence and z change
//   movie_start {frames} / movie_wait    the `movie` op split in two so the ops between them run while the frames are dumped (VIDEOX)
//   scatter_mask {pose, name, layers, near, radius, union_with}   projected instance bounds of the layers and the analytic sun-shadow footprint of
//                                         casters (sun from ATerrainLighting) as row runs into footprints.json[pose][name]; near="@<as>" with radius
//                                         (metres) keeps the instances around that target; only="@<as>" keeps that target's instance alone;
//                                         union_with merges an earlier mask of the same pose; caster shadows are taken both on the instance's
//                                         base plane and where the sun ray meets HF's drawn surface
// Results (task S5): timeline rows carry frame_start/frame_end (GFrameCounter) and the op's name; results.json carries script_path,
// script_sha256 and sg (the effective scalability groups of every run); scatter.proxy_events lists frame-stamped proxy and compile events.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Dom/JsonObject.h"
#include "Misc/DateTime.h"
#include "Math/RandomStream.h"
#include "Async/Future.h"
#include "Game/ChimeraTerrainGameMode.h"
#include "Test/TerrainMetrics.h"
#include "TerrainScriptDirector.generated.h"

class ATerrainActor;
class UPrimitiveComponent;
class ATerrainLighting;
class ATerrainScatter;
class APlayerController;
class USceneCaptureComponent2D;
class UTextureRenderTarget2D;
struct FScatterTarget;

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
	/** The scatter actor (dormant unless the run enabled scatter); call before Start. */
	void SetScatter(ATerrainScatter* InScatter) { Scatter = InScatter; }

	virtual void Tick(float DeltaSeconds) override;

private:
	enum class EStep : uint8 { Running, Done, Failed, TimedOut };

	UPROPERTY(Transient)
	TObjectPtr<ATerrainActor> Terrain;
	UPROPERTY(Transient)
	TObjectPtr<ATerrainLighting> Lighting;
	UPROPERTY(Transient)
	TObjectPtr<ATerrainScatter> Scatter;
	UPROPERTY(Transient)
	TObjectPtr<USceneCaptureComponent2D> Capture;
	UPROPERTY(Transient)
	TObjectPtr<UTextureRenderTarget2D> CaptureTarget;

	FChimeraTerrainOptions Options;
	FString ScriptName;
	/** SHA-256 of the script file's bytes (results.json script_sha256). */
	FString ScriptSha256;
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
	bool bStrokeHitched = false;
	int64 ShotLastSize = -1;
	int32 ShotStableFrames = 0;
	FDateTime ShotRequestUtc;
	FString ShotPath;

	// random_walk / soak
	struct FWalkState
	{
		bool bActive = false;
		bool bSoak = false;
		FRandomStream Rng;
		FVector2D Pos = FVector2D::ZeroVector;
		double Heading = 0.0;
		double ElapsedS = 0.0;
		double Accumulator = 0.0;
		double DurationS = 60.0;
		double CycleS = 5.0;
		double SpeedMps = 8.0;
		double BoundsM = 100.0;
		int32 Hz = 30;
		float Strength = 10.0f;
		TArray<float> Diameters;
		int32 Segment = -1;
		int32 PaintSegments = 0;
		int64 Ticks = 0;
		int64 Dropped = 0;
		int32 Segments = 0;
		double NextMemSampleS = 0.0;
	};
	FWalkState Walk;

	// csv
	bool bCsvActive = false;
	TSharedPtr<TSharedFuture<FString>> CsvFuture;
	FString CsvStartedPath;
	// movie
	int32 MovieRequested = 0;
	/** MovieFrame files an earlier movie op of this run listed (a later movie never lists them again). */
	TSet<FString> MovieClaimed;
	/** GFrameCounter at the current op's first step (timeline frame_start). */
	uint64 OpStartFrame = 0;
	FDateTime MovieStartUtc;
	// await_mouse
	int32 MouseTargetCount = 0;
	bool bMouseHitched = false;
	// memory sampling every 2 s of wall time for every run (UsedPhysical only; the UBodySetup walk runs only inside soak, at forced
	// samples and at gc, so measured C1/C1U runs carry no periodic object walk)
	double NextMemSampleSeconds = 0.0;
	double RunStartSeconds = 0.0;
	// residue (reported): when the soak's GC ran, and what the clear + GC + trim saw
	double AfterGcSeconds = 0.0;
	double ResidueUsedBeforeMb = 0.0;
	double ResidueUsedImmediateMb = 0.0;
	double ResidueUndoClearedMb = 0.0;
	double ResidueGcMs = 0.0;

	FTerrainMetrics Metrics;
	FString CurrentPose = TEXT("rts80");
	TSharedRef<FJsonObject> Results = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Hashes = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Shots = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Footprints = MakeShared<FJsonObject>();
	TArray<TSharedPtr<FJsonValue>> Strokes;
	TArray<TSharedPtr<FJsonValue>> Settles;
	TArray<TSharedPtr<FJsonValue>> Timeline;
	TArray<TSharedPtr<FJsonValue>> DepthChecks;
	TArray<TSharedPtr<FJsonValue>> Skipped;
	TArray<TSharedPtr<FJsonValue>> Walks;
	TArray<TSharedPtr<FJsonValue>> Hitches;
	/** temporal_freeze op rows (task S5): each request, whether the cvars exist in this build, and the frame it took effect. */
	TArray<TSharedPtr<FJsonValue>> TemporalFreezes;
	TArray<TSharedPtr<FJsonValue>> Gcs;
	TArray<TSharedPtr<FJsonValue>> Movies;
	TArray<TSharedPtr<FJsonValue>> UndoRedos;
	TArray<TSharedPtr<FJsonValue>> CollisionWaits;
	TArray<TSharedPtr<FJsonValue>> CollisionVerifies;
	/** Per-chunk collision submit counts at the end of the previous wait_collision (what was touched since). */
	TArray<int64> CollisionSubmitsAtLastWait;
	TSharedPtr<FJsonObject> Saved;
	TSharedPtr<FJsonObject> Loaded;
	TSharedPtr<FJsonObject> CsvInfo;
	// scatter ops (S4)
	TArray<TSharedPtr<FJsonValue>> ScatterVerifies;
	TArray<TSharedPtr<FJsonValue>> ScatterWaits;
	TArray<TSharedPtr<FJsonValue>> ScatterFreshes;
	TArray<TSharedPtr<FJsonValue>> ScatterToggles;
	int32 FreshStage = 0;
	int32 FreshFrames = 0;
	int32 FreshRebuilt = 0;
	FString FreshCvarOld;
	TSharedPtr<FJsonObject> FreshShotOp;
	// scatter ops (S5)
	TArray<TSharedPtr<FJsonValue>> ScatterDumps;
	TArray<TSharedPtr<FJsonValue>> ScatterChecks;
	TArray<TSharedPtr<FJsonValue>> ScatterCountsRows;
	TArray<TSharedPtr<FJsonValue>> ScatterViewCounts;
	TArray<TSharedPtr<FJsonValue>> ScatterMasks;
	TSharedRef<FJsonObject> ScatterCountsFile = MakeShared<FJsonObject>();
	/** scatter_target results by name (X, Y metres, key, class, z). */
	TMap<FString, TSharedPtr<FScatterTarget>> ScatterTargets;
	/** scatter_mask bitmaps by "pose/name" (viewport-sized, 1 byte a pixel). */
	TMap<FString, TArray<uint8>> ScatterMaskBits;
	TMap<FString, FIntPoint> ScatterMaskSize;
	/** Footprint discs (x, y, radius metres) by set name, filled by strokes with "fp". */
	TMap<FString, TArray<FVector>> FootprintSets;

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
	EStep StepStroke(const FJsonObject& Op, bool bForcePaint);
	EStep StepHash(const FJsonObject& Op);
	EStep StepShot(const FJsonObject& Op);
	EStep StepG1Regions(const FJsonObject& Op);
	EStep StepUndoRedo(const FJsonObject& Op, bool bUndo);
	EStep StepHitch(const FJsonObject& Op);
	/** temporal_freeze {value}: 1 freezes the renderer's temporal sequences (TSR jitter at index 0, frame-indexed noise) so an image pair of a static scene is
	 * repeatable (A/A floor about 0); 0 restores the defaults. Absent in Shipping (the cvars are compiled out): recorded as unavailable, never failed. The row
	 * records the cvars' read-back after the set (a set at ECVF_SetByCode is refused when an ini or ExecCmds holds the cvar higher); the parser trusts that.
	 * COMPARE FROZEN FRAMES ONLY WITHIN ONE FREEZE WINDOW: each freeze pins the view's frame index at whatever value it had, so two windows render different
	 * frame-indexed noise (about 0.2 % of the frame in S5's s1x_a). Within a window TSR's history still depends on what was on screen before (S5 round 4:
	 * the same state one scatter hide/restore apart differs by about 5 % of the last2 footprint with TSR on, and not at all with anti-aliasing off). */
	EStep StepTemporalFreeze(const FJsonObject& Op);
	/** Op `exposure` (task S6): "mode": "hold" keeps the adapted exposure (ATerrainLighting::SetExposureHold), "auto" releases it. */
	EStep StepExposure(const FJsonObject& Op);
	EStep StepSave(const FJsonObject& Op);
	EStep StepLoad(const FJsonObject& Op);
	EStep StepRandomWalk(const FJsonObject& Op, bool bSoak);
	EStep StepGc(const FJsonObject& Op);
	/** Reported-only residue sample after the soak (undo cleared, GC, FMemory::Trim); see TerrainMetrics MarkResidue. */
	EStep StepResidue(const FJsonObject& Op);
	EStep StepCsv(const FJsonObject& Op);
	EStep StepMovie(const FJsonObject& Op);
	EStep StepDepthCheck(const FJsonObject& Op);
	EStep StepProjectFootprint(const FJsonObject& Op);
	EStep StepAwaitMouse(const FJsonObject& Op);
	EStep StepWaitCollision(const FJsonObject& Op);
	EStep StepVerifyCollision(const FJsonObject& Op);
	/** Scatter ops need a run started with scatter (OpError otherwise). */
	bool NeedScatter();
	EStep StepScatter(const FJsonObject& Op);
	EStep StepScatterWait(const FJsonObject& Op);
	EStep StepScatterVisible(const FJsonObject& Op);
	EStep StepScatterVerify(const FJsonObject& Op);
	EStep StepScatterFresh(const FJsonObject& Op);
	EStep StepScatterDump(const FJsonObject& Op);
	EStep StepScatterCheck(const FJsonObject& Op);
	EStep StepScatterCounts(const FJsonObject& Op);
	EStep StepScatterViewCounts(const FJsonObject& Op);
	EStep StepScatterTarget(const FJsonObject& Op);
	EStep StepScatterMask(const FJsonObject& Op);
	/** movie_start {frames}: GIsDumpingMovie = frames and carry on (VIDEOX runs its strokes while the frames are dumped); movie_wait finishes it like `movie`. */
	EStep StepMovieStart(const FJsonObject& Op);
	EStep StepMovieWait(const FJsonObject& Op);
	/** Layer list ("all" or "grass,trees,...") to a layer bit mask; false with OpError on an unknown name. */
	bool ParseLayerMask(const FString& Spec, uint32& OutMask);
	/** The scatter block's S5 arrays (dumps, checks, counts files, view counts, masks, targets), added by Finish. */
	void AddScatterOpResults(FJsonObject& ScatterBlock) const;
	/** Physics trace (ECC_Visibility, complex) of a terrain-metre ray; true on a blocking hit, OutTerrain = the hit was a terrain chunk. */
	bool PhysicsRay(const FVector& OriginM, const FVector& DirM, double MaxM, FVector& OutHitM, bool& OutTerrain,
		const UPrimitiveComponent** OutComponent = nullptr) const;
	/** results.json "collision": options, cook and GT-apply series, waits, verifies (plan C 5 P5). */
	TSharedRef<FJsonObject> CollisionToJson() const;

	static bool CompileQueuesIdle();
	bool ApplyPose(const FString& Pose);
	void SetPhase(const FString& Name);
	/** Mark the current phase in the CSV capture (event `phase_<name>`), when one is running. */
	void RecordPhaseEvent() const;
	void SampleMemoryIfDue(bool bForce);
	void Finish(uint8 Code, const FString& Reason);
	bool WriteJsonFile(const FString& FileName, const TSharedRef<FJsonObject>& Obj) const;
	FString OutPath(const FString& FileName) const;
};

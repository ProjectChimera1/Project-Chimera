// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.8 metrics: per-frame frame/GT/RT/GPU ms (overall and per phase), per-tick brush timings (by diameter, mode and phase),
// render-thread acceptance latency of ranged edits, memory and UBodySetup samples; summarised into results.json, ticks.csv.
#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Game/TerrainActor.h"
#include "Render/TerrainChunkRenderer.h"

/** A growing sample series with order statistics. */
struct FTerrainSeries
{
	TArray<double> Values;

	void Add(double V) { Values.Add(V); }
	int32 Num() const { return Values.Num(); }
	/** Nearest-rank percentile, P in [0, 100]; 0 when empty. */
	double Percentile(double P) const;
	double Max() const;
	double Mean() const;
	/** {"n","p50","p99","max","mean"}. */
	TSharedRef<FJsonObject> ToJson() const;
};

/** One memory sample (plan C 3.8 `soak`): process working set and live UBodySetup objects, total and RMC-owned. */
struct FTerrainMemorySample
{
	double TimeSeconds = 0.0;
	double UsedPhysicalMB = 0.0;
	/** -1 when the sample did not count bodies (only soak samples, forced samples and gc count them). */
	int32 BodiesTotal = -1;
	int32 BodiesRmc = -1;
	/** Game-thread cost of taking this sample (the UBodySetup walk dominates when bodies are counted). */
	double SampleMs = 0.0;
	/** Undo/redo history held at the sample (MB; -1 = not recorded): retained editing state, not a leak, reported beside the growth. */
	double UndoMB = -1.0;
};

class FTerrainMetrics
{
public:
	/** Name the phase every following frame sample belongs to ("default" until set). */
	void SetPhase(const FString& Name);
	const FString& GetPhase() const { return Phases[Current].Name; }

	/** Sample the engine's per-frame timers (RenderTimer.h:8-17, RHIGetGPUFrameCycles DynamicRHI.h:1250). Call once per frame. */
	void SampleFrame(double DeltaSeconds);

	/**
	 * Memory sample now (FPlatformMemory UsedPhysical, GenericPlatformMemory.h:141). With bCountBodies the live UBodySetup objects are
	 * counted too: total, and those whose outer is a URealtimeMesh (owner per RealtimeMesh.cpp:377). The sample times itself (SampleMs).
	 */
	void SampleMemory(double TimeSeconds, bool bCountBodies, double UndoMB = -1.0);
	const TArray<FTerrainMemorySample>& GetMemory() const { return Memory; }
	/** Growth figures count from the latest sample (the soak's start), not from the process start (shaders, assets). */
	void MarkMemoryBaseline() { MemoryBaseline = Memory.Num() - 1; }
	/** The latest sample is the one taken right after the soak's blocking GC: P6's after-GC figures read it, not Memory.Last(). */
	void MarkAfterGc() { AfterGcIndex = Memory.Num() - 1; }
	/**
	 * The latest sample is the reported residue sample: taken after the gated samples, once the undo history was cleared, a second
	 * blocking GC ran and the allocator was trimmed (FMemory::Trim). Never gated; it measures what is left without the retained history.
	 */
	void MarkResidue() { ResidueIndex = Memory.Num() - 1; }
	/** The latest sample is the reported one taken at least 12 s after the soak's GC (Mimalloc's page-reset delay has passed). Never gated. */
	void MarkDelayed() { DelayedIndex = Memory.Num() - 1; }

	FTerrainSeries FrameMs;
	FTerrainSeries GameThreadMs;
	FTerrainSeries RenderThreadMs;
	FTerrainSeries GpuMs;

	/** {frame_ms, gt_ms, rt_ms, gpu_ms, phases:{name:{...}}}. */
	TSharedRef<FJsonObject> FramesToJson() const;
	/** Tick statistics from the terrain's tick log: overall, by_diameter, by_mode, by_phase (apply, normals, upload, splat, total). */
	static TSharedRef<FJsonObject> TicksToJson(const TArray<FTerrainTickSample>& Samples, const TArray<FString>& PhaseNames);
	/** ticks.csv text: one row per tick. */
	static FString TicksToCsv(const TArray<FTerrainTickSample>& Samples, const TArray<FString>& PhaseNames);
	/** {n, frames:{p50,p99,max,mean,hist}, ms:{...}} from the renderer's finished ranged edits. */
	static TSharedRef<FJsonObject> LatencyToJson(const TArray<ChimeraTerrain::FTerrainEditLatency>& Latencies);
	/** {samples:[...], start_mb, end_mb, peak_mb, growth_peak_mb, growth_end_mb, peak_bodies_total, peak_bodies_rmc, ...}. */
	TSharedRef<FJsonObject> MemoryToJson() const;

private:
	struct FPhase
	{
		FString Name;
		FTerrainSeries Frame;
		FTerrainSeries Gt;
		FTerrainSeries Rt;
		FTerrainSeries Gpu;
		double Seconds = 0.0;
	};

	TArray<FPhase> Phases = { FPhase{ TEXT("default") } };
	int32 Current = 0;
	TArray<FTerrainMemorySample> Memory;
	int32 MemoryBaseline = 0;
	int32 AfterGcIndex = INDEX_NONE;
	int32 ResidueIndex = INDEX_NONE;
	int32 DelayedIndex = INDEX_NONE;
};

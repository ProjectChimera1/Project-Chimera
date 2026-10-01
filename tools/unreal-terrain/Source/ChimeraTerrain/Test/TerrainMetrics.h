// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.8 metrics: per-frame frame/GT/RT/GPU ms and per-tick brush timings, summarised into results.json.
#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"

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

class FTerrainMetrics
{
public:
	/** Sample the engine's per-frame timers (RenderTimer.h:8-17, RHIGetGPUFrameCycles DynamicRHI.h:1250). Call once per frame. */
	void SampleFrame(double DeltaSeconds);

	void AddTick(double ApplyMs, double UploadMs, double SplatMs);

	FTerrainSeries FrameMs;
	FTerrainSeries GameThreadMs;
	FTerrainSeries RenderThreadMs;
	FTerrainSeries GpuMs;
	FTerrainSeries TickApplyMs;
	FTerrainSeries TickUploadMs;
	FTerrainSeries TickSplatMs;
	FTerrainSeries TickTotalMs;

	TSharedRef<FJsonObject> ToJson() const;
};

// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Test/TerrainMetrics.h"

#include "DynamicRHI.h"
#include "HAL/PlatformTime.h"
#include "RenderTimer.h"

double FTerrainSeries::Percentile(double P) const
{
	if (Values.Num() == 0)
	{
		return 0.0;
	}
	TArray<double> Sorted = Values;
	Sorted.Sort();
	const int32 Rank = FMath::Clamp(FMath::CeilToInt(P / 100.0 * Sorted.Num()), 1, Sorted.Num());
	return Sorted[Rank - 1];
}

double FTerrainSeries::Max() const
{
	double M = 0.0;
	for (const double V : Values)
	{
		M = FMath::Max(M, V);
	}
	return M;
}

double FTerrainSeries::Mean() const
{
	if (Values.Num() == 0)
	{
		return 0.0;
	}
	double S = 0.0;
	for (const double V : Values)
	{
		S += V;
	}
	return S / Values.Num();
}

TSharedRef<FJsonObject> FTerrainSeries::ToJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetNumberField(TEXT("n"), Values.Num());
	O->SetNumberField(TEXT("p50"), Percentile(50.0));
	O->SetNumberField(TEXT("p99"), Percentile(99.0));
	O->SetNumberField(TEXT("max"), Max());
	O->SetNumberField(TEXT("mean"), Mean());
	return O;
}

void FTerrainMetrics::SampleFrame(double DeltaSeconds)
{
	FrameMs.Add(DeltaSeconds * 1000.0);
	GameThreadMs.Add(FPlatformTime::ToMilliseconds(GGameThreadTime));
	RenderThreadMs.Add(FPlatformTime::ToMilliseconds(GRenderThreadTime));
	GpuMs.Add(FPlatformTime::ToMilliseconds(RHIGetGPUFrameCycles(0)));
}

void FTerrainMetrics::AddTick(double ApplyMs, double UploadMs, double SplatMs)
{
	TickApplyMs.Add(ApplyMs);
	TickUploadMs.Add(UploadMs);
	TickSplatMs.Add(SplatMs);
	TickTotalMs.Add(ApplyMs + UploadMs + SplatMs);
}

TSharedRef<FJsonObject> FTerrainMetrics::ToJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetObjectField(TEXT("frame_ms"), FrameMs.ToJson());
	O->SetObjectField(TEXT("gt_ms"), GameThreadMs.ToJson());
	O->SetObjectField(TEXT("rt_ms"), RenderThreadMs.ToJson());
	O->SetObjectField(TEXT("gpu_ms"), GpuMs.ToJson());
	O->SetObjectField(TEXT("tick_apply_ms"), TickApplyMs.ToJson());
	O->SetObjectField(TEXT("tick_upload_ms"), TickUploadMs.ToJson());
	O->SetObjectField(TEXT("tick_splat_ms"), TickSplatMs.ToJson());
	O->SetObjectField(TEXT("tick_total_ms"), TickTotalMs.ToJson());
	return O;
}

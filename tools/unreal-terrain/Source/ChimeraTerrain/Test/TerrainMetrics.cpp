// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Test/TerrainMetrics.h"

#include "DynamicRHI.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformTime.h"
#include "PhysicsEngine/BodySetup.h"
#include "RealtimeMesh.h"
#include "RenderTimer.h"
#include "UObject/UObjectIterator.h"

using namespace ChimeraTerrain;

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
	O->SetNumberField(TEXT("p95"), Percentile(95.0));
	O->SetNumberField(TEXT("p99"), Percentile(99.0));
	O->SetNumberField(TEXT("max"), Max());
	O->SetNumberField(TEXT("mean"), Mean());
	return O;
}

void FTerrainMetrics::SetPhase(const FString& Name)
{
	int32 Idx = INDEX_NONE;
	for (int32 I = 0; I < Phases.Num(); ++I)
	{
		if (Phases[I].Name == Name)
		{
			Idx = I;
			break;
		}
	}
	if (Idx == INDEX_NONE)
	{
		FPhase P;
		P.Name = Name;
		Idx = Phases.Add(MoveTemp(P));
	}
	Current = Idx;
}

void FTerrainMetrics::SampleFrame(double DeltaSeconds)
{
	const double Frame = DeltaSeconds * 1000.0;
	const double Gt = FPlatformTime::ToMilliseconds(GGameThreadTime);
	const double Rt = FPlatformTime::ToMilliseconds(GRenderThreadTime);
	const double Gpu = FPlatformTime::ToMilliseconds(RHIGetGPUFrameCycles(0));
	FrameMs.Add(Frame);
	GameThreadMs.Add(Gt);
	RenderThreadMs.Add(Rt);
	GpuMs.Add(Gpu);
	FPhase& P = Phases[Current];
	P.Frame.Add(Frame);
	P.Gt.Add(Gt);
	P.Rt.Add(Rt);
	P.Gpu.Add(Gpu);
	P.Seconds += DeltaSeconds;
}

void FTerrainMetrics::SampleMemory(double TimeSeconds, bool bCountBodies, double UndoMB)
{
	const double T0 = FPlatformTime::Seconds();
	FTerrainMemorySample S;
	S.TimeSeconds = TimeSeconds;
	S.UndoMB = UndoMB;
	S.UsedPhysicalMB = static_cast<double>(FPlatformMemory::GetStats().UsedPhysical) / (1024.0 * 1024.0);
	if (bCountBodies)
	{
		int32 Total = 0;
		int32 Rmc = 0;
		for (TObjectIterator<UBodySetup> It; It; ++It)
		{
			if (It->HasAnyFlags(RF_ClassDefaultObject | RF_ArchetypeObject))
			{
				continue;
			}
			++Total;
			const UObject* Outer = It->GetOuter();
			if (Outer && Outer->IsA<URealtimeMesh>())
			{
				++Rmc;
			}
		}
		S.BodiesTotal = Total;
		S.BodiesRmc = Rmc;
	}
	S.SampleMs = (FPlatformTime::Seconds() - T0) * 1000.0;
	Memory.Add(S);
}

TSharedRef<FJsonObject> FTerrainMetrics::FramesToJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetObjectField(TEXT("frame_ms"), FrameMs.ToJson());
	O->SetObjectField(TEXT("gt_ms"), GameThreadMs.ToJson());
	O->SetObjectField(TEXT("rt_ms"), RenderThreadMs.ToJson());
	O->SetObjectField(TEXT("gpu_ms"), GpuMs.ToJson());
	TSharedRef<FJsonObject> Ph = MakeShared<FJsonObject>();
	for (const FPhase& P : Phases)
	{
		if (P.Frame.Num() == 0)
		{
			continue;
		}
		TSharedRef<FJsonObject> One = MakeShared<FJsonObject>();
		One->SetNumberField(TEXT("seconds"), P.Seconds);
		One->SetObjectField(TEXT("frame_ms"), P.Frame.ToJson());
		One->SetObjectField(TEXT("gt_ms"), P.Gt.ToJson());
		One->SetObjectField(TEXT("rt_ms"), P.Rt.ToJson());
		One->SetObjectField(TEXT("gpu_ms"), P.Gpu.ToJson());
		int32 Over33 = 0;
		int32 Over100 = 0;
		for (const double V : P.Frame.Values)
		{
			Over33 += V > 33.3 ? 1 : 0;
			Over100 += V > 100.0 ? 1 : 0;
		}
		One->SetNumberField(TEXT("frames_over_33ms"), Over33);
		One->SetNumberField(TEXT("frames_over_100ms"), Over100);
		Ph->SetObjectField(P.Name, One);
	}
	O->SetObjectField(TEXT("phases"), Ph);
	return O;
}

namespace
{
	struct FTickSeriesSet
	{
		FTerrainSeries Apply, Normals, Upload, Splat, Total, Verts;

		void Add(const FTerrainTickSample& S)
		{
			Apply.Add(S.Timing.ApplyMs);
			Normals.Add(S.Timing.NormalsMs);
			Upload.Add(S.Timing.UploadMs);
			Splat.Add(S.Timing.SplatMs);
			Total.Add(S.Timing.TotalMs());
			Verts.Add(S.UploadedVertices);
		}

		TSharedRef<FJsonObject> ToJson() const
		{
			TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
			O->SetNumberField(TEXT("n"), Total.Num());
			O->SetObjectField(TEXT("apply_ms"), Apply.ToJson());
			O->SetObjectField(TEXT("normals_ms"), Normals.ToJson());
			O->SetObjectField(TEXT("upload_ms"), Upload.ToJson());
			O->SetObjectField(TEXT("splat_ms"), Splat.ToJson());
			O->SetObjectField(TEXT("total_ms"), Total.ToJson());
			O->SetObjectField(TEXT("uploaded_vertices"), Verts.ToJson());
			return O;
		}
	};

	const TCHAR* ModeName(uint8 Mode)
	{
		static const TCHAR* const Names[] = { TEXT("raise"), TEXT("lower"), TEXT("smooth"), TEXT("flatten"), TEXT("paint") };
		return Mode < 5 ? Names[Mode] : TEXT("?");
	}
}

TSharedRef<FJsonObject> FTerrainMetrics::TicksToJson(const TArray<FTerrainTickSample>& Samples, const TArray<FString>& PhaseNames)
{
	FTickSeriesSet All;
	TMap<int32, FTickSeriesSet> ByD;
	TMap<uint8, FTickSeriesSet> ByMode;
	TMap<int32, FTickSeriesSet> ByPhase;
	for (const FTerrainTickSample& S : Samples)
	{
		All.Add(S);
		ByD.FindOrAdd(FMath::RoundToInt(S.DiameterM)).Add(S);
		ByMode.FindOrAdd(S.Mode).Add(S);
		ByPhase.FindOrAdd(S.PhaseIndex).Add(S);
	}
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetObjectField(TEXT("overall"), All.ToJson());
	TSharedRef<FJsonObject> D = MakeShared<FJsonObject>();
	for (const TPair<int32, FTickSeriesSet>& P : ByD)
	{
		D->SetObjectField(FString::FromInt(P.Key), P.Value.ToJson());
	}
	O->SetObjectField(TEXT("by_diameter"), D);
	TSharedRef<FJsonObject> M = MakeShared<FJsonObject>();
	for (const TPair<uint8, FTickSeriesSet>& P : ByMode)
	{
		M->SetObjectField(ModeName(P.Key), P.Value.ToJson());
	}
	O->SetObjectField(TEXT("by_mode"), M);
	TSharedRef<FJsonObject> Ph = MakeShared<FJsonObject>();
	for (const TPair<int32, FTickSeriesSet>& P : ByPhase)
	{
		Ph->SetObjectField(PhaseNames.IsValidIndex(P.Key) ? PhaseNames[P.Key] : FString::FromInt(P.Key), P.Value.ToJson());
	}
	O->SetObjectField(TEXT("by_phase"), Ph);
	return O;
}

FString FTerrainMetrics::TicksToCsv(const TArray<FTerrainTickSample>& Samples, const TArray<FString>& PhaseNames)
{
	FString Out = TEXT("index,frame,phase,mode,d,s,apply_ms,normals_ms,upload_ms,splat_ms,total_ms,vertices_changed,texels_changed,uploaded_vertices\n");
	for (const FTerrainTickSample& S : Samples)
	{
		Out += FString::Printf(TEXT("%lld,%llu,%s,%s,%.0f,%.0f,%.4f,%.4f,%.4f,%.4f,%.4f,%d,%d,%d\n"), S.Index, S.Frame,
			PhaseNames.IsValidIndex(S.PhaseIndex) ? *PhaseNames[S.PhaseIndex] : TEXT("?"), ModeName(S.Mode), S.DiameterM, S.Strength,
			S.Timing.ApplyMs, S.Timing.NormalsMs, S.Timing.UploadMs, S.Timing.SplatMs, S.Timing.TotalMs(), S.VerticesChanged, S.TexelsChanged,
			S.UploadedVertices);
	}
	return Out;
}

TSharedRef<FJsonObject> FTerrainMetrics::LatencyToJson(const TArray<FTerrainEditLatency>& Latencies)
{
	FTerrainSeries Frames;
	FTerrainSeries Ms;
	int32 Hist[5] = { 0, 0, 0, 0, 0 };
	for (const FTerrainEditLatency& L : Latencies)
	{
		Frames.Add(L.Frames);
		Ms.Add(L.Ms);
		++Hist[FMath::Clamp(L.Frames, 0, 4)];
	}
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetNumberField(TEXT("n"), Latencies.Num());
	O->SetObjectField(TEXT("frames"), Frames.ToJson());
	O->SetObjectField(TEXT("ms"), Ms.ToJson());
	TArray<TSharedPtr<FJsonValue>> H;
	for (int32 I = 0; I < 5; ++I)
	{
		H.Add(MakeShared<FJsonValueNumber>(Hist[I]));
	}
	O->SetArrayField(TEXT("frames_hist_0_to_4plus"), H);
	return O;
}

TSharedRef<FJsonObject> FTerrainMetrics::MemoryToJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetNumberField(TEXT("n"), Memory.Num());
	if (Memory.Num() == 0)
	{
		return O;
	}
	const int32 Base = FMath::Clamp(MemoryBaseline, 0, Memory.Num() - 1);
	// The gated window (C5 rework): baseline .. the after-GC sample when the soak marked one, else every sample. Samples after it (the
	// closing wait/verify, the delayed and residue samples) are reported only and never move peak, end or the undo-excluded figures.
	const bool bHasGc = Memory.IsValidIndex(AfterGcIndex) && AfterGcIndex >= Base;
	const int32 WindowEnd = bHasGc ? AfterGcIndex : Memory.Num() - 1;
	double Peak = 0.0;
	int32 PeakBodies = -1;
	int32 PeakRmc = -1;
	TArray<TSharedPtr<FJsonValue>> Arr;
	FTerrainSeries SampleMsAll;
	FTerrainSeries SampleMsBodies;
	int32 BodySamples = 0;
	for (int32 I = 0; I < Memory.Num(); ++I)
	{
		const FTerrainMemorySample& S = Memory[I];
		SampleMsAll.Add(S.SampleMs);
		if (S.BodiesTotal >= 0)
		{
			++BodySamples;
			SampleMsBodies.Add(S.SampleMs);
		}
		if (I >= Base && I <= WindowEnd)
		{
			Peak = FMath::Max(Peak, S.UsedPhysicalMB);
			PeakBodies = FMath::Max(PeakBodies, S.BodiesTotal);
			PeakRmc = FMath::Max(PeakRmc, S.BodiesRmc);
		}
		TSharedRef<FJsonObject> One = MakeShared<FJsonObject>();
		One->SetNumberField(TEXT("t"), S.TimeSeconds);
		One->SetNumberField(TEXT("mb"), S.UsedPhysicalMB);
		One->SetNumberField(TEXT("bodies"), S.BodiesTotal);
		One->SetNumberField(TEXT("bodies_rmc"), S.BodiesRmc);
		One->SetNumberField(TEXT("ms"), S.SampleMs);
		One->SetNumberField(TEXT("undo_mb"), S.UndoMB);
		Arr.Add(MakeShared<FJsonValueObject>(One));
	}
	const double Start = Memory[Base].UsedPhysicalMB;
	const double End = Memory[WindowEnd].UsedPhysicalMB;
	O->SetNumberField(TEXT("baseline_index"), Base);
	O->SetNumberField(TEXT("window_end_index"), WindowEnd);
	O->SetNumberField(TEXT("start_mb"), Start);
	O->SetNumberField(TEXT("end_mb"), End);
	O->SetNumberField(TEXT("peak_mb"), Peak);
	O->SetNumberField(TEXT("growth_peak_mb"), Peak - Start);
	O->SetNumberField(TEXT("growth_end_mb"), End - Start);
	O->SetNumberField(TEXT("peak_bodies_total"), PeakBodies);
	O->SetNumberField(TEXT("peak_bodies_rmc"), PeakRmc);
	O->SetNumberField(TEXT("end_bodies_total"), Memory[WindowEnd].BodiesTotal);
	O->SetNumberField(TEXT("end_bodies_rmc"), Memory[WindowEnd].BodiesRmc);
	O->SetNumberField(TEXT("body_samples"), BodySamples);
	// P6's after-GC figures: the sample the soak marked right after its blocking GC (-1 / absent when no GC sample was marked, which the
	// parser treats as missing data). Memory.Last() is not used: sampling continues every 2 s after the soak.
	O->SetNumberField(TEXT("after_gc_index"), AfterGcIndex);
	if (bHasGc)
	{
		const FTerrainMemorySample& G = Memory[AfterGcIndex];
		O->SetNumberField(TEXT("after_gc_mb"), G.UsedPhysicalMB);
		O->SetNumberField(TEXT("growth_after_gc_mb"), G.UsedPhysicalMB - Start);
		O->SetNumberField(TEXT("bodies_total_after_gc"), G.BodiesTotal);
		O->SetNumberField(TEXT("bodies_rmc_after_gc"), G.BodiesRmc);
		O->SetNumberField(TEXT("undo_mb_after_gc"), G.UndoMB);
	}
	// Reported (never gated): the first sample at least 12 s after the soak's GC, past UE Mimalloc's 10 s page-reset delay
	// (MallocMimalloc.cpp:33 GMiMallocMemoryResetDelay), with the undo history still held.
	O->SetNumberField(TEXT("delayed_index"), DelayedIndex);
	if (Memory.IsValidIndex(DelayedIndex) && DelayedIndex >= Base)
	{
		const FTerrainMemorySample& D = Memory[DelayedIndex];
		O->SetNumberField(TEXT("delayed_mb"), D.UsedPhysicalMB);
		O->SetNumberField(TEXT("growth_delayed_mb"), D.UsedPhysicalMB - Start);
		O->SetNumberField(TEXT("delayed_after_gc_s"), bHasGc ? D.TimeSeconds - Memory[AfterGcIndex].TimeSeconds : -1.0);
		O->SetNumberField(TEXT("bodies_rmc_delayed"), D.BodiesRmc);
		O->SetNumberField(TEXT("undo_mb_delayed"), D.UndoMB);
	}
	// Reported residue (never gated): after the gated samples, undo history cleared, a second blocking GC and FMemory::Trim.
	O->SetNumberField(TEXT("residue_index"), ResidueIndex);
	if (Memory.IsValidIndex(ResidueIndex) && ResidueIndex >= Base)
	{
		const FTerrainMemorySample& R = Memory[ResidueIndex];
		O->SetNumberField(TEXT("residue_mb"), R.UsedPhysicalMB);
		O->SetNumberField(TEXT("growth_residue_mb"), R.UsedPhysicalMB - Start);
		O->SetNumberField(TEXT("bodies_total_residue"), R.BodiesTotal);
		O->SetNumberField(TEXT("bodies_rmc_residue"), R.BodiesRmc);
		O->SetNumberField(TEXT("undo_mb_residue"), R.UndoMB);
		O->SetNumberField(TEXT("residue_after_gc_s"), bHasGc ? R.TimeSeconds - Memory[AfterGcIndex].TimeSeconds : -1.0);
	}
	// Undo history beside the growth (reported, never gated here): growth minus the history's own growth over the same span, inside the
	// gated window only (the residue sample clears the history, so including it would report the residue growth as "excluding undo").
	const double StartUndo = Memory[Base].UndoMB;
	const double EndUndo = Memory[WindowEnd].UndoMB;
	if (StartUndo >= 0.0 && EndUndo >= 0.0)
	{
		double PeakExcl = 0.0;
		for (int32 I = Base; I <= WindowEnd; ++I)
		{
			if (Memory[I].UndoMB >= 0.0)
			{
				PeakExcl = FMath::Max(PeakExcl, (Memory[I].UsedPhysicalMB - Start) - (Memory[I].UndoMB - StartUndo));
			}
		}
		O->SetNumberField(TEXT("start_undo_mb"), StartUndo);
		O->SetNumberField(TEXT("end_undo_mb"), EndUndo);
		O->SetNumberField(TEXT("growth_end_excl_undo_mb"), (End - Start) - (EndUndo - StartUndo));
		O->SetNumberField(TEXT("growth_peak_excl_undo_mb"), PeakExcl);
	}
	O->SetObjectField(TEXT("sample_ms"), SampleMsAll.ToJson());
	O->SetObjectField(TEXT("sample_ms_with_bodies"), SampleMsBodies.ToJson());
	O->SetArrayField(TEXT("samples"), Arr);
	return O;
}

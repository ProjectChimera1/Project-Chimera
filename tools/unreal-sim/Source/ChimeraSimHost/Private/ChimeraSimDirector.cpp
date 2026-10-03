// Copyright Chimera. See ChimeraSimDirector.h.
#include "ChimeraSimDirector.h"

#include "AssetCompilingManager.h"
#include "Camera/CameraActor.h"
#include "Camera/CameraComponent.h"
#include "ChimeraSimLibrary.h"
#include "ChimeraSimLog.h"
#include "ChimeraUnitRenderer.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Engine/World.h"
#include "GameFramework/PlayerController.h"
#include "HAL/IConsoleManager.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnrealClient.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformTime.h"
#include "Misc/App.h"
#include "Misc/EngineVersion.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "ProfilingDebugging/CsvProfiler.h"
#include "ShaderCompiler.h"

CSV_DEFINE_CATEGORY(ChimeraSim, true);

namespace
{
	const TCHAR* OrdersHeader = TEXT("tick,faction,unit_ref,cmd,x_raw,z_raw,slot");

	constexpr uint64 FnvOffset = 14695981039346656037ull;
	constexpr uint64 FnvPrime = 1099511628211ull;

	FString Hex16(uint64 V) { return FString::Printf(TEXT("0x%016llX"), (unsigned long long)V); }
	FString Hex8(uint32 V) { return FString::Printf(TEXT("0x%08X"), V); }

	double Percentile(TArray<double> Values, double P)
	{
		if (Values.Num() == 0) { return 0.0; }
		Values.Sort();
		const int32 Idx = FMath::Clamp((int32)FMath::CeilToDouble(P * Values.Num()) - 1, 0, Values.Num() - 1);
		return Values[Idx];
	}

	/** EXECUTION 2.2 compile-idle condition (B F17 null guard; plan A F29 counters). */
	bool CompileIdle(int32& OutShaderJobs, int32& OutAssets)
	{
		OutShaderJobs = GShaderCompilingManager != nullptr ? GShaderCompilingManager->GetNumRemainingJobs() : 0;
		OutAssets = FAssetCompilingManager::Get().GetNumRemainingAssets();
		return OutShaderJobs == 0 && OutAssets == 0;
	}

	const TCHAR* UeConfigName()
	{
#if UE_BUILD_DEBUG
		return TEXT("Debug");
#elif UE_BUILD_DEVELOPMENT
		return TEXT("Development");
#elif UE_BUILD_TEST
		return TEXT("Test");
#elif UE_BUILD_SHIPPING
		return TEXT("Shipping");
#else
		return TEXT("Unknown");
#endif
	}
}

AChimeraSimDirector::AChimeraSimDirector()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.bStartWithTickEnabled = true;
	PrimaryActorTick.TickGroup = TG_PrePhysics;
}

void AChimeraSimDirector::BeginPlay()
{
	Super::BeginPlay();
	SetupTime = FPlatformTime::Seconds();
	FString RenderErr;
	if (Setup() && SetupRenderAndShots(RenderErr))
	{
		State = EState::Warmup;
		UE_LOG(LogChimeraSim, Display, TEXT("warmup: holding tick 0 until shader and asset compiles are idle for %d frames, then %.0f s settle (cap %.0f s)"),
			IdleFramesNeeded, SettleSeconds, Options.WarmupMaxSec);
	}
}

void AChimeraSimDirector::EndPlay(const EEndPlayReason::Type Reason)
{
	if (ShotDelegate.IsValid())
	{
		FScreenshotRequest::OnScreenshotRequestProcessed().Remove(ShotDelegate);
		ShotDelegate.Reset();
	}
	if (bTemporalFrozen)
	{
		SetTemporalFreeze(false);
	}
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	if (Session > 0 && Lib.IsLoaded())
	{
		Lib.Call(Lib.Api().SessionDestroy, Session);
		Session = 0;
	}
	Super::EndPlay(Reason);
}

bool AChimeraSimDirector::Setup()
{
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	FString Err;
	if (!Lib.Load(Err))
	{
		Fail(3, FString::Printf(TEXT("library: %s"), *Err));
		return false;
	}
	const FChimeraSimApi& Api = Lib.Api();

	DigestTicks = { 0, 300, 900, 1440 };
	DigestTicks.RemoveAll([this](int32 T) { return T > Options.Ticks; });

	if (!LoadOrders(Err))
	{
		Fail(8, FString::Printf(TEXT("orders: %s"), *Err));
		return false;
	}

	// Content and file hashes come from the DLL, never from UE (F27: no Windows SHA-256 in UE).
	int32 ShaRc = 0;
	ScenarioSha = Lib.FileSha256(Options.Scenario, &ShaRc);
	OrdersSha = Lib.FileSha256(Options.Orders);
	DllSha = Lib.FileSha256(Lib.GetDllPath());
	UeModulePath = FPaths::ConvertRelativePathToFull(FModuleManager::Get().GetModuleFilename(TEXT("ChimeraSimHost")));
	UeModuleSha = Lib.FileSha256(UeModulePath);
	UE_LOG(LogChimeraSim, Display, TEXT("sha256 scenario=%s orders=%s dll=%s ue_module=%s (%s)"),
		ScenarioSha.IsEmpty() ? TEXT("?") : *ScenarioSha, *OrdersSha, *DllSha, UeModuleSha.IsEmpty() ? TEXT("?") : *UeModuleSha, *UeModulePath);

	const uint32 Flags = Options.bAi ? CHIMERA_FLAG_AI : 0u;
	FTCHARToUTF8 Root(*Options.ContentRoot);
	FTCHARToUTF8 Scn(*Options.Scenario);
	int32 Id = 0;
	const double T0 = FPlatformTime::Seconds();
	const int32 Rc = Lib.Call(Api.SessionCreate, (const char*)Root.Get(), (const char*)Scn.Get(), Options.Seed, Flags, &Id);
	const double CreateMs = (FPlatformTime::Seconds() - T0) * 1000.0;
	if (Rc != CHIMERA_OK)
	{
		Fail(5, FString::Printf(TEXT("session_create rc=%d (%s) after %.0f ms; chimera_last_error: %s"), Rc,
			Rc == CHIMERA_E_CONTENT ? TEXT("content load failed") : Rc == CHIMERA_E_SCENARIO ? TEXT("scenario rejected") : TEXT("other"),
			CreateMs, *Lib.LastError(0)));
		return false;
	}
	Session = Id;
	UE_LOG(LogChimeraSim, Display, TEXT("session_create rc=0 id=%d ai=%d in %.0f ms"), Session, Options.bAi ? 1 : 0, CreateMs);

	int32 IvRc = Lib.Call(Api.SetChecksumInterval, Session, 1);
	int32 PreRc = Lib.Call(Api.PreTickHashes, Session, Pre);
	if (IvRc != CHIMERA_OK || PreRc != CHIMERA_OK)
	{
		Fail(5, FString::Printf(TEXT("set_checksum_interval rc=%d pre_tick_hashes rc=%d: %s"), IvRc, PreRc, *Lib.LastError(Session)));
		return false;
	}
	UnitsAtStart = AliveNow();
	int32 Rows0 = 0;
	Lib.Call(Api.ReadUnits, Session, (ChimeraUnit*)nullptr, 0, &Rows0); // size query: -5 with the row count
	UE_LOG(LogChimeraSim, Display, TEXT("pretick start_state=%s canonical_model=%s content=%s ruleset=%s agreement=%s tick0=%s units_at_start=%lld rows=%d"),
		*Hex16(Pre[0]), *Hex16(Pre[1]), *Hex16(Pre[2]), *Hex16(Pre[3]), *Hex16(Pre[4]), *Hex8((uint32)Pre[5]), UnitsAtStart, Rows0);

	Hashes.Reset(Options.Ticks);
	RecordDigest(0);
	return true;
}

bool AChimeraSimDirector::LoadOrders(FString& OutError)
{
	FString Text;
	if (!FFileHelper::LoadFileToString(Text, *Options.Orders))
	{
		OutError = FString::Printf(TEXT("cannot read %s"), *Options.Orders);
		return false;
	}
	TArray<FString> Lines;
	Text.ParseIntoArray(Lines, TEXT("\n"), /*InCullEmpty*/ false);
	bool bHeader = false;
	Rows.Reset();
	for (int32 Li = 0; Li < Lines.Num(); ++Li)
	{
		FString Line = Lines[Li];
		Line.TrimEndInline();
		if (Line.TrimStart().IsEmpty()) { continue; }
		if (!bHeader)
		{
			if (Line.TrimStartAndEnd() != OrdersHeader)
			{
				OutError = FString::Printf(TEXT("line %d: expected header '%s', got '%s'"), Li + 1, OrdersHeader, *Line);
				return false;
			}
			bHeader = true;
			continue;
		}
		TArray<FString> F;
		Line.ParseIntoArray(F, TEXT(","), /*InCullEmpty*/ false);
		if (F.Num() != 7)
		{
			OutError = FString::Printf(TEXT("line %d: expected 7 columns, got %d"), Li + 1, F.Num());
			return false;
		}
		FChimeraOrderRow R;
		for (int32 c = 0; c < 7; ++c)
		{
			const FString Cell = F[c].TrimStartAndEnd();
			TCHAR* End = nullptr;
			const int64 V = FCString::Strtoi64(*Cell, &End, 10);
			if (Cell.IsEmpty() || End == nullptr || *End != 0 || V < MIN_int32 || V > MAX_int32)
			{
				OutError = FString::Printf(TEXT("line %d column %d: '%s' is not an int32"), Li + 1, c + 1, *Cell);
				return false;
			}
			R.V[c] = (int32)V;
		}
		if (R.Tick() < 0 || (Rows.Num() > 0 && R.Tick() < Rows.Last().Tick()))
		{
			OutError = FString::Printf(TEXT("line %d: tick %d out of order"), Li + 1, R.Tick());
			return false;
		}
		Rows.Add(R);
	}
	if (!bHeader)
	{
		OutError = TEXT("empty order script");
		return false;
	}
	UE_LOG(LogChimeraSim, Display, TEXT("orders: %d rows from %s, orders_digest=%s"), Rows.Num(), *Options.Orders, *Hex16(OrdersDigest(Rows)));
	return true;
}

uint64 AChimeraSimDirector::OrdersDigest(const TArray<FChimeraOrderRow>& InRows)
{
	// FNV-1a 64 over every row in submit order, each field as little-endian int32 (OrderScript.Digest, compare_traces.py).
	uint64 H = FnvOffset;
	for (const FChimeraOrderRow& R : InRows)
	{
		for (int32 c = 0; c < 7; ++c)
		{
			const uint32 U = (uint32)R.V[c];
			for (int32 b = 0; b < 4; ++b)
			{
				H ^= (uint64)((U >> (8 * b)) & 0xFFu);
				H *= FnvPrime;
			}
		}
	}
	return H;
}

int64 AChimeraSimDirector::AliveNow() const
{
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	int64 S[CHIMERA_STATS_COUNT] = {};
	if (Session <= 0 || Lib.Call(Lib.Api().Stats, Session, S) != CHIMERA_OK) { return -1; }
	return S[6];
}

void AChimeraSimDirector::RecordDigest(int32 T)
{
	if (!DigestTicks.Contains(T)) { return; }
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	uint64 U = 0, W = 0;
	const int32 R1 = Lib.Call(Lib.Api().UnitsDigest, Session, &U);
	const int32 R2 = Lib.Call(Lib.Api().WideDigest, Session, &W);
	if (R1 != CHIMERA_OK || R2 != CHIMERA_OK)
	{
		Fail(6, FString::Printf(TEXT("digest at tick %d rc=%d/%d: %s"), T, R1, R2, *Lib.LastError(Session)));
		return;
	}
	Digests.Add(T, TPair<uint64, uint64>(U, W));
	UE_LOG(LogChimeraSim, Display, TEXT("digest.%d=%s wide.%d=%s"), T, *Hex16(U), T, *Hex16(W));
}

void AChimeraSimDirector::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	switch (State)
	{
	case EState::Warmup:
	case EState::Settle:
		TickWarmup();
		break;
	case EState::Run:
		// Real seconds of the last frame (R3 5.2: FApp's delta, not world-dilated time).
		TickRun(FApp::GetDeltaTime());
		break;
	default:
		break;
	}
}

void AChimeraSimDirector::TickWarmup()
{
	// The units are drawn (at tick 0) from the first frame, so their materials compile inside the warm-up wait.
	UpdateRenderer(1.0);
	if (State == EState::Failed)
	{
		return;
	}
	const double Now = FPlatformTime::Seconds();
	const double Waited = Now - SetupTime;
	if (Waited > Options.WarmupMaxSec)
	{
		int32 Jobs = 0, Assets = 0;
		CompileIdle(Jobs, Assets);
		Fail(4, FString::Printf(TEXT("warm-up exceeded %.0f s (shader jobs %d, assets %d, idle frames %d)"), Options.WarmupMaxSec, Jobs, Assets, IdleFrames));
		return;
	}
	if (State == EState::Warmup)
	{
		int32 Jobs = 0, Assets = 0;
		IdleFrames = CompileIdle(Jobs, Assets) ? IdleFrames + 1 : 0;
		if (IdleFrames >= IdleFramesNeeded)
		{
			State = EState::Settle;
			SettleStart = Now;
			UE_LOG(LogChimeraSim, Display, TEXT("warmup: compiles idle for %d frames after %.1f s; settling %.0f s"), IdleFrames, Waited, SettleSeconds);
		}
		return;
	}
	if (Now - SettleStart >= SettleSeconds)
	{
		WarmupWaitS = Now - SetupTime;
		State = EState::Run;
		RunStartTime = Now;
		Acc = 0.0;
		UE_LOG(LogChimeraSim, Display, TEXT("warmup_wait_s=%.1f; running %d ticks at 30 Hz (max %d steps per frame, clamp %.2f s)"),
			WarmupWaitS, Options.Ticks, MaxStepsPerFrame, MaxFrameDelta);
	}
}

void AChimeraSimDirector::TickRun(double Dt)
{
	// A hold in progress: no stepping, alpha 1, the hold's actions advance one frame (plan A 3.7 "Shots").
	if (Hold.bActive)
	{
		++Hold.PhaseFrames;
		UpdateRenderer(1.0);
		if (State == EState::Failed) { return; }
		TickHold();
		if (State == EState::Failed) { return; }
		RecordHoldFrame(Dt);
		if (!Hold.bActive && SimTick >= Options.Ticks)
		{
			Finish();
		}
		return;
	}
	// Tick 0 holds before any step (later hold ticks are entered at the end of the frame whose loop reached them).
	if (IsHoldTick(SimTick))
	{
		Alpha = 1.0;
		UpdateRenderer(1.0);
		if (State == EState::Failed) { return; }
		BeginHold();
		if (State == EState::Failed) { return; }
		RecordHoldFrame(Dt);
		if (!Hold.bActive && SimTick >= Options.Ticks)
		{
			Finish();
		}
		return;
	}

	// Plan A 3.7 "Loop": the accumulator is wall-clock pacing only; the sim sees one chimera_step per tick, nothing else.
	Acc += FMath::Min(Dt, MaxFrameDelta);
	int32 Steps = 0;
	bool bReachedHold = false;
	const double FrameT0 = FPlatformTime::Seconds();
	while (Acc >= TickSeconds && SimTick < Options.Ticks && Steps < MaxStepsPerFrame)
	{
		if (!StepOnce()) { return; }
		Acc -= TickSeconds;
		++Steps;
		if (IsHoldTick(SimTick))
		{
			bReachedHold = true; // stop here: the renderer shows exactly this tick (alpha 1) for the verify and the shots
			break;
		}
	}
	const double SimMs = (FPlatformTime::Seconds() - FrameT0) * 1000.0;
	Alpha = bReachedHold ? 1.0 : FMath::Clamp(Acc / TickSeconds, 0.0, 1.0);
	MaxStepsSeen = FMath::Max(MaxStepsSeen, Steps);
	UpdateRenderer(Alpha);
	if (State == EState::Failed) { return; }

	FChimeraFrameRow Row;
	Row.Frame = RunFrame;
	Row.DtMs = Dt * 1000.0;
	Row.Steps = Steps;
	Row.SimMs = SimMs;
	Row.Tick = SimTick;
	Row.Alive = AliveNow();
	Row.Alpha = Alpha;
	CSV_CUSTOM_STAT(ChimeraSim, SimMs, (float)SimMs, ECsvCustomStatOp::Set);
	CSV_CUSTOM_STAT(ChimeraSim, TicksStepped, Steps, ECsvCustomStatOp::Set);
	CSV_CUSTOM_STAT(ChimeraSim, Alive, (int32)Row.Alive, ECsvCustomStatOp::Set);

	++RunFrame;
	if (bReachedHold)
	{
		Frames.Add(Row);
		BeginHold();
		if (State == EState::Failed) { return; }
		if (!Hold.bActive && SimTick >= Options.Ticks)
		{
			Finish();
		}
		return;
	}
	if (SimTick >= Options.Ticks)
	{
		Frames.Add(Row);
		Finish();
		return;
	}
	// -ChimeraSimHitchMs/-ChimeraSimHitchEvery: a forced long frame, so the next delta hits the clamp and catch-up.
	if (Options.HitchMs > 0 && Options.HitchEvery > 0 && RunFrame % Options.HitchEvery == 0)
	{
		Row.bHitch = true;
		++Hitches;
		FPlatformProcess::Sleep((float)Options.HitchMs / 1000.0f);
	}
	Frames.Add(Row);
}

void AChimeraSimDirector::RecordHoldFrame(double Dt)
{
	FChimeraFrameRow Row;
	Row.Frame = RunFrame++;
	Row.DtMs = Dt * 1000.0;
	Row.Tick = SimTick;
	Row.Alive = AliveNow();
	Row.Alpha = 1.0;
	Row.bHold = true;
	Frames.Add(Row);
}

bool AChimeraSimDirector::StepOnce()
{
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	const FChimeraSimApi& Api = Lib.Api();
	const double T0 = FPlatformTime::Seconds();

	// Orders whose tick == the current tick, in file order, then exactly one step (identical in every leg).
	while (Cursor < Rows.Num() && Rows[Cursor].Tick() <= SimTick)
	{
		const FChimeraOrderRow& R = Rows[Cursor];
		if (R.Tick() < SimTick)
		{
			Fail(6, FString::Printf(TEXT("order row %d is for tick %d but the session is at tick %d"), Cursor, R.Tick(), SimTick));
			return false;
		}
		const int32 Rc = Lib.Call(Api.SubmitOrder, Session, R.V[1], R.V[2], R.V[3], R.V[4], R.V[5], R.V[6]);
		if (Rc == CHIMERA_ORDER_APPLIED) { ++Applied; }
		else if (Rc == CHIMERA_ORDER_DROPPED) { ++Dropped; }
		else
		{
			Fail(6, FString::Printf(TEXT("submit_order row %d rc=%d: %s"), Cursor, Rc, *Lib.LastError(Session)));
			return false;
		}
		++Cursor;
	}
	const int32 StepRc = Lib.Call(Api.Step, Session);
	if (StepRc != CHIMERA_OK)
	{
		Fail(6, FString::Printf(TEXT("step to tick %d rc=%d: %s"), SimTick + 1, StepRc, *Lib.LastError(Session)));
		return false;
	}
	uint32 T = 0, H = 0;
	const int32 CkRc = Lib.Call(Api.LastChecksum, Session, &T, &H);
	if (CkRc != CHIMERA_OK || T != (uint32)(SimTick + 1))
	{
		Fail(6, FString::Printf(TEXT("last_checksum rc=%d tick=%u, expected tick %d"), CkRc, T, SimTick + 1));
		return false;
	}
	++SimTick;
	Hashes.Add(H);
	StepMs.Add((FPlatformTime::Seconds() - T0) * 1000.0);
	if (SimTick % 60 == 0)
	{
		UE_LOG(LogChimeraSim, Display, TEXT("tick=%d hash=%s"), SimTick, *Hex8(H));
	}
	RecordDigest(SimTick);
	return State != EState::Failed;
}

void AChimeraSimDirector::Finish()
{
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	AliveEnd = AliveNow();
	int32 V = -1;
	if (Lib.Call(Lib.Api().Verdict, Session, &V) == CHIMERA_OK) { Verdict = V; }
	State = EState::Done;

	FString Err;
	if (!WriteOutputs(true, Err))
	{
		Fail(7, Err);
		return;
	}
	TArray<double> Dts;
	int32 HoldFrames = 0;
	for (const FChimeraFrameRow& F : Frames)
	{
		if (F.bHold) { ++HoldFrames; continue; } // shot and verify holds are not paced frames
		Dts.Add(F.DtMs);
	}
	const double MedianDt = Percentile(Dts, 0.5);
	const double FpsMedian = MedianDt > 0.0 ? 1000.0 / MedianDt : 0.0;
	const double SimP95 = Percentile(StepMs, 0.95);
	const double SimMax = Percentile(StepMs, 1.0);
	UE_LOG(LogChimeraSim, Display, TEXT("RESULT ticks=%d final=%s fps_median=%.1f sim_p95_ms=%.3f sim_max_ms=%.3f max_steps_per_frame=%d frames=%d hitches=%d orders_applied=%d orders_dropped=%d alive_end=%lld verdict=%d mxcsr_nondefault=%lld abi_calls=%lld warmup_wait_s=%.1f run_s=%.1f hold_frames=%d verify=%d/%d shots=%d film=%d"),
		SimTick, Hashes.Num() > 0 ? *Hex8(Hashes.Last()) : TEXT("?"), FpsMedian, SimP95, SimMax, MaxStepsSeen, Frames.Num(), Hitches, Applied, Dropped,
		AliveEnd, Verdict, Lib.GetMxcsrNonDefault(), Lib.GetCalls(), WarmupWaitS, FPlatformTime::Seconds() - RunStartTime, HoldFrames, VerifyPassed,
		VerifyRecords.Num(), ShotsWritten, FilmIndex);
	if (Options.bExitWhenDone)
	{
		GLog->Flush();
		// Success may exit non-forced (EXECUTION 2.2); the wrapper also requires the RESULT sentinel.
		FPlatformMisc::RequestExitWithStatus(false, 0);
	}
}

void AChimeraSimDirector::Fail(uint8 Code, const FString& Message)
{
	State = EState::Failed;
	UE_LOG(LogChimeraSim, Error, TEXT("%s (exit %d)"), *Message, (int32)Code);
	FString Err;
	if (!Options.OutDir.IsEmpty() && !WriteOutputs(false, Err))
	{
		UE_LOG(LogChimeraSim, Error, TEXT("%s"), *Err);
	}
	if (Options.bExitWhenDone)
	{
		// Files are closed (SaveStringToFile returns after closing); forced, so the code reaches the process (EXECUTION 2.2, C3).
		GLog->Flush();
		FPlatformMisc::RequestExitWithStatus(true, Code);
	}
}

FString AChimeraSimDirector::BuildTrace(bool bComplete) const
{
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	FString S;
	S.Reserve(Hashes.Num() * 14 + 4096);
	auto H = [&S](const TCHAR* K, const FString& V) { S += FString::Printf(TEXT("# %s: %s\n"), K, *V); };
	H(TEXT("leg"), Options.Leg);
	H(TEXT("host"), FString::Printf(TEXT("unreal %s -game"), *FEngineVersion::Current().ToString()));
	H(TEXT("runtime"), Lib.GetBuildField(TEXT("runtime")));
	H(TEXT("config"), TEXT("Release"));
	H(TEXT("commit"), Lib.GetBuildField(TEXT("commit")));
	H(TEXT("dirty"), Lib.GetBuildField(TEXT("dirty")));
	H(TEXT("algo"), Lib.GetBuildField(TEXT("algo")));
	H(TEXT("scenario_sha256"), ScenarioSha);
	H(TEXT("orders_sha256"), OrdersSha);
	H(TEXT("seed"), Options.SeedHex());
	H(TEXT("ai"), Options.bAi ? TEXT("1") : TEXT("0"));
	H(TEXT("hash.start_state"), Hex16(Pre[0]));
	H(TEXT("hash.canonical_model"), Hex16(Pre[1]));
	H(TEXT("hash.content"), Hex16(Pre[2]));
	H(TEXT("hash.ruleset"), Hex16(Pre[3]));
	H(TEXT("hash.agreement"), Hex16(Pre[4]));
	H(TEXT("hash.tick0"), Hex8((uint32)Pre[5]));
	H(TEXT("units_at_start"), FString::Printf(TEXT("%lld"), UnitsAtStart));
	H(TEXT("dll_sha256"), DllSha);
	H(TEXT("dll_path"), Lib.GetDllPath());
	H(TEXT("build_info"), Lib.GetBuildInfo());
	H(TEXT("p_commit"), Options.PCommit);
	H(TEXT("p_dirty"), Options.PDirty);
	H(TEXT("ue_module_sha256"), UeModuleSha);
	H(TEXT("ue_module"), UeModulePath);
	H(TEXT("ue_config"), UeConfigName());
	H(TEXT("shots"), Options.ShotList());
	H(TEXT("max_steps_per_frame"), FString::Printf(TEXT("%d"), MaxStepsSeen));
	H(TEXT("hitch"), FString::Printf(TEXT("%dms_every_%d_frames count=%d"), Options.HitchMs, Options.HitchEvery, Hitches));
	H(TEXT("mxcsr_nondefault"), FString::Printf(TEXT("%lld"), Lib.GetMxcsrNonDefault()));
	H(TEXT("abi_calls"), FString::Printf(TEXT("%lld"), Lib.GetCalls()));
	H(TEXT("warmup_wait_s"), FString::Printf(TEXT("%.1f"), WarmupWaitS));
	if (!bComplete)
	{
		H(TEXT("incomplete"), FString::Printf(TEXT("stopped at tick %d"), SimTick));
	}
	for (int32 i = 0; i < Hashes.Num(); ++i)
	{
		S += FString::Printf(TEXT("%d %08X\n"), i + 1, Hashes[i]);
	}
	TArray<int32> Keys;
	Digests.GetKeys(Keys);
	Keys.Sort();
	for (int32 T : Keys)
	{
		const TPair<uint64, uint64>& D = Digests[T];
		H(*FString::Printf(TEXT("digest.%d"), T), Hex16(D.Key));
		H(*FString::Printf(TEXT("wide.%d"), T), Hex16(D.Value));
	}
	H(TEXT("orders_digest"), Hex16(OrdersDigest(Rows)));
	H(TEXT("orders_applied"), FString::Printf(TEXT("%d"), Applied));
	H(TEXT("orders_dropped"), FString::Printf(TEXT("%d"), Dropped));
	H(TEXT("alive_end"), FString::Printf(TEXT("%lld"), AliveEnd));
	H(TEXT("verdict"), FString::Printf(TEXT("%d"), Verdict));
	return S;
}

bool AChimeraSimDirector::WriteOutputs(bool bComplete, FString& OutError) const
{
	IFileManager::Get().MakeDirectory(*Options.OutDir, /*Tree*/ true);
	// A failed run never leaves a trace.txt that could be mistaken for a complete one.
	const FString TracePath = Options.OutDir / (bComplete ? TEXT("trace.txt") : TEXT("trace.partial.txt"));
	if (!FFileHelper::SaveStringToFile(BuildTrace(bComplete), *TracePath, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
	{
		OutError = FString::Printf(TEXT("cannot write %s"), *TracePath);
		return false;
	}
	FString Csv = TEXT("frame,dt_ms,steps,sim_ms,tick,alive,alpha,hitch,hold\n");
	Csv.Reserve(Frames.Num() * 50 + 64);
	for (const FChimeraFrameRow& F : Frames)
	{
		Csv += FString::Printf(TEXT("%d,%.3f,%d,%.3f,%d,%lld,%.4f,%d,%d\n"), F.Frame, F.DtMs, F.Steps, F.SimMs, F.Tick, F.Alive, F.Alpha, F.bHitch ? 1 : 0,
			F.bHold ? 1 : 0);
	}
	const FString FramesPath = Options.OutDir / TEXT("frames.csv");
	if (!FFileHelper::SaveStringToFile(Csv, *FramesPath, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
	{
		OutError = FString::Printf(TEXT("cannot write %s"), *FramesPath);
		return false;
	}
	// verify.json (plan A 3.7 "Verify"): every verify and shot of the run, complete or not.
	FString VerifyText;
	{
		TSharedRef<TJsonWriter<>> W = TJsonWriterFactory<>::Create(&VerifyText);
		FJsonSerializer::Serialize(BuildVerifyJson(), W);
	}
	const FString VerifyPath = Options.OutDir / TEXT("verify.json");
	if (!FFileHelper::SaveStringToFile(VerifyText, *VerifyPath, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
	{
		OutError = FString::Printf(TEXT("cannot write %s"), *VerifyPath);
		return false;
	}
	if (Options.FilmEvery > 0)
	{
		const FString FilmPath = Options.OutDir / TEXT("film") / TEXT("film_ticks.txt");
		if (!FFileHelper::SaveStringToFile(FString::Join(FilmLines, TEXT("\n")) + TEXT("\n"), *FilmPath, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
		{
			OutError = FString::Printf(TEXT("cannot write %s"), *FilmPath);
			return false;
		}
	}
	UE_LOG(LogChimeraSim, Display, TEXT("wrote %s (%d ticks), %s (%d frames) and %s (%d verifies, %d shots)"), *TracePath, Hashes.Num(), *FramesPath,
		Frames.Num(), *VerifyPath, VerifyRecords.Num(), ShotsWritten);
	return true;
}

// ---------------------------------------------------------------------------------------------------------------------------------
// A11: renderer, cameras, holds, shot pairs, film frames, verify (plan A 3.7 "Rendering", "Shots", "Verify").

APlayerController* AChimeraSimDirector::GetPC() const
{
	return GetWorld() != nullptr ? GetWorld()->GetFirstPlayerController() : nullptr;
}

ACameraActor* AChimeraSimDirector::SpawnShotCamera(const FString& Name, double DistM, double PitchDeg, float HFovDeg)
{
	// Pivot (0,0,0), behind it along -Y and above it, yaw 90 (world -X = screen right, plan A 3.7 / F31), as the game mode's overview.
	const FVector Loc(0.0, -DistM * FMath::Cos(FMath::DegreesToRadians(PitchDeg)) * 100.0, DistM * FMath::Sin(FMath::DegreesToRadians(PitchDeg)) * 100.0);
	const FRotator Rot(-PitchDeg, 90.0, 0.0);
	FActorSpawnParameters Params;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	ACameraActor* Cam = GetWorld()->SpawnActor<ACameraActor>(ACameraActor::StaticClass(), Loc, Rot, Params);
	if (Cam != nullptr)
	{
		Cam->GetCameraComponent()->SetFieldOfView(HFovDeg); // UE's FieldOfView is horizontal
		Cam->GetCameraComponent()->SetConstraintAspectRatio(false);
		UE_LOG(LogChimeraSim, Display, TEXT("camera: %s at (%.0f, %.0f, %.0f) cm pitch -%.0f yaw 90 hfov %.0f (shots only)"), *Name, Loc.X, Loc.Y, Loc.Z, PitchDeg, HFovDeg);
	}
	return Cam;
}

ACameraActor* AChimeraSimDirector::FindCamera(const FString& Name) const
{
	const int32 Idx = ShotCameraNames.IndexOfByKey(Name);
	return Idx != INDEX_NONE ? ShotCameras[Idx].Get() : nullptr;
}

bool AChimeraSimDirector::SetupRenderAndShots(FString& OutError)
{
	// Verify at every digest tick (0/300/900/1440) and every shot tick (plan A 4 A11: 0/60/300/900/1440 with the default shots).
	for (int32 T : DigestTicks) { VerifyTicks.Add(T); }
	for (const FChimeraSimShot& S : Options.Shots) { VerifyTicks.Add(S.Tick); }
	if (Options.VerifyEvery > 0)
	{
		for (int32 T = Options.VerifyEvery; T <= Options.Ticks; T += Options.VerifyEvery) { VerifyTicks.Add(T); } // diagnostic runs (no shots)
	}

	FActorSpawnParameters Params;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	Renderer = GetWorld()->SpawnActor<AChimeraUnitRenderer>(AChimeraUnitRenderer::StaticClass(), FTransform::Identity, Params);
	if (Renderer == nullptr)
	{
		Fail(8, TEXT("renderer: spawn failed"));
		return false;
	}
	FString Err;
	if (!Renderer->Init(Session, Options.MeshesPath, Options.bHideUnits, Options.bDeadUnderGround, Err))
	{
		Fail(8, FString::Printf(TEXT("renderer: %s"), *Err));
		return false;
	}
	UE_LOG(LogChimeraSim, Display, TEXT("renderer: %d groups, drawn %d: %s"), Renderer->GetGroups().Num(), Renderer->GetDrawnLastFrame(), *Renderer->Describe());

	// Shot cameras of plan A 3.7: overview 150 m pitch 55 hFOV 90, wide 90 m pitch 55 hFOV 90, close 30 m pitch 40 hFOV 60.
	ShotCameraNames = { TEXT("overview"), TEXT("wide"), TEXT("close") };
	ShotCameras.Reset();
	ShotCameras.Add(OverviewCamera != nullptr ? OverviewCamera.Get() : SpawnShotCamera(TEXT("overview"), 150.0, 55.0, 90.0f));
	ShotCameras.Add(SpawnShotCamera(TEXT("wide"), 90.0, 55.0, 90.0f));
	ShotCameras.Add(SpawnShotCamera(TEXT("close"), 30.0, 40.0, 60.0f));
	for (const FChimeraSimShot& S : Options.Shots)
	{
		if (FindCamera(S.Camera) == nullptr)
		{
			Fail(8, FString::Printf(TEXT("shots: unknown camera '%s' (overview, wide, close)"), *S.Camera));
			return false;
		}
	}
	ShotDelegate = FScreenshotRequest::OnScreenshotRequestProcessed().AddUObject(this, &AChimeraSimDirector::OnScreenshotProcessed);
	TArray<int32> Vt = VerifyTicks.Array();
	Vt.Sort();
	TArray<FString> VtS;
	for (int32 T : Vt) { VtS.Add(FString::FromInt(T)); }
	UE_LOG(LogChimeraSim, Display, TEXT("verify ticks: %s; shots: %d; film every %d ticks; shot freeze %d"), *FString::Join(VtS, TEXT(",")), Options.Shots.Num(),
		Options.FilmEvery, Options.bShotFreeze ? 1 : 0);
	return true;
}

void AChimeraSimDirector::UpdateRenderer(double InAlpha)
{
	if (Renderer == nullptr)
	{
		return;
	}
	FString Err;
	if (!Renderer->Update(InAlpha, Err))
	{
		Fail(6, FString::Printf(TEXT("renderer: %s"), *Err));
	}
}

bool AChimeraSimDirector::IsHoldTick(int32 T) const
{
	if (HoldsDone.Contains(T))
	{
		return false;
	}
	return VerifyTicks.Contains(T) || (Options.FilmEvery > 0 && T % Options.FilmEvery == 0 && T <= Options.Ticks);
}

void AChimeraSimDirector::BeginHold()
{
	Hold = FHold();
	Hold.bActive = true;
	Hold.Tick = SimTick;
	HoldsDone.Add(SimTick);
	for (int32 i = 0; i < Options.Shots.Num(); ++i)
	{
		if (Options.Shots[i].Tick == SimTick)
		{
			Hold.ShotIdx.Add(i);
		}
	}
	Hold.bFilm = Options.FilmEvery > 0 && SimTick % Options.FilmEvery == 0;
	Hold.bVisual = Hold.ShotIdx.Num() > 0 || Hold.bFilm;
	bHaveCurrentVerify = false;
	if (VerifyTicks.Contains(SimTick) && !DoVerify())
	{
		return;
	}
	if (Hold.ShotIdx.Num() > 0 || Hold.bFilm)
	{
		if (APlayerController* PC = GetPC())
		{
			if (DefaultViewTarget == nullptr)
			{
				DefaultViewTarget = PC->GetViewTarget();
			}
		}
	}
	NextHoldAction();
}

void AChimeraSimDirector::NextHoldAction()
{
	APlayerController* PC = GetPC();
	if (Hold.ShotCursor < Hold.ShotIdx.Num())
	{
		const FChimeraSimShot& S = Options.Shots[Hold.ShotIdx[Hold.ShotCursor]];
		ACameraActor* Cam = FindCamera(S.Camera);
		if (PC == nullptr || Cam == nullptr)
		{
			Fail(8, FString::Printf(TEXT("shot %d:%s: no player controller or camera"), S.Tick, *S.Camera));
			return;
		}
		PC->SetViewTarget(Cam);
		Hold.Phase = EHoldPhase::CameraSettle;
		Hold.PhaseFrames = 0;
		const FString Name = FString::Printf(TEXT("shot_t%04d_%s"), S.Tick, *S.Camera);
		Hold.ShotJson = MakeShared<FJsonObject>();
		Hold.ShotJson->SetStringField(TEXT("name"), Name);
		Hold.ShotJson->SetNumberField(TEXT("tick"), S.Tick);
		Hold.ShotJson->SetStringField(TEXT("camera"), S.Camera);
		const FVector L = Cam->GetActorLocation();
		const FRotator R = Cam->GetActorRotation();
		TArray<TSharedPtr<FJsonValue>> Pose = { MakeShared<FJsonValueNumber>(L.X), MakeShared<FJsonValueNumber>(L.Y), MakeShared<FJsonValueNumber>(L.Z),
			MakeShared<FJsonValueNumber>(R.Pitch), MakeShared<FJsonValueNumber>(R.Yaw), MakeShared<FJsonValueNumber>(Cam->GetCameraComponent()->FieldOfView) };
		Hold.ShotJson->SetArrayField(TEXT("camera_xyz_pitch_yaw_hfov"), Pose);
		return;
	}
	if (Hold.bFilm)
	{
		Hold.bFilm = false;
		if (PC != nullptr && DefaultViewTarget != nullptr && PC->GetViewTarget() != DefaultViewTarget)
		{
			PC->SetViewTarget(DefaultViewTarget);
		}
		Hold.Phase = EHoldPhase::FilmFreeze;
		Hold.PhaseFrames = 0;
		return;
	}
	EndHold();
}

void AChimeraSimDirector::TickHold()
{
	const FString ShotDir = Options.OutDir / TEXT("shots");
	switch (Hold.Phase)
	{
	case EHoldPhase::CameraSettle:
		if (Hold.PhaseFrames >= CameraSettleFrames)
		{
			// Exposure and TSR settle on the new camera first; then both are held for the pair (ChimeraTerrain S5 "Frozen image pairs").
			if (Options.bShotFreeze)
			{
				Renderer->SetExposureHold(true);
				SetTemporalFreeze(true);
			}
			Hold.Phase = EHoldPhase::Frozen;
			Hold.PhaseFrames = 0;
		}
		break;
	case EHoldPhase::Frozen:
		if (Hold.PhaseFrames >= ShotWaitFrames)
		{
			APlayerController* PC = GetPC();
			if (PC == nullptr)
			{
				Fail(8, TEXT("shot: no player controller"));
				return;
			}
			int32 W = 0, H = 0;
			PC->GetViewportSize(W, H);
			if (bHaveCurrentVerify)
			{
				Hold.ShotJson->SetObjectField(TEXT("projection"), ChimeraSimVerify::ProjectShot(*PC, *Renderer, CurrentVerify, FIntPoint(W, H), Options.ShotSamples));
			}
			const FString Path = ShotDir / Hold.ShotJson->GetStringField(TEXT("name")) + TEXT(".png");
			Hold.ShotJson->SetStringField(TEXT("png"), Path);
			if (!RequestCapture(Path)) { return; }
			Hold.Phase = EHoldPhase::WaitShot;
			Hold.PhaseFrames = 0;
		}
		break;
	case EHoldPhase::WaitShot:
	{
		const int32 R = PollCapture();
		if (R < 0)
		{
			Fail(7, FString::Printf(TEXT("screenshot %s not written within %.0f s"), *Hold.PendingPath, CaptureTimeoutSec));
			return;
		}
		if (R > 0)
		{
			Hold.ShotJson->SetNumberField(TEXT("png_bytes"), (double)IFileManager::Get().FileSize(*Hold.PendingPath));
			Hold.ShotJson->SetNumberField(TEXT("units_drawn"), Renderer->GetDrawnLastFrame());
			Renderer->SetUnitsVisible(false);
			Hold.Phase = EHoldPhase::HiddenWait;
			Hold.PhaseFrames = 0;
		}
		break;
	}
	case EHoldPhase::HiddenWait:
		if (Hold.PhaseFrames >= HiddenWaitFrames)
		{
			const FString Path = ShotDir / Hold.ShotJson->GetStringField(TEXT("name")) + TEXT("_hidden.png");
			Hold.ShotJson->SetStringField(TEXT("hidden_png"), Path);
			if (!RequestCapture(Path)) { return; }
			Hold.Phase = EHoldPhase::WaitHidden;
			Hold.PhaseFrames = 0;
		}
		break;
	case EHoldPhase::WaitHidden:
	{
		const int32 R = PollCapture();
		if (R < 0)
		{
			Fail(7, FString::Printf(TEXT("screenshot %s not written within %.0f s"), *Hold.PendingPath, CaptureTimeoutSec));
			return;
		}
		if (R > 0)
		{
			Hold.ShotJson->SetNumberField(TEXT("hidden_bytes"), (double)IFileManager::Get().FileSize(*Hold.PendingPath));
			Renderer->SetUnitsVisible(true);
			if (Options.bShotFreeze)
			{
				SetTemporalFreeze(false);
				Renderer->SetExposureHold(false);
			}
			Hold.ShotJson->SetBoolField(TEXT("pair_frozen"), Options.bShotFreeze);
			if (CurrentVerifyJson.IsValid())
			{
				TArray<TSharedPtr<FJsonValue>> Shots = CurrentVerifyJson->GetArrayField(TEXT("shots"));
				Shots.Add(MakeShared<FJsonValueObject>(Hold.ShotJson));
				CurrentVerifyJson->SetArrayField(TEXT("shots"), Shots);
			}
			++ShotsWritten;
			UE_LOG(LogChimeraSim, Display, TEXT("shot pair written %s (%.0f / %.0f bytes) at tick %d"), *Hold.ShotJson->GetStringField(TEXT("name")),
				Hold.ShotJson->GetNumberField(TEXT("png_bytes")), Hold.ShotJson->GetNumberField(TEXT("hidden_bytes")), Hold.Tick);
			++Hold.ShotCursor;
			NextHoldAction();
		}
		break;
	}
	case EHoldPhase::FilmFreeze:
		if (Hold.PhaseFrames >= FilmFreezeFrames)
		{
			const FString Path = Options.OutDir / TEXT("film") / FString::Printf(TEXT("film_%04d.png"), FilmIndex);
			if (!RequestCapture(Path)) { return; }
			Hold.Phase = EHoldPhase::WaitFilm;
			Hold.PhaseFrames = 0;
		}
		break;
	case EHoldPhase::WaitFilm:
	{
		const int32 R = PollCapture();
		if (R < 0)
		{
			Fail(7, FString::Printf(TEXT("film frame %s not written within %.0f s"), *Hold.PendingPath, CaptureTimeoutSec));
			return;
		}
		if (R > 0)
		{
			FilmLines.Add(FString::Printf(TEXT("%04d %d"), FilmIndex, Hold.Tick));
			++FilmIndex;
			NextHoldAction();
		}
		break;
	}
	default:
		EndHold();
		break;
	}
}

void AChimeraSimDirector::EndHold()
{
	if (Hold.ShotIdx.Num() > 0)
	{
		APlayerController* PC = GetPC();
		if (PC != nullptr && DefaultViewTarget != nullptr)
		{
			PC->SetViewTarget(DefaultViewTarget);
		}
	}
	if (Hold.bVisual)
	{
		Acc = 0.0; // a visual hold took many frames: resume pacing from now (pacing never reaches the sim)
	}
	Hold.bActive = false;
	Hold.Phase = EHoldPhase::None;
}

bool AChimeraSimDirector::DoVerify()
{
	FString Err;
	if (!ChimeraSimVerify::Run(Session, *Renderer, SimTick, CurrentVerify, Err))
	{
		Fail(6, Err);
		return false;
	}
	bHaveCurrentVerify = true;
	CurrentVerifyJson = CurrentVerify.ToJson();
	VerifyRecords.Add(MakeShared<FJsonValueObject>(CurrentVerifyJson));
	VerifyPassed += CurrentVerify.bPass ? 1 : 0;
	UE_LOG(LogChimeraSim, Display, TEXT("verify tick=%d rows=%d alive=%d phased=%d visible=%d drawn_instances=%d dead_visible=%d alive_hidden=%d group_mismatch=%d max_err_cm=%.4f stats_alive=%lld new_ids_alive=%d new_ids_visible=%d units_hidden_by_option=%d pass=%d"),
		CurrentVerify.Tick, CurrentVerify.Rows, CurrentVerify.Alive, CurrentVerify.Phased, CurrentVerify.Visible, CurrentVerify.DrawnInstances, CurrentVerify.DeadVisible,
		CurrentVerify.AliveHidden, CurrentVerify.GroupMismatch, CurrentVerify.MaxErrCm, CurrentVerify.StatsAlive, CurrentVerify.NewIdsAlive, CurrentVerify.NewIdsVisible,
		CurrentVerify.bUnitsHiddenByOption ? 1 : 0, CurrentVerify.bPass ? 1 : 0);
	UE_LOG(LogChimeraSim, Display, TEXT("verify tick=%d buildings rows=%d alive=%d visible=%d drawn_instances=%d dead_visible=%d alive_hidden=%d group_mismatch=%d max_err_cm=%.4f ai_created_alive=%d ai_created_visible=%d pass=%d"),
		CurrentVerify.Tick, CurrentVerify.BuildingRows, CurrentVerify.BuildingsAlive, CurrentVerify.BuildingsVisible, CurrentVerify.BuildingDrawnInstances,
		CurrentVerify.BuildingDeadVisible, CurrentVerify.BuildingAliveHidden, CurrentVerify.BuildingGroupMismatch, CurrentVerify.BuildingMaxErrCm,
		CurrentVerify.AiCreatedAlive, CurrentVerify.AiCreatedVisible, CurrentVerify.bBuildingsPass ? 1 : 0);
	return true;
}

bool AChimeraSimDirector::RequestCapture(const FString& Path)
{
	IFileManager& FM = IFileManager::Get();
	FM.MakeDirectory(*FPaths::GetPath(Path), /*Tree*/ true);
	// No stale file can satisfy "exists" (EXECUTION 2.2: done = exists, non-empty, newer than the request).
	FM.Delete(*Path, /*RequireExists*/ false, /*EvenReadOnly*/ true, /*Quiet*/ true);
	if (FM.FileExists(*Path))
	{
		Fail(7, FString::Printf(TEXT("cannot replace %s"), *Path));
		return false;
	}
	bShotProcessed = false;
	Hold.PendingPath = Path;
	Hold.RequestStamp = FDateTime::UtcNow();
	Hold.RequestTime = FPlatformTime::Seconds();
	Hold.LastSize = -1;
	FScreenshotRequest::RequestScreenshot(Path, /*bInShowUI*/ false, /*bAddFilenameSuffix*/ false);
	return true;
}

int32 AChimeraSimDirector::PollCapture()
{
	if (FPlatformTime::Seconds() - Hold.RequestTime > CaptureTimeoutSec)
	{
		return -1;
	}
	if (!bShotProcessed)
	{
		return 0; // serviced at the next draw (F25); the delegate fires even on failure, so the file decides
	}
	IFileManager& FM = IFileManager::Get();
	const int64 Size = FM.FileSize(*Hold.PendingPath);
	if (Size <= 0 || FM.GetTimeStamp(*Hold.PendingPath) < Hold.RequestStamp - FTimespan::FromSeconds(2.0))
	{
		return 0;
	}
	if (Size == Hold.LastSize)
	{
		return 1; // same size on two consecutive frames
	}
	Hold.LastSize = Size;
	return 0;
}

void AChimeraSimDirector::OnScreenshotProcessed()
{
	bShotProcessed = true;
}

void AChimeraSimDirector::SetTemporalFreeze(bool bOn)
{
	// ChimeraTerrain TerrainScriptDirector::StepTemporalFreeze: r.Test.FreezeTemporalSequences stops the view's frame index and the
	// TSR sample index, r.TemporalAA.Debug.OverrideTemporalIndex pins the jitter (SceneVisibility.cpp, non-Shipping). Render state only.
	static const TCHAR* const Names[2] = { TEXT("r.Test.FreezeTemporalSequences"), TEXT("r.TemporalAA.Debug.OverrideTemporalIndex") };
	static const TCHAR* const OnValues[2] = { TEXT("1"), TEXT("0") };
	static const TCHAR* const OffValues[2] = { TEXT("0"), TEXT("-1") };
	for (int32 K = 0; K < 2; ++K)
	{
		if (IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(Names[K]))
		{
			Cv->Set(bOn ? OnValues[K] : OffValues[K], ECVF_SetByCode);
			if (bOn)
			{
				TemporalReadback.Add(Names[K], Cv->GetString());
			}
		}
		else if (bOn)
		{
			TemporalReadback.Add(Names[K], TEXT("missing"));
		}
	}
	bTemporalFrozen = bOn;
}

TSharedRef<FJsonObject> AChimeraSimDirector::BuildVerifyJson() const
{
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("leg"), Options.Leg);
	J->SetNumberField(TEXT("ticks"), Options.Ticks);
	J->SetNumberField(TEXT("sim_tick_reached"), SimTick);
	J->SetStringField(TEXT("shots_list"), Options.ShotList());
	J->SetStringField(TEXT("shot_dir"), Options.OutDir / TEXT("shots"));
	J->SetBoolField(TEXT("ai"), Options.bAi);
	J->SetBoolField(TEXT("hide_units"), Options.bHideUnits);
	J->SetBoolField(TEXT("dead_under_ground"), Options.bDeadUnderGround);
	J->SetBoolField(TEXT("shot_freeze"), Options.bShotFreeze);
	J->SetNumberField(TEXT("shot_samples"), Options.ShotSamples);
	J->SetNumberField(TEXT("verify_every"), Options.VerifyEvery);
	J->SetNumberField(TEXT("camera_settle_frames"), CameraSettleFrames);
	J->SetNumberField(TEXT("shot_wait_frames"), ShotWaitFrames);
	J->SetNumberField(TEXT("hidden_wait_frames"), HiddenWaitFrames);
	TSharedRef<FJsonObject> Tr = MakeShared<FJsonObject>();
	for (const TPair<FString, FString>& P : TemporalReadback) { Tr->SetStringField(P.Key, P.Value); }
	J->SetObjectField(TEXT("temporal_freeze_readback"), Tr);
	if (Renderer != nullptr)
	{
		TSharedRef<FJsonObject> R = MakeShared<FJsonObject>();
		R->SetStringField(TEXT("groups"), Renderer->Describe());
		R->SetNumberField(TEXT("group_count"), Renderer->GetGroups().Num());
		R->SetNumberField(TEXT("journal_adds"), Renderer->GetJournal().Num());
		R->SetNumberField(TEXT("re_adds"), Renderer->GetReAdds());
		R->SetNumberField(TEXT("new_id_adds"), Renderer->GetNewIdAdds());
		R->SetNumberField(TEXT("building_adds"), Renderer->GetBuildingAdds());
		R->SetNumberField(TEXT("initial_rows"), Renderer->GetInitialUnitRows());
		R->SetNumberField(TEXT("building_journal"), Renderer->GetBuildingJournal().Num());
		R->SetNumberField(TEXT("initial_building_adds"), Renderer->GetInitialBuildingAdds());
		J->SetObjectField(TEXT("renderer"), R);
	}
	TArray<int32> Vt = VerifyTicks.Array();
	Vt.Sort();
	TArray<TSharedPtr<FJsonValue>> VtJ;
	for (int32 T : Vt) { VtJ.Add(MakeShared<FJsonValueNumber>(T)); }
	J->SetArrayField(TEXT("verify_ticks"), VtJ);
	J->SetArrayField(TEXT("verifies"), VerifyRecords);
	J->SetNumberField(TEXT("verify_passed"), VerifyPassed);
	J->SetNumberField(TEXT("shots_written"), ShotsWritten);
	J->SetNumberField(TEXT("film_frames"), FilmIndex);
	J->SetBoolField(TEXT("all_verify_pass"), VerifyRecords.Num() == Vt.Num() && VerifyPassed == Vt.Num());
	return J;
}

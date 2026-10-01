// Copyright Chimera. ChimeraSimHost module (plan A 3.7). In a -game process StartupModule loads ChimeraSim.dll once (never freed),
// logs "LogChimeraSim: loaded path=...", runs the runtime self tests and exits forced with 3 on a load or ABI failure
// (EXECUTION 2.2). The X1 diagnostic stays: with -ChimeraAotSmoke=<abs dll>, a core ticker loads a NativeAOT test library 60 frames
// after start, calls its exports, logs the result and exits forced (0 if all pass, 6 otherwise); the sim DLL is not loaded then.
#include "CoreMinimal.h"
#include "ChimeraSimLibrary.h"
#include "ChimeraSimLog.h"
#include "Containers/Ticker.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformMisc.h"
#include "Misc/CommandLine.h"
#include "Misc/CoreMisc.h"
#include "Misc/Parse.h"
#include "Modules/ModuleManager.h"

DEFINE_LOG_CATEGORY(LogChimeraSim);

namespace
{
	// Exports of the smoke library (tools/sim-trial/aot-ue-smoke/Smoke.cs), plain C ABI.
	typedef int32 (*FSmokeAdd)(int32, int32);
	typedef int32 (*FSmokeRuntime)(char* Buf, int32 Cap, int32* Len);
	typedef int32 (*FSmokeSelfTest)(int32 Kind);

	/** X1: loads the library (never freed: NativeAOT libraries cannot be unloaded), runs the checks, exits. */
	void RunAotSmoke(const FString& DllPath)
	{
		UE_LOG(LogChimeraSim, Display, TEXT("aot smoke: loading %s"), *DllPath);
		void* Handle = FPlatformProcess::GetDllHandle(*DllPath);
		if (!Handle)
		{
			UE_LOG(LogChimeraSim, Error, TEXT("aot smoke: GetDllHandle failed for %s"), *DllPath);
			FPlatformMisc::RequestExitWithStatus(true, 6);
			return;
		}
		FSmokeAdd Add = (FSmokeAdd)FPlatformProcess::GetDllExport(Handle, TEXT("smoke_add"));
		FSmokeRuntime Runtime = (FSmokeRuntime)FPlatformProcess::GetDllExport(Handle, TEXT("smoke_runtime"));
		FSmokeSelfTest SelfTest = (FSmokeSelfTest)FPlatformProcess::GetDllExport(Handle, TEXT("smoke_selftest"));
		if (!Add || !Runtime || !SelfTest)
		{
			UE_LOG(LogChimeraSim, Error, TEXT("aot smoke: missing export (add=%p runtime=%p selftest=%p)"), (void*)Add, (void*)Runtime, (void*)SelfTest);
			FPlatformMisc::RequestExitWithStatus(true, 6);
			return;
		}

		const int32 AddRc = Add(40, 2);
		UE_LOG(LogChimeraSim, Display, TEXT("smoke_add(40,2)=%d"), AddRc);

		char Buf[256] = {};
		int32 Len = 0;
		const int32 RtRc = Runtime(Buf, (int32)sizeof(Buf), &Len);
		FString RuntimeText = RtRc == 0 ? FString(Len, UTF8_TO_TCHAR(Buf)) : FString(TEXT("runtime=? aot=?"));

		int32 Passed = 0;
		for (int32 Kind = 1; Kind <= 5; ++Kind)
		{
			const int32 Rc = SelfTest(Kind);
			UE_LOG(LogChimeraSim, Display, TEXT("smoke kind=%d rc=%d"), Kind, Rc);
			if (Rc == 0) { ++Passed; }
		}

		const bool bOk = (Passed == 5) && AddRc == 42 && RtRc == 0;
		UE_LOG(LogChimeraSim, Display, TEXT("smoke %d/5 %s"), Passed, *RuntimeText);
		FPlatformMisc::RequestExitWithStatus(true, bOk ? 0 : 6);
	}
}

class FChimeraSimHostModule : public IModuleInterface
{
public:
	virtual void StartupModule() override
	{
		FString SmokeDll;
		if (FParse::Value(FCommandLine::Get(), TEXT("-ChimeraAotSmoke="), SmokeDll, /*bShouldStopOnSeparator*/ false))
		{
			StartAotSmoke(SmokeDll);
			return;
		}
		// Only a process launched as a game loads the sim here (not the editor, not commandlets); the director loads it
		// lazily in any other world (Load() is idempotent, one "loaded path=" line per process).
		// A -game run of another map (the look test's run_fps.ps1) does not ask for the sim: only a command line that names the
		// sim game mode or any -ChimeraSim* option loads it at start-up.
		if (!IsRunningGame() || IsRunningCommandlet() || FCString::Stristr(FCommandLine::Get(), TEXT("ChimeraSim")) == nullptr)
		{
			return;
		}
		FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
		FString Err;
		if (!Lib.Load(Err))
		{
			UE_LOG(LogChimeraSim, Error, TEXT("load failed: %s (exit 3)"), *Err);
			GLog->Flush();
			FPlatformMisc::RequestExitWithStatus(true, 3);
			return;
		}
		const int32 Passed = Lib.RunSelfTests();
		if (Passed != 3)
		{
			UE_LOG(LogChimeraSim, Error, TEXT("runtime self tests %d/3 inside Unreal (exit 3)"), Passed);
			GLog->Flush();
			FPlatformMisc::RequestExitWithStatus(true, 3);
		}
	}

	virtual void ShutdownModule() override
	{
		if (TickHandle.IsValid()) { FTSTicker::GetCoreTicker().RemoveTicker(TickHandle); }
		// The sim DLL is never freed (R3 3.2).
	}

private:
	void StartAotSmoke(const FString& DllPath)
	{
		UE_LOG(LogChimeraSim, Display, TEXT("aot smoke requested: %s (runs after 60 frames)"), *DllPath);
		FrameCount = 0;
		TickHandle = FTSTicker::GetCoreTicker().AddTicker(TEXT("ChimeraAotSmoke"), 0.0f, [this, DllPath](float)
		{
			if (++FrameCount < 60) { return true; }
			RunAotSmoke(DllPath);
			return false;
		});
	}

	FTSTicker::FDelegateHandle TickHandle;
	int32 FrameCount = 0;
};

IMPLEMENT_MODULE(FChimeraSimHostModule, ChimeraSimHost)

// Copyright Chimera. See ChimeraSimLibrary.h.
#include "ChimeraSimLibrary.h"

#include "ChimeraSimLog.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Paths.h"

#include <xmmintrin.h>

namespace ChimeraSim
{
	uint32 ReadMxcsr() { return (uint32)_mm_getcsr(); }
	void WriteMxcsr(uint32 Value) { _mm_setcsr((unsigned int)Value); }
}

FChimeraSimLibrary& FChimeraSimLibrary::Get()
{
	static FChimeraSimLibrary Instance;
	return Instance;
}

namespace
{
	/** Resolves one export into a typed pointer; records the name when missing. */
	template <typename T>
	void Resolve(void* Handle, const TCHAR* Name, T& Out, TArray<FString>& Missing)
	{
		// GetDllExport is GetProcAddress with TCHAR_TO_ANSI (WindowsPlatformProcess.cpp:315-320).
		Out = reinterpret_cast<T>(FPlatformProcess::GetDllExport(Handle, Name));
		if (Out == nullptr)
		{
			Missing.Add(Name);
		}
	}

	/** Reads a NUL-terminated UTF-8 string export (buf, cap, len) into an FString; rc in OutRc. */
	template <typename FnType, typename... ArgTypes>
	FString ReadText(FChimeraSimLibrary& Lib, int32& OutRc, FnType Fn, ArgTypes... Args)
	{
		ANSICHAR Small[1024];
		int32 Len = 0;
		OutRc = Lib.Call(Fn, Args..., Small, (int32)sizeof(Small), &Len);
		if (OutRc == CHIMERA_OK)
		{
			return FString(UTF8_TO_TCHAR(Small));
		}
		if (OutRc == CHIMERA_E_BUFFER && Len > 0 && Len < (1 << 20))
		{
			TArray<ANSICHAR> Big;
			Big.SetNumZeroed(Len + 1);
			OutRc = Lib.Call(Fn, Args..., Big.GetData(), (int32)Big.Num(), &Len);
			if (OutRc == CHIMERA_OK)
			{
				return FString(UTF8_TO_TCHAR(Big.GetData()));
			}
		}
		return FString();
	}
}

bool FChimeraSimLibrary::Load(FString& OutError)
{
	if (bAttempted)
	{
		OutError = AttemptError;
		return bLoaded;
	}
	bAttempted = true;

	// R3 3.1/3.2 and plan A 3.7: a full path under the project (FPaths::ConvertRelativePathToFull, Paths.h).
	DllPath = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir() / TEXT("Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll"));
	if (!FPaths::FileExists(DllPath))
	{
		AttemptError = FString::Printf(TEXT("ChimeraSim.dll not found at %s (run NAT/publish.ps1 -StageOnly)"), *DllPath);
		OutError = AttemptError;
		return false;
	}
	Handle = FPlatformProcess::GetDllHandle(*DllPath);
	if (Handle == nullptr)
	{
		AttemptError = FString::Printf(TEXT("GetDllHandle failed for %s"), *DllPath);
		OutError = AttemptError;
		return false;
	}

	TArray<FString> Missing;
	Resolve(Handle, TEXT("chimera_abi_version"), Fns.AbiVersion, Missing);
	Resolve(Handle, TEXT("chimera_abi_check"), Fns.AbiCheck, Missing);
	Resolve(Handle, TEXT("chimera_build_info"), Fns.BuildInfo, Missing);
	Resolve(Handle, TEXT("chimera_session_create"), Fns.SessionCreate, Missing);
	Resolve(Handle, TEXT("chimera_session_destroy"), Fns.SessionDestroy, Missing);
	Resolve(Handle, TEXT("chimera_set_checksum_interval"), Fns.SetChecksumInterval, Missing);
	Resolve(Handle, TEXT("chimera_last_checksum"), Fns.LastChecksum, Missing);
	Resolve(Handle, TEXT("chimera_pre_tick_hashes"), Fns.PreTickHashes, Missing);
	Resolve(Handle, TEXT("chimera_submit_order"), Fns.SubmitOrder, Missing);
	Resolve(Handle, TEXT("chimera_step"), Fns.Step, Missing);
	Resolve(Handle, TEXT("chimera_read_units"), Fns.ReadUnits, Missing);
	Resolve(Handle, TEXT("chimera_read_buildings"), Fns.ReadBuildings, Missing);
	Resolve(Handle, TEXT("chimera_units_digest"), Fns.UnitsDigest, Missing);
	Resolve(Handle, TEXT("chimera_wide_digest"), Fns.WideDigest, Missing);
	Resolve(Handle, TEXT("chimera_unit_def_id"), Fns.UnitDefId, Missing);
	Resolve(Handle, TEXT("chimera_building_def_id"), Fns.BuildingDefId, Missing);
	Resolve(Handle, TEXT("chimera_verdict"), Fns.Verdict, Missing);
	Resolve(Handle, TEXT("chimera_stats"), Fns.Stats, Missing);
	Resolve(Handle, TEXT("chimera_file_sha256"), Fns.FileSha256, Missing);
	Resolve(Handle, TEXT("chimera_selftest"), Fns.SelfTest, Missing);
	Resolve(Handle, TEXT("chimera_last_error"), Fns.LastError, Missing);
	if (Missing.Num() > 0)
	{
		AttemptError = FString::Printf(TEXT("missing exports in %s: %s"), *DllPath, *FString::Join(Missing, TEXT(", ")));
		OutError = AttemptError;
		return false;
	}

	// First call is the trivial version export (R3 3.2: a bad runtime start-up shows in the first log line).
	const int32 Version = Call(Fns.AbiVersion);
	if (Version != CHIMERA_ABI_VERSION)
	{
		AttemptError = FString::Printf(TEXT("ABI mismatch: library 0x%08X, header 0x%08X"), (uint32)Version, (uint32)CHIMERA_ABI_VERSION);
		OutError = AttemptError;
		return false;
	}
	const int32 SizeRc = Call(Fns.AbiCheck, (int32)sizeof(ChimeraUnit), (int32)sizeof(ChimeraBuilding));
	if (SizeRc != CHIMERA_OK)
	{
		AttemptError = FString::Printf(TEXT("ABI struct check failed rc=%d (unit %d, building %d bytes)"), SizeRc, (int32)sizeof(ChimeraUnit), (int32)sizeof(ChimeraBuilding));
		OutError = AttemptError;
		return false;
	}
	int32 InfoRc = 0;
	BuildInfo = ReadText(*this, InfoRc, Fns.BuildInfo);
	if (InfoRc != CHIMERA_OK)
	{
		AttemptError = FString::Printf(TEXT("chimera_build_info rc=%d"), InfoRc);
		OutError = AttemptError;
		return false;
	}

	bLoaded = true;
	UE_LOG(LogChimeraSim, Display, TEXT("loaded path=%s abi=0x%08X structs=%d/%d build=%s"),
		*DllPath, (uint32)Version, (int32)sizeof(ChimeraUnit), (int32)sizeof(ChimeraBuilding), *BuildInfo);
	return true;
}

FString FChimeraSimLibrary::GetBuildField(const TCHAR* Key) const
{
	TArray<FString> Parts;
	BuildInfo.ParseIntoArray(Parts, TEXT(";"));
	const FString Prefix = FString(Key) + TEXT("=");
	for (const FString& P : Parts)
	{
		if (P.StartsWith(Prefix, ESearchCase::CaseSensitive))
		{
			return P.RightChop(Prefix.Len());
		}
	}
	return FString();
}

FString FChimeraSimLibrary::LastError(int32 Session)
{
	if (!bLoaded)
	{
		return FString();
	}
	int32 Rc = 0;
	return ReadText(*this, Rc, Fns.LastError, Session);
}

FString FChimeraSimLibrary::FileSha256(const FString& Path, int32* OutRc)
{
	int32 Rc = CHIMERA_E_ARG;
	FString Hex;
	if (bLoaded)
	{
		FTCHARToUTF8 Utf8(*Path);
		Hex = ReadText(*this, Rc, Fns.FileSha256, (const char*)Utf8.Get());
	}
	if (OutRc)
	{
		*OutRc = Rc;
	}
	return Rc == CHIMERA_OK ? Hex : FString();
}

FString FChimeraSimLibrary::UnitDefId(int32 Session, int32 UnitId)
{
	int32 Rc = 0;
	FString S = ReadText(*this, Rc, Fns.UnitDefId, Session, UnitId);
	return Rc == CHIMERA_OK ? S : FString();
}

int32 FChimeraSimLibrary::RunSelfTests()
{
	int32 Passed = 0;
	for (int32 Kind = 1; Kind <= 3; ++Kind)
	{
		const int32 Rc = Call(Fns.SelfTest, Kind);
		UE_LOG(LogChimeraSim, Display, TEXT("selftest kind=%d rc=%d"), Kind, Rc);
		if (Rc == CHIMERA_OK)
		{
			++Passed;
		}
	}
	UE_LOG(LogChimeraSim, Display, TEXT("selftest %d/3"), Passed);
	return Passed;
}

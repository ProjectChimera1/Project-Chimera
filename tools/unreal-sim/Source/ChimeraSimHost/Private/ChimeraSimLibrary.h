// Copyright Chimera. FChimeraSimLibrary: loads ChimeraSim.dll (NativeAOT, C ABI) once per process and calls it through an
// MXCSR shim (plan A 3.4, 3.7; R3 3.2). The DLL is never freed: a NativeAOT library cannot unload (R3 3.2, F22).
#pragma once

#include "CoreMinimal.h"

THIRD_PARTY_INCLUDES_START
// The ABI of record, copied here by NAT/publish.ps1 -StageOnly (SHA-256 checked against the published header).
#include "ThirdParty/chimera_sim.h"
THIRD_PARTY_INCLUDES_END

/** Function-pointer table for every export of chimera_sim.h (types from the header's own declarations). */
struct FChimeraSimApi
{
	decltype(&chimera_abi_version) AbiVersion = nullptr;
	decltype(&chimera_abi_check) AbiCheck = nullptr;
	decltype(&chimera_build_info) BuildInfo = nullptr;
	decltype(&chimera_session_create) SessionCreate = nullptr;
	decltype(&chimera_session_destroy) SessionDestroy = nullptr;
	decltype(&chimera_set_checksum_interval) SetChecksumInterval = nullptr;
	decltype(&chimera_last_checksum) LastChecksum = nullptr;
	decltype(&chimera_pre_tick_hashes) PreTickHashes = nullptr;
	decltype(&chimera_submit_order) SubmitOrder = nullptr;
	decltype(&chimera_step) Step = nullptr;
	decltype(&chimera_read_units) ReadUnits = nullptr;
	decltype(&chimera_read_buildings) ReadBuildings = nullptr;
	decltype(&chimera_units_digest) UnitsDigest = nullptr;
	decltype(&chimera_wide_digest) WideDigest = nullptr;
	decltype(&chimera_unit_def_id) UnitDefId = nullptr;
	decltype(&chimera_building_def_id) BuildingDefId = nullptr;
	decltype(&chimera_verdict) Verdict = nullptr;
	decltype(&chimera_stats) Stats = nullptr;
	decltype(&chimera_file_sha256) FileSha256 = nullptr;
	decltype(&chimera_selftest) SelfTest = nullptr;
	decltype(&chimera_last_error) LastError = nullptr;
};

namespace ChimeraSim
{
	/** Default x64 MXCSR: all exceptions masked, round to nearest, no FTZ/DAZ. */
	constexpr uint32 DefaultMxcsr = 0x1F80u;
	/** MXCSR control bits (masks, rounding, FTZ, DAZ); bits 0-5 are sticky status flags and are not "configuration". */
	constexpr uint32 MxcsrControlMask = 0xFFC0u;

	uint32 ReadMxcsr();
	void WriteMxcsr(uint32 Value);
}

/**
 * Process-wide handle on ChimeraSim.dll. Load() resolves every export, checks the ABI version and struct sizes,
 * and logs "LogChimeraSim: loaded path=<full> abi=0x... build=..." exactly once. Every call goes through Call(),
 * which saves MXCSR, sets 0x1F80, calls, restores, and counts calls that found non-default control bits.
 * Game thread only (the DLL takes one process-wide lock and creates no threads).
 */
class FChimeraSimLibrary
{
public:
	static FChimeraSimLibrary& Get();

	/** Loads <ProjectDir>/Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll once; true when loaded and ABI-checked. */
	bool Load(FString& OutError);
	bool IsLoaded() const { return bLoaded; }
	const FChimeraSimApi& Api() const { return Fns; }

	/** Full path of the loaded DLL and its chimera_build_info string ("abi=1.0;algo=29;commit=...;..."). */
	const FString& GetDllPath() const { return DllPath; }
	const FString& GetBuildInfo() const { return BuildInfo; }
	/** One field of the build info ("commit", "dirty", "runtime", "aot", "algo"); empty if absent. */
	FString GetBuildField(const TCHAR* Key) const;

	/** Calls Fn(Args...) under the MXCSR shim (plan A 3.4 "FP environment"). */
	template <typename FnType, typename... ArgTypes>
	int32 Call(FnType Fn, ArgTypes... Args)
	{
		const uint32 Saved = ChimeraSim::ReadMxcsr();
		const bool bNonDefault = (Saved & ChimeraSim::MxcsrControlMask) != ChimeraSim::DefaultMxcsr;
		++Calls;
		if (bNonDefault)
		{
			++MxcsrNonDefault;
			LastNonDefaultMxcsr = Saved;
		}
		ChimeraSim::WriteMxcsr(ChimeraSim::DefaultMxcsr);
		const int32 Rc = (int32)Fn(Args...);
		ChimeraSim::WriteMxcsr(Saved);
		return Rc;
	}

	/** chimera_last_error text for a session (0 = process-wide). */
	FString LastError(int32 Session);
	/** chimera_file_sha256 of a file (lowercase hex); empty with OutRc < 0 on failure. */
	FString FileSha256(const FString& Path, int32* OutRc = nullptr);
	/** chimera_unit_def_id / chimera_building_def_id as a string ("" when none). */
	FString UnitDefId(int32 Session, int32 UnitId);

	/** Runs chimera_selftest kinds 1-3 (throw/catch, NullReferenceException, full blocking GC); returns how many passed. */
	int32 RunSelfTests();

	int64 GetCalls() const { return Calls; }
	int64 GetMxcsrNonDefault() const { return MxcsrNonDefault; }
	uint32 GetLastNonDefaultMxcsr() const { return LastNonDefaultMxcsr; }

private:
	bool bLoaded = false;
	bool bAttempted = false;
	FString AttemptError;
	void* Handle = nullptr;
	FChimeraSimApi Fns;
	FString DllPath;
	FString BuildInfo;
	int64 Calls = 0;
	int64 MxcsrNonDefault = 0;
	uint32 LastNonDefaultMxcsr = 0;
};

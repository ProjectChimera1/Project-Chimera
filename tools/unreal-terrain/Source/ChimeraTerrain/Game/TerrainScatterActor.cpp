// Project Chimera terrain trial (plan C scatter 3.1-3.8, task S4). Original Chimera code.

#include "Game/TerrainScatterActor.h"

#include "ChimeraTerrain.h"
#include "Engine/StaticMesh.h"
#include "Engine/World.h"
#include "Game/TerrainActor.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformTime.h"
#include "Misc/App.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Async/ParallelFor.h"
#include "Data/TerrainScatterMath.h"
#include "Render/TerrainScatterRenderer.h"
#include "AssetCompilingManager.h"
#include "ShaderCompiler.h"
#include "UObject/UObjectGlobals.h"
#if WITH_EDITOR
#include "EditorSupportDelegates.h"
#endif

#include <cmath>

using namespace ChimeraTerrain;

namespace
{
	constexpr double Pi = 3.14159265358979323846;

	/** Cull end (metres) per mesh unit (plan C scatter 3.4 "Cull end"); TreeBroadA also shows saplings (300 m), so the unit keeps the tree's 600 m. */
	double MeshCullEndM(EScatterMesh M)
	{
		switch (M)
		{
		case EScatterMesh::GrassT0: return 130.0;
		case EScatterMesh::GrassT1: return 90.0;
		case EScatterMesh::Tussock: return 150.0;
		case EScatterMesh::Flower: return 110.0;
		case EScatterMesh::NearCard: return 32.0;
		case EScatterMesh::ShrubA:
		case EScatterMesh::ShrubB: return 300.0;
		case EScatterMesh::Fern: return 100.0;
		case EScatterMesh::RockA:
		case EScatterMesh::RockB: return 300.0;
		default: return 600.0;
		}
	}

	EScatterLayer MeshLayer(EScatterMesh M)
	{
		switch (M)
		{
		case EScatterMesh::GrassT0:
		case EScatterMesh::GrassT1:
		case EScatterMesh::NearCard: return EScatterLayer::Grass;
		case EScatterMesh::Tussock:
		case EScatterMesh::Flower:
		case EScatterMesh::Fern: return EScatterLayer::Groundcover;
		case EScatterMesh::ShrubA:
		case EScatterMesh::ShrubB: return EScatterLayer::Shrubs;
		case EScatterMesh::RockA:
		case EScatterMesh::RockB: return EScatterLayer::Rocks;
		default: return EScatterLayer::Trees;
		}
	}

	/** Tag class of a mesh unit (ComponentTags ChimeraScatter.<class>; plan C scatter 3.7). */
	const TCHAR* MeshTagClass(EScatterMesh M)
	{
		switch (M)
		{
		case EScatterMesh::GrassT0:
		case EScatterMesh::GrassT1: return TEXT("Grass");
		case EScatterMesh::Tussock: return TEXT("Tussock");
		case EScatterMesh::Flower: return TEXT("Flower");
		case EScatterMesh::NearCard: return TEXT("NearCard");
		case EScatterMesh::ShrubA:
		case EScatterMesh::ShrubB: return TEXT("Shrub");
		case EScatterMesh::Fern: return TEXT("Fern");
		case EScatterMesh::RockA:
		case EScatterMesh::RockB: return TEXT("Rock");
		default: return TEXT("Tree");
		}
	}

	/** Casters (plan C scatter 3.7 table): shrubs, trees, rocks cast VSM shadows and are occluders; grass, groundcover and ferns do not. */
	bool MeshCasts(EScatterMesh M)
	{
		return MeshLayer(M) == EScatterLayer::Shrubs || MeshLayer(M) == EScatterLayer::Trees || MeshLayer(M) == EScatterLayer::Rocks;
	}

	bool IsGrassMesh(EScatterMesh M)
	{
		return M == EScatterMesh::GrassT0 || M == EScatterMesh::GrassT1;
	}

	FString Hex64(uint64 V)
	{
		return FString::Printf(TEXT("0x%016llx"), static_cast<unsigned long long>(V));
	}

	TSharedRef<FJsonObject> SeriesJson(const TArray<double>& In)
	{
		TArray<double> S = In;
		S.Sort();
		TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetNumberField(TEXT("n"), S.Num());
		auto P = [&S](double Pc)
		{
			if (S.Num() == 0)
			{
				return 0.0;
			}
			const int32 Rank = FMath::Clamp(FMath::CeilToInt(Pc / 100.0 * S.Num()), 1, S.Num());
			return S[Rank - 1];
		};
		double Sum = 0.0;
		for (const double V : S)
		{
			Sum += V;
		}
		O->SetNumberField(TEXT("p50"), P(50.0));
		O->SetNumberField(TEXT("p95"), P(95.0));
		O->SetNumberField(TEXT("p99"), P(99.0));
		O->SetNumberField(TEXT("p999"), P(99.9));
		O->SetNumberField(TEXT("max"), S.Num() ? S.Last() : 0.0);
		O->SetNumberField(TEXT("mean"), S.Num() ? Sum / S.Num() : 0.0);
		return O;
	}

	/**
	 * Unit normal of HF's triangulated surface at (X, Y) metres: the cell and triangle choice of FTerrainHeightfield::SampleSurface
	 * (TerrainHeightfield.cpp:137-162; FV >= FU = the upper triangle BL, TL, TR). Float heights, independent of the record's integer gradient.
	 */
	FVector HfCellNormal(const FTerrainHeightfield& HF, double X, double Y)
	{
		const int32 Q = HF.Width() - 1;
		const double U = FMath::Clamp(X + HF.HalfExtentM(), 0.0, static_cast<double>(Q));
		const double V = FMath::Clamp(Y + HF.HalfExtentM(), 0.0, static_cast<double>(Q));
		const int32 CX = FMath::Min(static_cast<int32>(FMath::FloorToDouble(U)), Q - 1);
		const int32 CY = FMath::Min(static_cast<int32>(FMath::FloorToDouble(V)), Q - 1);
		const double FU = U - CX;
		const double FV = V - CY;
		const double HBL = HF.GetHeight(CX, CY);
		const double HBR = HF.GetHeight(CX + 1, CY);
		const double HTL = HF.GetHeight(CX, CY + 1);
		const double HTR = HF.GetHeight(CX + 1, CY + 1);
		double GX;
		double GY;
		if (FV >= FU)
		{
			GX = HTR - HTL;
			GY = HTL - HBL;
		}
		else
		{
			GX = HBR - HBL;
			GY = HTR - HBR;
		}
		return FVector(-GX, -GY, 1.0).GetSafeNormal();
	}

	/**
	 * The up axis a class rule gives for a surface normal (align factor, plus a rock's hashed tilt from the record's Tilt and Yaw words), with BuildInstance's tilt
	 * frame (world X projected onto the plane perpendicular to the aligned axis), which is well conditioned near vertical, so the full axis is compared everywhere.
	 */
	FVector RuleUpAxis(const FScatterPalette& P, EScatterClass Class, const FVector& N, uint16 Tilt, uint16 Yaw)
	{
		int64 AQ = 0;
		switch (Class)
		{
		case EScatterClass::Grass: AQ = P.Get(EScatterParam::GrassAlign); break;
		case EScatterClass::Tussock: AQ = P.Get(EScatterParam::TussockAlign); break;
		case EScatterClass::Flower: AQ = P.Get(EScatterParam::FlowerAlign); break;
		case EScatterClass::NearCard: AQ = P.Get(EScatterParam::NearCardAlign); break;
		case EScatterClass::Shrub: AQ = P.Get(EScatterParam::ShrubAlign); break;
		case EScatterClass::Fern: AQ = P.Get(EScatterParam::FernAlign); break;
		case EScatterClass::Rock: AQ = P.Get(EScatterParam::RockAlign); break;
		default: AQ = 0; break;
		}
		const double A = static_cast<double>(AQ) / 65536.0;
		FVector Up = (N * A + FVector(0.0, 0.0, 1.0 - A)).GetSafeNormal();
		if (Class == EScatterClass::Rock)
		{
			const double MaxDeg = static_cast<double>(P.Get(EScatterParam::RockTiltDeg)) / 65536.0;
			const double Ang = (static_cast<double>(Tilt) / 65536.0) * MaxDeg * Pi / 180.0;
			const double Dir = 2.0 * Pi * (static_cast<double>((static_cast<uint32>(Tilt) * 3u + Yaw) & 0xFFFFu) / 65536.0);
			FVector E1(1.0 - Up.X * Up.X, -Up.X * Up.Y, -Up.X * Up.Z);
			if (E1.SizeSquared() < 1e-6)
			{
				E1 = FVector(-Up.Y * Up.X, 1.0 - Up.Y * Up.Y, -Up.Y * Up.Z);
			}
			// Normalised exactly as BuildInstance's Normalize3 (threshold 1e-12), not with FVector::Normalize's tolerance.
			const double E1Len = E1.Size();
			if (E1Len > 1e-12)
			{
				E1 /= E1Len;
			}
			const FVector E2 = FVector::CrossProduct(Up, E1);
			const double T = std::tan(Ang);
			Up = (Up + E1 * (std::cos(Dir) * T) + E2 * (std::sin(Dir) * T)).GetSafeNormal();
		}
		return Up;
	}

	/** The z a class rule gives on HF's float surface at (X, Y) (plan C scatter 3.4 "Scale, align, sink"; Z rules use palette constants only). */
	double RuleZ(const FTerrainHeightfield& HF, const FScatterPalette& P, EScatterClass Class, double X, double Y, double ScaleXY)
	{
		auto M = [&P](EScatterParam Pa) { return static_cast<double>(P.Get(Pa)) / 65536.0; };
		const double S = HF.SampleSurface(X, Y);
		switch (Class)
		{
		case EScatterClass::Grass: return S - M(EScatterParam::GrassSinkM);
		case EScatterClass::Tussock: return S - M(EScatterParam::TussockSinkM);
		case EScatterClass::Flower: return S - M(EScatterParam::FlowerSinkM);
		case EScatterClass::NearCard: return S - M(EScatterParam::NearCardSinkM);
		case EScatterClass::Tree:
		case EScatterClass::Sapling:
		{
			const double D = M(EScatterParam::TreeBaseProbeM);
			double Z = S;
			Z = FMath::Min(Z, static_cast<double>(HF.SampleSurface(X + D, Y)));
			Z = FMath::Min(Z, static_cast<double>(HF.SampleSurface(X - D, Y)));
			Z = FMath::Min(Z, static_cast<double>(HF.SampleSurface(X, Y + D)));
			Z = FMath::Min(Z, static_cast<double>(HF.SampleSurface(X, Y - D)));
			return Z - M(EScatterParam::TreeSinkM);
		}
		case EScatterClass::Shrub: return S - M(EScatterParam::ShrubSinkM);
		case EScatterClass::Fern: return S - M(EScatterParam::FernSinkM);
		default: return S - M(EScatterParam::RockSinkFrac) * M(EScatterParam::RockNominalHM) * ScaleXY;
		}
	}

	/** SHA-256 of a byte array, lowercase hex (FIPS 180-4). FPlatformMisc::GetSHA256Signature has no Windows implementation in 5.8.3 (it asserts). */
	FString Sha256Hex(const TArray<uint8>& Data)
	{
		static const uint32 K[64] = {
			0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
			0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
			0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
			0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
			0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
			0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
		uint32 H[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
		auto Rotr = [](uint32 X, uint32 N) { return (X >> N) | (X << (32 - N)); };
		TArray<uint8> M = Data;
		const uint64 BitLen = static_cast<uint64>(Data.Num()) * 8ull;
		M.Add(0x80);
		while (M.Num() % 64 != 56)
		{
			M.Add(0);
		}
		for (int32 I = 7; I >= 0; --I)
		{
			M.Add(static_cast<uint8>((BitLen >> (I * 8)) & 0xFF));
		}
		for (int32 Off = 0; Off < M.Num(); Off += 64)
		{
			uint32 W[64];
			for (int32 T = 0; T < 16; ++T)
			{
				W[T] = (static_cast<uint32>(M[Off + 4 * T]) << 24) | (static_cast<uint32>(M[Off + 4 * T + 1]) << 16) | (static_cast<uint32>(M[Off + 4 * T + 2]) << 8) | M[Off + 4 * T + 3];
			}
			for (int32 T = 16; T < 64; ++T)
			{
				const uint32 S0 = Rotr(W[T - 15], 7) ^ Rotr(W[T - 15], 18) ^ (W[T - 15] >> 3);
				const uint32 S1 = Rotr(W[T - 2], 17) ^ Rotr(W[T - 2], 19) ^ (W[T - 2] >> 10);
				W[T] = W[T - 16] + S0 + W[T - 7] + S1;
			}
			uint32 A = H[0], B = H[1], C = H[2], D = H[3], E = H[4], F = H[5], G = H[6], Hh = H[7];
			for (int32 T = 0; T < 64; ++T)
			{
				const uint32 S1 = Rotr(E, 6) ^ Rotr(E, 11) ^ Rotr(E, 25);
				const uint32 Ch = (E & F) ^ (~E & G);
				const uint32 T1 = Hh + S1 + Ch + K[T] + W[T];
				const uint32 S0 = Rotr(A, 2) ^ Rotr(A, 13) ^ Rotr(A, 22);
				const uint32 Maj = (A & B) ^ (A & C) ^ (B & C);
				const uint32 T2 = S0 + Maj;
				Hh = G;
				G = F;
				F = E;
				E = D + T1;
				D = C;
				C = B;
				B = A;
				A = T1 + T2;
			}
			H[0] += A; H[1] += B; H[2] += C; H[3] += D; H[4] += E; H[5] += F; H[6] += G; H[7] += Hh;
		}
		FString Out;
		for (const uint32 V : H)
		{
			Out += FString::Printf(TEXT("%08x"), V);
		}
		return Out;
	}

	const TCHAR* const ScalabilityCvars[] = {TEXT("sg.ResolutionQuality"), TEXT("sg.ViewDistanceQuality"), TEXT("sg.AntiAliasingQuality"),
		TEXT("sg.ShadowQuality"), TEXT("sg.GlobalIlluminationQuality"), TEXT("sg.ReflectionQuality"), TEXT("sg.PostProcessQuality"),
		TEXT("sg.TextureQuality"), TEXT("sg.EffectsQuality"), TEXT("sg.FoliageQuality"), TEXT("sg.ShadingQuality"), TEXT("sg.LandscapeQuality")};
}

ATerrainScatter::ATerrainScatter()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.bStartWithTickEnabled = false;   // Setup enables the tick only when scatter is available; a dormant actor never ticks
	PrimaryActorTick.TickGroup = TG_PostUpdateWork;
	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	Root->SetMobility(EComponentMobility::Stationary);
	SetRootComponent(Root);
	SetReplicates(false);
}

void ATerrainScatter::Setup(ATerrainActor* InTerrain, const FScatterOptions& InOptions, bool bRequestedOnCommandLine)
{
	Terrain = InTerrain;
	Options = InOptions;
	bRequested = bRequestedOnCommandLine;
	bAvailable = Options.bEnabled && Terrain != nullptr && Terrain->GetHeightfield().IsInitialized();
	Palette = MakeShared<const FScatterPalette, ESPMode::ThreadSafe>(Options.Palette);
	LayerVisibleMask = Options.LayerMask;
	if (!Options.ParamsError.IsEmpty())
	{
		if (Options.bEnabled && bRequested)
		{
			UE_LOG(LogChimeraTerrain, Error, TEXT("scatter options: %s"), *Options.ParamsError);
		}
		else
		{
			UE_LOG(LogChimeraTerrain, Warning, TEXT("scatter options: %s"), *Options.ParamsError);
		}
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter: %s (requested=%d level=%s seed=%u config_fnv=%s threads=%d tiles=%d/%d apply=%s during_stroke=%d caster_during_stroke=%d budget=%.2f/%.2f governor=%d)"),
		bAvailable ? TEXT("on") : TEXT("dormant"), bRequested ? 1 : 0, Options.Palette.Level == EScatterLevel::L1 ? TEXT("L1") : TEXT("L0"), Options.Palette.Seed,
		*Hex64(Options.ConfigFnv()), Options.Threads, Options.FineTileM, Options.CoarseTileM, *Options.ApplyMode, Options.DuringStrokeMs, Options.CasterDuringStrokeMs,
		Options.BudgetMs, Options.LoadBudgetMs, Options.Governor);
	if (!bAvailable)
	{
		// Dormant: no tick, no subscription, no provider, no delegate, process-wide counters untouched.
		SetActorTickEnabled(false);
		return;
	}
	ScatterProxyCounters().Reset();
	ScatterResetProxyEvents();
#if WITH_EDITOR
	// -game in the editor build compiles shaders and assets on demand; each compiler marks the primitives it affects dirty and then
	// broadcasts, so the handlers attribute those recreates per component (counted apart from proxy_recreates; see FScatterProxyCounters).
	FEditorSupportDelegates::RedrawAllViewports.AddUObject(this, &ATerrainScatter::OnShaderPropagation);
	FAssetCompilingManager::Get().OnAssetPostCompileEvent().AddUObject(this, &ATerrainScatter::OnAssetPostCompile);
#endif
	SetActorTickEnabled(true);
	FScatterSchedulerConfig Cfg;
	Cfg.HalfExtentM = Terrain->GetHeightfield().HalfExtentM();
	Cfg.FineTileM = Options.FineTileM;
	Cfg.CoarseTileM = Options.CoarseTileM;
	Cfg.MaxInFlight = Options.Threads;
	Cfg.DuringStrokeMs = Options.DuringStrokeMs;
	Cfg.CasterDuringStrokeMs = Options.CasterDuringStrokeMs;
	// Predicted apply cost a + b * changes + c * instances_after (ms): option defaults (FScatterOptions::PredictA/B/C), timing only, not hashed
	// (plan C scatter 3.5). parse_terrain.py --scatter refits them on every run's apply_rows.
	Cfg.PredictA = Options.PredictA;
	Cfg.PredictB = Options.PredictB;
	Cfg.PredictC = Options.PredictC;
	Scheduler.Init(Cfg);
	FineUnits.SetNum(Scheduler.GetTileGrid(EScatterGrid::Fine).NumTiles() * ScatterMeshCountOfGrid(EScatterGrid::Fine));
	CoarseUnits.SetNum(Scheduler.GetTileGrid(EScatterGrid::Coarse).NumTiles() * ScatterMeshCountOfGrid(EScatterGrid::Coarse));
	Terrain->OnTerrainChanged.AddUObject(this, &ATerrainScatter::OnTerrainChanged);
	TWeakObjectPtr<ATerrainScatter> Weak(this);
	Terrain->AddPendingWorkProvider([Weak]() { return Weak.IsValid() && Weak->HasPendingWork(); });
	Enable();
}

void ATerrainScatter::OnShaderPropagation()
{
	ScatterProxyCounters().ShaderPropagations.fetch_add(1);
	ScatterLogProxyEvent(EScatterProxyEvent::ShaderPropagation);
	NoteCompilePropagationAll();
}

void ATerrainScatter::OnAssetPostCompile(const TArray<FAssetCompileData>& Assets)
{
	ScatterProxyCounters().AssetPostCompiles.fetch_add(1);
	ScatterLogProxyEvent(EScatterProxyEvent::AssetPostCompile);
	NoteCompilePropagationAll();
}

void ATerrainScatter::NoteCompilePropagationAll()
{
	// The compiler marked its primitives dirty just before broadcasting; the deferred recreate runs at the end of the frame, so a scatter
	// component that is render-state dirty now was marked by that compiler (or earlier this frame by scatter's own apply, which outranks it).
	for (TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
	{
		for (UChimeraScatterISM* C : *A)
		{
			if (C)
			{
				C->NoteCompilePropagation();
			}
		}
	}
}

void ATerrainScatter::SetDirector(AActor* Director)
{
	if (Director)
	{
		AddTickPrerequisiteActor(Director);
	}
}

UChimeraScatterISM* ATerrainScatter::GetUnit(EScatterGrid Grid, int32 TileIndex, int32 MeshIndexInGrid) const
{
	const TArray<TObjectPtr<UChimeraScatterISM>>& A = Grid == EScatterGrid::Fine ? FineUnits : CoarseUnits;
	const int32 I = TileIndex * ScatterMeshCountOfGrid(Grid) + MeshIndexInGrid;
	return A.IsValidIndex(I) ? A[I].Get() : nullptr;
}

void ATerrainScatter::LoadMeshes()
{
	Meshes.SetNum(ScatterMeshCount);
	MeshPaths.SetNum(ScatterMeshCount);
	MeshSha256.SetNum(ScatterMeshCount);
	MeshErrors.Reset();
	const EScatterLevel Level = Options.Palette.Level;
	for (int32 M = 0; M < ScatterMeshCount; ++M)
	{
		const EScatterMesh Mesh = static_cast<EScatterMesh>(M);
		Meshes[M] = nullptr;
		MeshPaths[M].Reset();
		MeshSha256[M].Reset();
		if (!ScatterMeshExistsAtLevel(Mesh, Level))
		{
			continue;
		}
		FString Slot = UTF8_TO_TCHAR(ScatterMeshName(Mesh));
		if (IsGrassMesh(Mesh) && !Options.bGrassNanite)
		{
			// The non-Nanite LOD-chain A/B arm built by S3 (GrassT0_L, GrassT1_L).
			Slot += TEXT("_L");
		}
		// L1 uses its own mesh where S3 imported one for the slot; the procedural L0 mesh is the slot's L1 choice otherwise (plan C scatter 3.6).
		TArray<FString> Candidates;
		if (Level == EScatterLevel::L1 && !(IsGrassMesh(Mesh) && !Options.bGrassNanite))
		{
			Candidates.Add(FString::Printf(TEXT("/Game/Terrain/Scatter/Meshes/L1/%s"), *Slot));
		}
		Candidates.Add(FString::Printf(TEXT("/Game/Terrain/Scatter/Meshes/L0/%s"), *Slot));
		for (const FString& Package : Candidates)
		{
			FString File;
			if (!FPackageName::DoesPackageExist(Package, &File))
			{
				continue;
			}
			const FString ObjectPath = Package + TEXT(".") + FPackageName::GetShortName(Package);
			UStaticMesh* Loaded = LoadObject<UStaticMesh>(nullptr, *ObjectPath, nullptr, LOAD_Quiet | LOAD_NoWarn);
			if (!Loaded)
			{
				continue;
			}
			Meshes[M] = Loaded;
			MeshPaths[M] = ObjectPath;
			TArray<uint8> Bytes;
			if (!File.IsEmpty() && FFileHelper::LoadFileToArray(Bytes, *File, FILEREAD_Silent))
			{
				MeshSha256[M] = Sha256Hex(Bytes);
			}
			else
			{
				MeshSha256[M] = TEXT("n/a");
			}
			break;
		}
		if (!Meshes[M])
		{
			const FString Msg = FString::Printf(TEXT("scatter: no mesh for class %s (slot %s; looked for %s); the slot is skipped"), MeshTagClass(Mesh), *Slot, *FString::Join(Candidates, TEXT(", ")));
			MeshErrors += Msg + TEXT("; ");
			if (bRequested)
			{
				UE_LOG(LogChimeraTerrain, Error, TEXT("%s"), *Msg);
			}
			else
			{
				UE_LOG(LogChimeraTerrain, Warning, TEXT("%s"), *Msg);
			}
		}
	}
}

void ATerrainScatter::UnitCullDistances(EScatterMesh Mesh, int32& OutStart, int32& OutEnd) const
{
	if (!IsMeshVisible(Mesh))
	{
		// Hidden by cull distance (F18: never SetVisibility); SetCullDistances updates the live proxy in place (F6).
		OutStart = 0;
		OutEnd = 1;
		return;
	}
	double End = MeshCullEndM(Mesh);
	if (Options.Governor >= 2)
	{
		End *= 0.8;
	}
	if (Mesh == EScatterMesh::Tussock && Options.Governor >= 4)
	{
		// Plan C scatter 3.7 step 4 names an absolute end ("tussock end 150 -> 110 m"), so it applies after step 2's scaling.
		End = 110.0;
	}
	double Start = 0.0;
	if (Mesh == EScatterMesh::NearCard)
	{
		Start = End * (22.0 / 32.0);
	}
	else if (IsGrassMesh(Mesh) && !Options.bGrassNanite)
	{
		// The non-Nanite arm fades (M_ScatterBladeFade reads the ISM fade).
		Start = End * 0.75;
	}
	OutStart = FMath::RoundToInt(Start * 100.0);
	OutEnd = FMath::RoundToInt(End * 100.0);
}

bool ATerrainScatter::IsMeshVisible(EScatterMesh Mesh) const
{
	if ((LayerVisibleMask & (1u << static_cast<uint32>(MeshLayer(Mesh)))) == 0)
	{
		return false;
	}
	if (Options.Governor >= 1 && Mesh == EScatterMesh::GrassT1)
	{
		return false;
	}
	if (Options.Governor >= 4 && Mesh == EScatterMesh::Flower)
	{
		return false;
	}
	return true;
}

void ATerrainScatter::CreateComponents()
{
	bComponentsCreated = true;
	if (!FApp::CanEverRender())
	{
		// nullrhi: the generator, hashes and tests run; no component is created (the splat texture's precedent).
		return;
	}
	EComponentMobility::Type Mobility = EComponentMobility::Stationary;
	if (Options.Mobility == TEXT("static"))
	{
		Mobility = EComponentMobility::Static;
	}
	else if (Options.Mobility == TEXT("movable"))
	{
		Mobility = EComponentMobility::Movable;
	}
	Root->SetMobility(Mobility);
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const FScatterTileGrid& TG = Scheduler.GetTileGrid(Grid);
		TArray<EScatterMesh> GridMeshes;
		ScatterMeshesOfGrid(Grid, GridMeshes);
		TArray<TObjectPtr<UChimeraScatterISM>>& Units = Grid == EScatterGrid::Fine ? FineUnits : CoarseUnits;
		for (int32 Tile = 0; Tile < TG.NumTiles(); ++Tile)
		{
			const double CX = (static_cast<double>(TG.TXOf(Tile)) + 0.5) * TG.SizeM;
			const double CY = (static_cast<double>(TG.TYOf(Tile)) + 0.5) * TG.SizeM;
			for (int32 K = 0; K < GridMeshes.Num(); ++K)
			{
				const EScatterMesh Mesh = GridMeshes[K];
				UStaticMesh* SM = Meshes.IsValidIndex(static_cast<int32>(Mesh)) ? Meshes[static_cast<int32>(Mesh)].Get() : nullptr;
				if (!SM)
				{
					continue;
				}
				const FName Name(*FString::Printf(TEXT("Scatter_%s_%d_%d"), UTF8_TO_TCHAR(ScatterMeshName(Mesh)), TG.TXOf(Tile), TG.TYOf(Tile)));
				UChimeraScatterISM* C = NewObject<UChimeraScatterISM>(this, Name);
				C->Grid = static_cast<uint8>(G);
				C->TileIndex = Tile;
				C->MeshSlot = static_cast<uint8>(Mesh);
				// Plan C scatter 3.7, every setting before RegisterComponent.
				C->SetMobility(Mobility);
				C->SetStaticMesh(SM);
				C->SetRemoveSwap();
				C->InstancingRandomSeed = 7919 + G * 100000 + Tile * 16 + K;   // fixed and non-zero (F3); no scatter material reads it (F25)
				C->SetNumCustomDataFloats(Grid == EScatterGrid::Fine ? 4 : 2);
				C->bDisableCollision = true;
				C->SetCollisionEnabled(ECollisionEnabled::NoCollision);
				C->SetCanEverAffectNavigation(false);
				C->SetGenerateOverlapEvents(false);
				C->bAffectDistanceFieldLighting = false;
				C->bAffectDynamicIndirectLighting = false;
				C->bReceivesDecals = false;
				C->bVisibleInRayTracing = false;
				C->bEvaluateWorldPositionOffset = false;
				C->NanitePixelProgrammableDistance = 0.0f;   // F19: masked leaves at full raster at every distance
				C->bUseGpuLodSelection = true;
				C->bDisallowNanite = IsGrassMesh(Mesh) && !Options.bGrassNanite;
				bool bCast = MeshCasts(Mesh);
				if (bCast && Grid == EScatterGrid::Coarse && Options.Governor >= 5 && FMath::Sqrt(CX * CX + CY * CY) > 200.0)
				{
					// Governor step 5: far caster units of the coarse grid cast no VSM shadow (set once, before the first fill).
					bCast = false;
				}
				C->CastShadow = bCast;
				C->bCastDynamicShadow = bCast;
				C->bUseAsOccluder = MeshCasts(Mesh);
				C->ComponentTags.Add(FName(*FString::Printf(TEXT("ChimeraScatter.%s"), MeshTagClass(Mesh))));
				int32 S0 = 0;
				int32 S1 = 0;
				UnitCullDistances(Mesh, S0, S1);
				C->InstanceStartCullDistance = S0;
				C->InstanceEndCullDistance = S1;
				C->SetupAttachment(Root);
				C->RegisterComponent();
				Units[Tile * GridMeshes.Num() + K] = C;
			}
		}
	}
}

void ATerrainScatter::ApplyGovernorCvars()
{
	GovernorReadBack.Reset();
	auto SetCvar = [this](const TCHAR* Name, const FString& Value)
	{
		IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(Name);
		if (!Cv)
		{
			GovernorReadBack.Add(TPair<FString, FString>(Name, TEXT("missing")));
			return;
		}
		bool bKnown = false;
		for (const TPair<FString, FString>& R : GovernorRestore)
		{
			bKnown |= R.Key == Name;
		}
		if (!bKnown)
		{
			GovernorRestore.Add(TPair<FString, FString>(Name, Cv->GetString()));
		}
		Cv->Set(*Value, ECVF_SetByCode);
		GovernorReadBack.Add(TPair<FString, FString>(Name, Cv->GetString()));
	};
	if (Options.Governor >= 3)
	{
		SetCvar(TEXT("r.Nanite.MaxPixelsPerEdge"), TEXT("2"));
	}
	if (Options.Governor >= 6)
	{
		IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(TEXT("r.Shadow.Virtual.ResolutionLodBiasDirectional"));
		FString Base = Cv ? Cv->GetString() : FString(TEXT("0"));
		for (const TPair<FString, FString>& R : GovernorRestore)
		{
			if (R.Key == TEXT("r.Shadow.Virtual.ResolutionLodBiasDirectional"))
			{
				Base = R.Value;
			}
		}
		SetCvar(TEXT("r.Shadow.Virtual.ResolutionLodBiasDirectional"), FString::SanitizeFloat(FCString::Atof(*Base) + 1.0f));
	}
}

void ATerrainScatter::RestoreGovernorCvars()
{
	for (const TPair<FString, FString>& R : GovernorRestore)
	{
		if (IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(*R.Key))
		{
			Cv->Set(*R.Value, ECVF_SetByCode);
		}
	}
	GovernorRestore.Reset();
}

void ATerrainScatter::Enable()
{
	if (!bAvailable || bEnabledNow)
	{
		return;
	}
	if (!bComponentsCreated)
	{
		// Under nullrhi nothing is loaded and no component is created (plan C scatter 3.5); the generator and hashes still run.
		if (FApp::CanEverRender())
		{
			LoadMeshes();
		}
		CreateComponents();
	}
	else
	{
		// Re-enable after scatter 0: each component that had a proxy gets a new one at its refill (scatter's own rebuild).
		for (TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
		{
			for (UChimeraScatterISM* C : *A)
			{
				if (C && C->GetProxyCreates() > 0)
				{
					C->ExpectRebuild();
				}
			}
		}
	}
	ApplyGovernorCvars();
	Scheduler.BumpEpochAndDirtyAll();
	bEnabledNow = true;
	bFill = true;
	FillStartSeconds = FPlatformTime::Seconds();
	++Enables;
	StartLatency(TEXT("enable"));
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter enable #%lld epoch=%u units=%d meshes_missing='%s'"), Enables, Scheduler.GetEpoch(), FineUnits.Num() + CoarseUnits.Num(), *MeshErrors);
}

void ATerrainScatter::Disable()
{
	if (!bEnabledNow)
	{
		return;
	}
	Scheduler.Disable();
	Scheduler.ClearUnitStates();
	for (TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
	{
		for (UChimeraScatterISM* C : *A)
		{
			if (C && C->GetInstanceCount() > 0)
			{
				C->ClearInstances();
			}
		}
	}
	RestoreGovernorCvars();
	bEnabledNow = false;
	bFill = false;
	++Disables;
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter disable #%lld epoch=%u in_flight=%d busy=%d"), Disables, Scheduler.GetEpoch(), Scheduler.NumInFlight(), Scheduler.NumBusy());
}

void ATerrainScatter::StartLatency(const TCHAR* Reason)
{
	FScatterLatencyRow R;
	R.Reason = Reason;
	R.Id = ++LatencyId;
	R.StartFrame = GFrameCounter;
	R.StartSeconds = FPlatformTime::Seconds();
	Latencies.Add(R);
}

void ATerrainScatter::OnTerrainChanged(const FTerrainChange& Change)
{
	const int32 K = static_cast<int32>(Change.Kind);
	if (K >= 0 && K < 8)
	{
		++EventsSeen[K];
	}
	if (!bEnabledNow || !Terrain)
	{
		return;
	}
	const uint64 T0 = FPlatformTime::Cycles64();
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	switch (Change.Kind)
	{
	case ETerrainChangeKind::Tick:
		// Each tick's own rects (never the stroke's union box, r8d 4.3).
		Scheduler.MarkRects(Change.HeightRect, Change.SplatRect);
		break;
	case ETerrainChangeKind::StrokeEnd:
		// Every tick already marked its tiles; stroke end only releases the throttled and coarse tiles (the scheduler reads IsStrokeOpen).
		StartLatency(TEXT("stroke_end"));
		break;
	case ETerrainChangeKind::Undo:
	case ETerrainChangeKind::Redo:
		if (Change.Chunks.Num() > 0)
		{
			for (const int32 Id : Change.Chunks)
			{
				Scheduler.MarkRects(HF.ChunkOwnedRect(Id), HF.ChunkOwnedSplatRect(Id));
			}
		}
		else
		{
			Scheduler.MarkRects(Change.HeightRect, Change.SplatRect);
		}
		StartLatency(Change.Kind == ETerrainChangeKind::Undo ? TEXT("undo") : TEXT("redo"));
		break;
	case ETerrainChangeKind::Load:
	case ETerrainChangeKind::Init:
		Scheduler.BumpEpochAndDirtyAll();
		bFill = true;
		FillStartSeconds = FPlatformTime::Seconds();
		Loads += Change.Kind == ETerrainChangeKind::Load ? 1 : 0;
		StartLatency(Change.Kind == ETerrainChangeKind::Load ? TEXT("load") : TEXT("init"));
		break;
	default:
		break;
	}
	const double Us = FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0) * 1000.0;
	MarkUsTotal += Us;
	MarkUsMax = FMath::Max(MarkUsMax, Us);
	++MarkCount;
}

void ATerrainScatter::Launch(const FScatterDispatch& D)
{
	const FScatterTileKey Key = Scheduler.GetTileKey(D.Grid, D.TileIndex);
	TSharedPtr<const FScatterSnapshot, ESPMode::ThreadSafe> Snap = MakeShared<const FScatterSnapshot, ESPMode::ThreadSafe>(MakeSnapshot(Terrain->GetHeightfield(), Key));
	TArray<EScatterMesh> GridMeshes;
	ScatterMeshesOfGrid(D.Grid, GridMeshes);
	TArray<TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe>> Olds;
	for (int32 K = 0; K < GridMeshes.Num(); ++K)
	{
		Olds.Add(Scheduler.GetUnitState(D.Grid, D.TileIndex, K));
	}
	TSharedPtr<const FScatterPalette, ESPMode::ThreadSafe> Pal = Palette;
	const bool bFull = Options.ApplyMode == TEXT("clear");
	FScatterScheduler* Sched = &Scheduler;
	FCriticalSection* Lock = &GenLock;
	TArray<double>* GenOut = &GenMs;
	auto Job = [D, Key, Snap, Olds = MoveTemp(Olds), GridMeshes, Pal, bFull, Sched, Lock, GenOut]()
	{
		const uint64 T0 = FPlatformTime::Cycles64();
		FScatterTileRecords Recs;
		EvaluateTile(*Snap, *Pal, Key, Recs);
		FScatterTileResult R;
		R.Grid = D.Grid;
		R.TileIndex = D.TileIndex;
		R.Gen = D.Gen;
		R.Epoch = D.Epoch;
		const FScatterUnitState Empty;
		for (int32 K = 0; K < GridMeshes.Num(); ++K)
		{
			const EScatterMesh Mesh = GridMeshes[K];
			if (!ScatterMeshExistsAtLevel(Mesh, Pal->Level))
			{
				continue;
			}
			const FScatterUnitState& Old = Olds[K].IsValid() ? *Olds[K] : Empty;
			FScatterUnitWork W;
			W.Mesh = static_cast<uint8>(Mesh);
			W.InstancesBefore = Old.Records.Num();
			W.Script = BuildEditScript(Old, Recs.PerMesh[static_cast<int32>(Mesh)], W.Next);
			if (!W.Script.IsEmpty())
			{
				TSharedPtr<FScatterUnitPrepared, ESPMode::ThreadSafe> Prep = MakeShared<FScatterUnitPrepared, ESPMode::ThreadSafe>();
				PrepareScatterUnit(W, *Pal, bFull, *Prep);
				W.Prepared = Prep;
			}
			R.Units.Add(MoveTemp(W));
		}
		const double Ms = FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0);
		{
			FScopeLock L(Lock);
			GenOut->Add(Ms);
		}
		Sched->PostResult(MoveTemp(R));
	};
	if (D.bGameThread)
	{
		// ScatterThreads=0: the determinism path, generated inline on the game thread (never counted by SX11 or SX12).
		Job();
	}
	else
	{
		Tasks.Add(UE::Tasks::Launch(TEXT("ChimeraScatterTile"), MoveTemp(Job), UE::Tasks::ETaskPriority::BackgroundNormal));
	}
}

void ATerrainScatter::ApplyUnit(EScatterGrid Grid, int32 TileIndex, const FScatterUnitWork& Unit)
{
	const uint64 T0 = FPlatformTime::Cycles64();
	UChimeraScatterISM* C = GetUnit(Grid, TileIndex, ScatterMeshIndexInGrid(static_cast<EScatterMesh>(Unit.Mesh)));
	if (C && Unit.Prepared.IsValid())
	{
		// Launch is the only producer of payloads, and it always builds an FScatterUnitPrepared.
		C->ApplyScript(Unit.Script, static_cast<const FScatterUnitPrepared&>(*Unit.Prepared));
	}
	const double Ms = FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0);
	FScatterApplyRow Row;
	Row.Ms = Ms;
	Row.PredictedMs = Scheduler.PredictUnitMs(Unit);
	Row.Changes = Unit.Script.Changes();
	Row.Updates = Unit.Script.Updates.Num();
	Row.Removes = Unit.Script.Removes.Num();
	Row.Appends = Unit.Script.Appends.Num();
	Row.InstancesAfter = Unit.InstancesAfter();
	Row.Mesh = Unit.Mesh;
	Row.bFill = bFill;
	if (ApplyRows.Num() < MaxSeriesRows)
	{
		ApplyRows.Add(Row);
	}
	else
	{
		++ApplyRowsDropped;
	}
	LastApplyMs = Ms;
	++UnitsAppliedThisFrame;
}

void ATerrainScatter::PruneTasks(bool bWaitAll)
{
	if (bWaitAll)
	{
		for (const UE::Tasks::FTask& T : Tasks)
		{
			T.Wait();
		}
		Tasks.Reset();
		return;
	}
	Tasks.RemoveAll([](const UE::Tasks::FTask& T) { return T.IsCompleted(); });
}

bool ATerrainScatter::AnyComponentAwaitingProxy() const
{
	for (const TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
	{
		for (const UChimeraScatterISM* C : *A)
		{
			if (C && C->IsRegistered() && C->GetInstanceCount() > 0 && !C->HasLiveProxy() && C->ShouldComponentAddToScene())
			{
				return true;
			}
		}
	}
	return false;
}

bool ATerrainScatter::HasPendingWork() const
{
	if (!bAvailable)
	{
		return false;
	}
	if (Scheduler.HasPendingWork() || bAppliedLastFrame)
	{
		return true;
	}
	return bEnabledNow && AnyComponentAwaitingProxy();
}

void ATerrainScatter::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	StepFrame();
}

void ATerrainScatter::StepFrame()
{
	if (!bAvailable || !Terrain)
	{
		return;
	}
	const uint64 T0 = FPlatformTime::Cycles64();
	FScatterProxyCounters& PC = ScatterProxyCounters();
	// The flush timed now ran at the end of the previous frame: it belongs to that frame's row (its applies, its fill flag).
	const uint64 FlushCycles = PC.FlushCycles.exchange(0);
	const int64 FlushCalls = PC.FlushCalls.exchange(0);
	if (FrameRows.Num() > 0)
	{
		FrameRows.Last().FlushMs = FPlatformTime::ToMilliseconds64(FlushCycles);
		FrameRows.Last().FlushCalls = static_cast<int32>(FlushCalls);
	}
	UnitsAppliedThisFrame = 0;
	// Editor asset and shader compilation outstanding this frame (the director's compile-idle test, ShaderCompiler.h, AssetCompilingManager.h): reported.
	const bool bShadersIdle = GShaderCompilingManager == nullptr || GShaderCompilingManager->GetNumRemainingJobs() == 0;
	if (!bShadersIdle || FAssetCompilingManager::Get().GetNumRemainingAssets() > 0)
	{
		PC.CompileBusyFrames.fetch_add(1);
		ScatterLogProxyEvent(EScatterProxyEvent::CompileBusyFrame);
	}

	Scheduler.SetStrokeOpen(Terrain->IsStrokeOpen());
	Scheduler.PollResults();
	PruneTasks(false);
	double Budget = bFill ? Options.LoadBudgetMs : Options.BudgetMs;
	if (Options.Threads == 0)
	{
		Budget = 0.0;   // the determinism path applies everything ready (one tile per frame is generated)
	}
	Scheduler.ApplyReady(Budget, []() { return FPlatformTime::Seconds() * 1000.0; },
		[this](EScatterGrid Grid, int32 TileIndex, const FScatterUnitWork& Unit) { ApplyUnit(Grid, TileIndex, Unit); });
	if (bEnabledNow)
	{
		const FVector2D Brush = Terrain->GetBrushRingCenter();
		Scheduler.Dispatch(FPlatformTime::Seconds() * 1000.0, Brush.X, Brush.Y, [this](const FScatterDispatch& D) { Launch(D); });
	}
	bAppliedLastFrame = UnitsAppliedThisFrame > 0;

	const bool bPending = Scheduler.HasPendingWork() || bAppliedLastFrame || (bEnabledNow && AnyComponentAwaitingProxy());
	PC.bEditsActive.store(Terrain->IsStrokeOpen() || (bPending && !bFill));
	if (!bPending)
	{
		if (bFill)
		{
			const double Ms = (FPlatformTime::Seconds() - FillStartSeconds) * 1000.0;
			if (InitMs < 0.0)
			{
				InitMs = Ms;
			}
			FillMs.Add(Ms);
			bFill = false;
			ClearExpectedRebuilds();
			UE_LOG(LogChimeraTerrain, Display, TEXT("scatter fill done in %.0f ms: %lld instances"), Ms, TotalInstances());
		}
		for (FScatterLatencyRow& L : Latencies)
		{
			if (L.Frames < 0)
			{
				L.Frames = static_cast<int32>(GFrameCounter - L.StartFrame);
				L.Ms = (FPlatformTime::Seconds() - L.StartSeconds) * 1000.0;
			}
		}
	}

	FScatterFrameRow Row;
	Row.Frame = GFrameCounter;
	const FString Phase = Terrain->GetPhase();
	Row.Phase = Terrain->GetPhaseNames().IndexOfByKey(Phase);
	Row.bFill = bFill;
	Row.Ready = Scheduler.NumReady();
	Row.InFlight = Scheduler.NumInFlight();
	Row.Busy = Scheduler.NumBusy();
	Row.UnitsApplied = UnitsAppliedThisFrame;
	Row.GtMs = FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0);
	if (FrameRows.Num() < MaxSeriesRows)
	{
		FrameRows.Add(Row);
	}
	else
	{
		++FrameRowsDropped;
	}
}

void ATerrainScatter::SetLayersVisible(uint32 Mask, bool bVisible)
{
	if (bVisible)
	{
		// Restore only layers the run shows (-ChimeraTerrainScatterLayers): scatter_visible never shows a layer the option hid.
		LayerVisibleMask |= (Mask & Options.LayerMask);
	}
	else
	{
		LayerVisibleMask &= ~Mask;
	}
	ApplyCullDistancesAll();
}

void ATerrainScatter::ApplyCullDistancesAll()
{
	for (TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
	{
		for (UChimeraScatterISM* C : *A)
		{
			if (C)
			{
				int32 S0 = 0;
				int32 S1 = 0;
				UnitCullDistances(static_cast<EScatterMesh>(C->MeshSlot), S0, S1);
				C->SetCullDistances(S0, S1);
			}
		}
	}
}

FScatterHash ATerrainScatter::ReferenceHashNow() const
{
	return ReferenceHash(Terrain->GetHeightfield(), *Palette, Options.FineTileM, Options.CoarseTileM, true);
}

uint64 ATerrainScatter::GetConfigFnv() const
{
	return Palette.IsValid() ? Palette->ConfigFnv() : 0;
}

uint64 ATerrainScatter::ScatterFnvOf(const FScatterHash& H) const
{
	return ScatterFnv(GetConfigFnv(), Terrain ? Terrain->GetHeightfield().HalfExtentM() : 0, Options.Palette.Level, H);
}

void ATerrainScatter::AddHashFields(FJsonObject& Out) const
{
	if (!bEnabledNow || !Terrain)
	{
		return;
	}
	const uint64 T0 = FPlatformTime::Cycles64();
	const FScatterHash Ref = ReferenceHashNow();
	const double RefMs = FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0);
	const FScatterHash Live = LiveHash();
	Out.SetStringField(TEXT("scatter_fnv"), Hex64(ScatterFnvOf(Ref)));
	Out.SetStringField(TEXT("scatter_live_fnv"), Hex64(ScatterFnvOf(Live)));
	Out.SetNumberField(TEXT("scatter_count"), static_cast<double>(Ref.TotalCount()));
	Out.SetNumberField(TEXT("scatter_live_count"), static_cast<double>(Live.TotalCount()));
	Out.SetNumberField(TEXT("scatter_reference_ms"), RefMs);
	Out.SetBoolField(TEXT("scatter_pending"), HasPendingWork());
	TSharedRef<FJsonObject> Cls = MakeShared<FJsonObject>();
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		Cls->SetNumberField(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C))), static_cast<double>(Ref.Classes[C].Count));
	}
	Out.SetObjectField(TEXT("scatter_classes"), Cls);
}

int32 ATerrainScatter::RebuildAllProxies()
{
	int32 N = 0;
	for (TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
	{
		for (UChimeraScatterISM* C : *A)
		{
			if (C && C->HasLiveProxy())
			{
				// Only a component that still holds instances gets a proxy back (an empty ISM creates none, F3), so only those are expected rebuilds.
				const bool bGetsProxy = C->GetInstanceCount() > 0;
				if (bGetsProxy)
				{
					C->ExpectRebuild();
				}
				C->MarkRenderStateDirty();
				N += bGetsProxy ? 1 : 0;
			}
		}
	}
	return N;
}

void ATerrainScatter::ClearExpectedRebuilds()
{
	for (TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
	{
		for (UChimeraScatterISM* C : *A)
		{
			if (C)
			{
				C->ClearExpectedRebuilds();
			}
		}
	}
}

int64 ATerrainScatter::TotalInstances() const
{
	// Record counts only (no hashing): cheap enough for the HUD every frame.
	int64 N = 0;
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const int32 NM = ScatterMeshCountOfGrid(Grid);
		for (int32 Tile = 0; Tile < Scheduler.GetTileGrid(Grid).NumTiles(); ++Tile)
		{
			for (int32 K = 0; K < NM; ++K)
			{
				const TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> St = Scheduler.GetUnitState(Grid, Tile, K);
				N += St.IsValid() ? St->Records.Num() : 0;
			}
		}
	}
	return N;
}

TSharedRef<FJsonObject> ATerrainScatter::Verify(const FString& Name, bool& bOutPass, const TArray<FScatterTarget>* Targets)
{
	TSharedRef<FJsonObject> V = MakeShared<FJsonObject>();
	V->SetStringField(TEXT("name"), Name);
	V->SetNumberField(TEXT("frame"), static_cast<double>(GFrameCounter));
	TArray<FString> Fails;
	if (!bEnabledNow || !Terrain)
	{
		V->SetBoolField(TEXT("pass"), false);
		V->SetStringField(TEXT("fail"), TEXT("scatter is not enabled"));
		bOutPass = false;
		return V;
	}
	const uint64 T0 = FPlatformTime::Cycles64();
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	const FScatterPalette& P = *Palette;

	// 1. live == reference.
	const FScatterHash Ref = ReferenceHashNow();
	const FScatterHash Live = LiveHash();
	const uint64 RefFnv = ScatterFnvOf(Ref);
	const uint64 LiveFnv = ScatterFnvOf(Live);
	V->SetStringField(TEXT("scatter_fnv"), Hex64(RefFnv));
	V->SetStringField(TEXT("scatter_live_fnv"), Hex64(LiveFnv));
	V->SetNumberField(TEXT("scatter_count"), static_cast<double>(Ref.TotalCount()));
	V->SetNumberField(TEXT("scatter_live_count"), static_cast<double>(Live.TotalCount()));
	TSharedRef<FJsonObject> Cls = MakeShared<FJsonObject>();
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		TSharedRef<FJsonObject> One = MakeShared<FJsonObject>();
		One->SetNumberField(TEXT("reference"), static_cast<double>(Ref.Classes[C].Count));
		One->SetNumberField(TEXT("live"), static_cast<double>(Live.Classes[C].Count));
		Cls->SetObjectField(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C))), One);
	}
	V->SetObjectField(TEXT("classes"), Cls);
	const bool bLiveEq = LiveFnv == RefFnv && Live == Ref;
	V->SetBoolField(TEXT("live_equals_reference"), bLiveEq);
	if (!bLiveEq)
	{
		Fails.Add(TEXT("live fold differs from the reference"));
	}
	// 2. no tile dirty or Busy, nothing waiting to apply.
	V->SetNumberField(TEXT("dirty"), Scheduler.NumDirty());
	V->SetNumberField(TEXT("busy"), Scheduler.NumBusy());
	V->SetNumberField(TEXT("ready"), Scheduler.NumReady());
	if (Scheduler.HasPendingWork())
	{
		Fails.Add(TEXT("tiles dirty, Busy or waiting"));
	}

	// 3. readback == BuildInstance of the unit states; 4. z and up axis from HF, independently of the records' integers.
	constexpr double TolCm = 0.01;
	constexpr double TolRad = 1.0e-4;
	constexpr double TolUnit = 1.0 / 65536.0;
	constexpr double TolZM = 0.001;
	constexpr double TolUpRad = 1.0e-3;
	int64 Instances = 0;
	int64 UnitsChecked = 0;
	int64 UnitsMissing = 0;
	int64 CountMismatch = 0;
	int64 BadTransform = 0;
	int64 BadCustom = 0;
	int64 BadZ = 0;
	int64 BadUp = 0;
	double MaxDPos = 0.0;
	double MaxDRot = 0.0;
	double MaxDScale = 0.0;
	double MaxDCustom = 0.0;
	double MaxDZ = 0.0;
	double MaxDUp = 0.0;
	TArray<FString> Examples;
	const bool bComponents = FApp::CanEverRender();
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const int32 NM = ScatterMeshCountOfGrid(Grid);
		const int32 NC = Grid == EScatterGrid::Fine ? 4 : 2;
		for (int32 Tile = 0; Tile < Scheduler.GetTileGrid(Grid).NumTiles(); ++Tile)
		{
			for (int32 K = 0; K < NM; ++K)
			{
				const TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> St = Scheduler.GetUnitState(Grid, Tile, K);
				const int32 N = St.IsValid() ? St->Records.Num() : 0;
				UChimeraScatterISM* C = GetUnit(Grid, Tile, K);
				if (!C)
				{
					if (N > 0 && bComponents)
					{
						++UnitsMissing;
					}
					continue;
				}
				++UnitsChecked;
				if (C->GetInstanceCount() != N || C->PerInstanceSMCustomData.Num() != N * NC)
				{
					++CountMismatch;
					if (Examples.Num() < 8)
					{
						Examples.Add(FString::Printf(TEXT("%s: %d instances, state %d"), *C->GetName(), C->GetInstanceCount(), N));
					}
					continue;
				}
				for (int32 I = 0; I < N; ++I)
				{
					const FScatterRecord& R = St->Records[I];
					const FScatterInstance BI = BuildInstance(R, P);
					const FTransform Want = ScatterInstanceTransform(BI);
					FTransform Got;
					C->GetInstanceTransform(I, Got, false);
					++Instances;
					const double DPos = (Got.GetTranslation() - Want.GetTranslation()).GetAbsMax();
					const double Dot = FMath::Min(1.0, FMath::Abs(Got.GetRotation() | Want.GetRotation()));
					const double DRot = 2.0 * std::acos(Dot);
					const double DScale = (Got.GetScale3D() - Want.GetScale3D()).GetAbsMax();
					MaxDPos = FMath::Max(MaxDPos, DPos);
					MaxDRot = FMath::Max(MaxDRot, DRot);
					MaxDScale = FMath::Max(MaxDScale, DScale);
					if (DPos > TolCm || DRot > TolRad || DScale > TolUnit)
					{
						++BadTransform;
						if (Examples.Num() < 8)
						{
							Examples.Add(FString::Printf(TEXT("%s[%d]: dpos %.5f cm drot %.2e rad dscale %.2e"), *C->GetName(), I, DPos, DRot, DScale));
						}
					}
					double DC = 0.0;
					for (int32 Q = 0; Q < NC; ++Q)
					{
						DC = FMath::Max(DC, static_cast<double>(FMath::Abs(C->PerInstanceSMCustomData[I * NC + Q] - BI.Custom[Q])));
					}
					MaxDCustom = FMath::Max(MaxDCustom, DC);
					if (DC > TolUnit)
					{
						++BadCustom;
					}
					// Independent of the record's integers: the drawn instance against HF's float surface.
					const FVector PosM = Got.GetTranslation() / 100.0;
					const EScatterClass Class = static_cast<EScatterClass>(R.Class);
					const double DZ = FMath::Abs(PosM.Z - RuleZ(HF, P, Class, PosM.X, PosM.Y, Got.GetScale3D().X));
					MaxDZ = FMath::Max(MaxDZ, DZ);
					if (DZ > TolZM)
					{
						++BadZ;
						if (Examples.Num() < 8)
						{
							Examples.Add(FString::Printf(TEXT("%s[%d] %s: z %.4f m vs rule %.4f m"), *C->GetName(), I, UTF8_TO_TCHAR(ScatterClassName(Class)), PosM.Z,
								RuleZ(HF, P, Class, PosM.X, PosM.Y, Got.GetScale3D().X)));
						}
					}
					const FVector UpGot = Got.GetRotation().RotateVector(FVector(0.0, 0.0, 1.0));
					// The record's exact position, not the instance's float32-cm translation (about 2e-5 m of rounding at 160 m, enough to move a position that
					// lies within that distance of a cell edge or the diagonal onto the other triangle). A position on an edge or the diagonal may still
					// round to either triangle: take the closer of the nearby samples.
					const double RX = static_cast<double>(R.XQ) / 65536.0;
					const double RY = static_cast<double>(R.YQ) / 65536.0;
					double DUp = 1.0e30;
					static const double Off[5][2] = {{0.0, 0.0}, {1e-6, 0.0}, {-1e-6, 0.0}, {0.0, 1e-6}, {0.0, -1e-6}};
					FVector BestWant = FVector::ZeroVector;
					for (const auto& O : Off)
					{
						// The full axis (plan C scatter 3.8): every class, rocks included.
						const FVector UpWant = RuleUpAxis(P, Class, HfCellNormal(HF, RX + O[0], RY + O[1]), R.Tilt, R.Yaw);
						const double Ang = std::acos(FMath::Clamp(UpGot | UpWant, -1.0, 1.0));
						if (Ang < DUp)
						{
							DUp = Ang;
							BestWant = UpWant;
						}
					}
					MaxDUp = FMath::Max(MaxDUp, DUp);
					if (DUp > TolUpRad)
					{
						++BadUp;
						if (Examples.Num() < 8)
						{
							Examples.Add(FString::Printf(TEXT("%s[%d] %s: up axis off by %.2e rad (tilt %u yaw %u, gradient %d,%d, drawn up %.5f %.5f %.5f, rule up %.5f %.5f %.5f)"),
								*C->GetName(), I, UTF8_TO_TCHAR(ScatterClassName(Class)), DUp, R.Tilt, R.Yaw, R.GXQ, R.GYQ, UpGot.X, UpGot.Y, UpGot.Z,
								BestWant.X, BestWant.Y, BestWant.Z));
						}
					}
				}
			}
		}
	}
	if (UnitsMissing > 0)
	{
		Fails.Add(FString::Printf(TEXT("%lld units hold records but have no component"), UnitsMissing));
	}
	if (CountMismatch > 0)
	{
		Fails.Add(FString::Printf(TEXT("%lld units: instance count differs from the unit state"), CountMismatch));
	}
	if (BadTransform > 0)
	{
		Fails.Add(FString::Printf(TEXT("%lld instances: transform differs from BuildInstance"), BadTransform));
	}
	if (BadCustom > 0)
	{
		Fails.Add(FString::Printf(TEXT("%lld instances: custom data differs"), BadCustom));
	}
	if (BadZ > 0)
	{
		Fails.Add(FString::Printf(TEXT("%lld instances: z off the class rule on HF by > 1 mm"), BadZ));
	}
	if (BadUp > 0)
	{
		Fails.Add(FString::Printf(TEXT("%lld instances: up axis off the HF cell-triangle rule by > 1e-3 rad"), BadUp));
	}
	if (bComponents && Live.TotalCount() > 0 && Instances != Live.TotalCount())
	{
		Fails.Add(FString::Printf(TEXT("instances read back %lld != live records %lld"), Instances, Live.TotalCount()));
	}
	V->SetBoolField(TEXT("components"), bComponents);
	V->SetNumberField(TEXT("units_checked"), static_cast<double>(UnitsChecked));
	V->SetNumberField(TEXT("instances_read_back"), static_cast<double>(Instances));
	V->SetNumberField(TEXT("count_mismatch_units"), static_cast<double>(CountMismatch));
	V->SetNumberField(TEXT("bad_transform"), static_cast<double>(BadTransform));
	V->SetNumberField(TEXT("bad_custom"), static_cast<double>(BadCustom));
	V->SetNumberField(TEXT("bad_z"), static_cast<double>(BadZ));
	V->SetNumberField(TEXT("bad_up"), static_cast<double>(BadUp));
	V->SetNumberField(TEXT("max_dpos_cm"), MaxDPos);
	V->SetNumberField(TEXT("max_drot_rad"), MaxDRot);
	V->SetNumberField(TEXT("max_dscale"), MaxDScale);
	V->SetNumberField(TEXT("max_dcustom"), MaxDCustom);
	V->SetNumberField(TEXT("max_dz_m"), MaxDZ);
	V->SetNumberField(TEXT("max_dup_rad"), MaxDUp);
	TArray<TSharedPtr<FJsonValue>> Ex;
	for (const FString& E : Examples)
	{
		Ex.Add(MakeShared<FJsonValueString>(E));
	}
	V->SetArrayField(TEXT("examples"), Ex);
	if (Targets && Targets->Num() > 0)
	{
		// scatter_target rows: does each stored key still exist, and how far did its z move (SHADX's precondition, SX10).
		TMap<uint64, FScatterRecord> ByKey;
		TArray<FScatterRecord> All;
		CollectLive(All);
		for (const FScatterRecord& R : All)
		{
			ByKey.Add(R.Key(), R);
		}
		TArray<TSharedPtr<FJsonValue>> Tj;
		for (const FScatterTarget& T : *Targets)
		{
			TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
			J->SetStringField(TEXT("name"), T.Name);
			J->SetStringField(TEXT("key"), Hex64(T.Key));
			J->SetStringField(TEXT("class"), UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(T.Class))));
			const FScatterRecord* R = ByKey.Find(T.Key);
			J->SetBoolField(TEXT("exists"), R != nullptr);
			J->SetNumberField(TEXT("z0_m"), T.Z);
			if (R)
			{
				const double Z = static_cast<double>(R->ZQ) / 65536.0;
				J->SetNumberField(TEXT("z_m"), Z);
				J->SetNumberField(TEXT("dz_m"), Z - T.Z);
			}
			Tj.Add(MakeShared<FJsonValueObject>(J));
		}
		V->SetArrayField(TEXT("targets"), Tj);
	}
	TArray<TSharedPtr<FJsonValue>> Fj;
	for (const FString& F : Fails)
	{
		Fj.Add(MakeShared<FJsonValueString>(F));
	}
	V->SetArrayField(TEXT("fails"), Fj);
	V->SetNumberField(TEXT("ms"), FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0));
	bOutPass = Fails.Num() == 0;
	V->SetBoolField(TEXT("pass"), bOutPass);
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_verify %s: %s live=%s ref=%s instances=%lld max dpos %.5f cm drot %.2e dz %.2e m dup %.2e%s"), *Name, bOutPass ? TEXT("PASS") : TEXT("FAIL"),
		*Hex64(LiveFnv), *Hex64(RefFnv), Instances, MaxDPos, MaxDRot, MaxDZ, MaxDUp, Fails.Num() ? *(TEXT(" fails: ") + FString::Join(Fails, TEXT("; "))) : TEXT(""));
	return V;
}

FString ATerrainScatter::Sha256HexOf(const TArray<uint8>& Data)
{
	return Sha256Hex(Data);
}

void ATerrainScatter::CollectLive(TArray<FScatterRecord>& Out) const
{
	Out.Reset();
	if (!bAvailable)
	{
		return;
	}
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const int32 NM = ScatterMeshCountOfGrid(Grid);
		for (int32 Tile = 0; Tile < Scheduler.GetTileGrid(Grid).NumTiles(); ++Tile)
		{
			for (int32 K = 0; K < NM; ++K)
			{
				const TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> St = Scheduler.GetUnitState(Grid, Tile, K);
				if (St.IsValid())
				{
					Out.Append(St->Records);
				}
			}
		}
	}
}

bool ATerrainScatter::FindNearest(EScatterClass Class, double XM, double YM, int32 Zone, FScatterRecord& Out) const
{
	if (!bAvailable || !Palette.IsValid())
	{
		return false;
	}
	const EScatterGrid Grid = ScatterGridOfClass(Class);
	const int32 NM = ScatterMeshCountOfGrid(Grid);
	double Best = TNumericLimits<double>::Max();
	bool bFound = false;
	for (int32 Tile = 0; Tile < Scheduler.GetTileGrid(Grid).NumTiles(); ++Tile)
	{
		for (int32 K = 0; K < NM; ++K)
		{
			const TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> St = Scheduler.GetUnitState(Grid, Tile, K);
			if (!St.IsValid())
			{
				continue;
			}
			for (const FScatterRecord& R : St->Records)
			{
				if (R.Class != static_cast<uint8>(Class))
				{
					continue;
				}
				if (Zone >= 0 && static_cast<int32>(ScatterTreeZoneAt(*Palette, R.XQ, R.YQ)) != Zone)
				{
					continue;
				}
				const double Dx = static_cast<double>(R.XQ) / 65536.0 - XM;
				const double Dy = static_cast<double>(R.YQ) / 65536.0 - YM;
				const double D2 = Dx * Dx + Dy * Dy;
				if (D2 < Best || (D2 == Best && bFound && R.Key() < Out.Key()))
				{
					Best = D2;
					Out = R;
					bFound = true;
				}
			}
		}
	}
	return bFound;
}

TSharedRef<FJsonObject> ATerrainScatter::DumpApplied(const FString& Name, const FString& FilePath, bool& bOutOk) const
{
	const uint64 T0 = FPlatformTime::Cycles64();
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("name"), Name);
	J->SetStringField(TEXT("path"), FilePath);
	TArray<FScatterRecord> All;
	CollectLive(All);
	All.Sort([](const FScatterRecord& L, const FScatterRecord& R) { return L.Key() < R.Key(); });
	TArray<uint8> Bytes;
	Bytes.Reserve(All.Num() * ScatterRecordBytes);
	FScatterHash H;
	int64 PerClass[ScatterClassCount] = {};
	for (const FScatterRecord& R : All)
	{
		AppendScatterRecordBytes(Bytes, R);
		ScatterFoldRecord(H, R);
		++PerClass[R.Class];
	}
	bOutOk = FFileHelper::SaveArrayToFile(Bytes, *FilePath);
	J->SetBoolField(TEXT("written"), bOutOk);
	J->SetNumberField(TEXT("records"), static_cast<double>(All.Num()));
	J->SetNumberField(TEXT("bytes"), static_cast<double>(Bytes.Num()));
	J->SetNumberField(TEXT("record_bytes"), ScatterRecordBytes);
	J->SetStringField(TEXT("sha256"), Sha256Hex(Bytes));
	J->SetStringField(TEXT("scatter_live_fnv"), Hex64(ScatterFnvOf(H)));
	TSharedRef<FJsonObject> Cls = MakeShared<FJsonObject>();
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		Cls->SetNumberField(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C))), static_cast<double>(PerClass[C]));
	}
	J->SetObjectField(TEXT("classes"), Cls);
	J->SetNumberField(TEXT("ms"), FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0));
	return J;
}

TSharedRef<FJsonObject> ATerrainScatter::CheckDiscs(const FString& Name, const FString& SetName, const TArray<FVector>& Discs) const
{
	const uint64 T0 = FPlatformTime::Cycles64();
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("name"), Name);
	J->SetStringField(TEXT("set"), SetName);
	J->SetNumberField(TEXT("discs"), Discs.Num());
	struct FCls
	{
		int64 CoreLive = 0;
		int64 BandLive = 0;
		int64 CorePure = 0;
		int64 BandPure = 0;
	};
	FCls Counts[ScatterClassCount];
	// 0 = outside, 1 = band (0.5R .. R + 2 m), 2 = core (inner half radius of some disc).
	auto Zone = [&Discs](int32 XQ, int32 YQ) -> int32
	{
		const double X = static_cast<double>(XQ) / 65536.0;
		const double Y = static_cast<double>(YQ) / 65536.0;
		int32 Best = 0;
		for (const FVector& D : Discs)
		{
			const double Dx = X - D.X;
			const double Dy = Y - D.Y;
			const double D2 = Dx * Dx + Dy * Dy;
			const double Core = 0.5 * D.Z;
			if (D2 <= Core * Core)
			{
				return 2;
			}
			const double Outer = D.Z + 2.0;
			if (D2 <= Outer * Outer)
			{
				Best = 1;
			}
		}
		return Best;
	};
	TArray<FScatterRecord> Live;
	CollectLive(Live);
	for (const FScatterRecord& R : Live)
	{
		const int32 Z = Zone(R.XQ, R.YQ);
		if (Z == 2)
		{
			++Counts[R.Class].CoreLive;
		}
		else if (Z == 1)
		{
			++Counts[R.Class].BandLive;
		}
	}
	// The same discs with the splat forced to pure grass (same heights, same seed): the yield a disc would have had unpainted.
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	FScatterSnapshot Full = MakeFullSnapshot(HF);
	for (int32 I = 0; I + 3 < Full.Splat.Num(); I += 4)
	{
		Full.Splat[I] = 255;
		Full.Splat[I + 1] = 0;
		Full.Splat[I + 2] = 0;
		Full.Splat[I + 3] = 0;
	}
	FCriticalSection Lock;
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const FScatterTileGrid& TG = Scheduler.GetTileGrid(Grid);
		ParallelFor(TG.NumTiles(), [&](int32 Index)
		{
			const FScatterTileKey Key = Scheduler.GetTileKey(Grid, Index);
			FScatterTileRecords Recs;
			EvaluateTile(Full, *Palette, Key, Recs);
			FCls Local[ScatterClassCount];
			for (int32 M = 0; M < ScatterMeshCount; ++M)
			{
				for (const FScatterRecord& R : Recs.PerMesh[M])
				{
					const int32 Z = Zone(R.XQ, R.YQ);
					if (Z == 2)
					{
						++Local[R.Class].CorePure;
					}
					else if (Z == 1)
					{
						++Local[R.Class].BandPure;
					}
				}
			}
			FScopeLock L(&Lock);
			for (int32 C = 0; C < ScatterClassCount; ++C)
			{
				Counts[C].CorePure += Local[C].CorePure;
				Counts[C].BandPure += Local[C].BandPure;
			}
		});
	}
	TSharedRef<FJsonObject> Cls = MakeShared<FJsonObject>();
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		TSharedRef<FJsonObject> One = MakeShared<FJsonObject>();
		One->SetNumberField(TEXT("core_live"), static_cast<double>(Counts[C].CoreLive));
		One->SetNumberField(TEXT("core_pure"), static_cast<double>(Counts[C].CorePure));
		One->SetNumberField(TEXT("band_live"), static_cast<double>(Counts[C].BandLive));
		One->SetNumberField(TEXT("band_pure"), static_cast<double>(Counts[C].BandPure));
		Cls->SetObjectField(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C))), One);
	}
	J->SetObjectField(TEXT("classes"), Cls);
	J->SetNumberField(TEXT("ms"), FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - T0));
	return J;
}

TSharedRef<FJsonObject> ATerrainScatter::CountsJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	TArray<TSharedPtr<FJsonValue>> Names;
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		Names.Add(MakeShared<FJsonValueString>(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C)))));
	}
	O->SetArrayField(TEXT("class_names"), Names);
	for (int32 G = 0; G < ScatterGridCount; ++G)
	{
		const EScatterGrid Grid = static_cast<EScatterGrid>(G);
		const FScatterTileGrid& TG = Scheduler.GetTileGrid(Grid);
		const int32 NM = ScatterMeshCountOfGrid(Grid);
		TSharedRef<FJsonObject> GJ = MakeShared<FJsonObject>();
		GJ->SetNumberField(TEXT("size_m"), TG.SizeM);
		GJ->SetNumberField(TEXT("min"), TG.Min);
		GJ->SetNumberField(TEXT("count"), TG.Count);
		TArray<TSharedPtr<FJsonValue>> Tiles;
		for (int32 Tile = 0; Tile < TG.NumTiles(); ++Tile)
		{
			int64 PerClass[ScatterClassCount] = {};
			for (int32 K = 0; K < NM; ++K)
			{
				const TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> St = Scheduler.GetUnitState(Grid, Tile, K);
				if (St.IsValid())
				{
					for (const FScatterRecord& R : St->Records)
					{
						++PerClass[R.Class];
					}
				}
			}
			TArray<TSharedPtr<FJsonValue>> Row;
			Row.Add(MakeShared<FJsonValueNumber>(TG.TXOf(Tile)));
			Row.Add(MakeShared<FJsonValueNumber>(TG.TYOf(Tile)));
			for (int32 C = 0; C < ScatterClassCount; ++C)
			{
				Row.Add(MakeShared<FJsonValueNumber>(static_cast<double>(PerClass[C])));
			}
			Tiles.Add(MakeShared<FJsonValueArray>(Row));
		}
		GJ->SetArrayField(TEXT("tiles"), Tiles);
		O->SetObjectField(Grid == EScatterGrid::Fine ? TEXT("fine") : TEXT("coarse"), GJ);
	}
	O->SetStringField(TEXT("tile_row_format"), TEXT("tx, ty, then the count of each class in class_names order"));
	return O;
}

FTransform ATerrainScatter::InstanceTransformOf(const FScatterRecord& R) const
{
	return ScatterInstanceTransform(BuildInstance(R, *Palette));
}

FBox ATerrainScatter::MeshLocalBoundsCm(EScatterMesh Mesh) const
{
	const int32 M = static_cast<int32>(Mesh);
	if (!Meshes.IsValidIndex(M) || !Meshes[M])
	{
		return FBox(ForceInit);
	}
	return Meshes[M]->GetBounds().GetBox();
}

double ATerrainScatter::CullEndMetersOf(const FScatterRecord& R) const
{
	int32 S0 = 0;
	int32 S1 = 0;
	UnitCullDistances(static_cast<EScatterMesh>(R.Mesh), S0, S1);
	// What the unit really draws: saplings share the tree units (TreeBroadA/B), whose end is the tree's 600 m, so a sapling is drawn to 600 m, not
	// the 300 m of plan C scatter 3.4 (a layout gap reported to the main session by S5; the analytic counts and masks follow the drawing).
	return static_cast<double>(S1) / 100.0;
}

TSharedRef<FJsonObject> ATerrainScatter::OptionsJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetBoolField(TEXT("enabled_at_start"), Options.bEnabled);
	O->SetBoolField(TEXT("requested_on_command_line"), bRequested);
	O->SetBoolField(TEXT("available"), bAvailable);
	O->SetNumberField(TEXT("seed"), Options.Palette.Seed);
	O->SetStringField(TEXT("level"), Options.Palette.Level == EScatterLevel::L1 ? TEXT("L1") : TEXT("L0"));
	O->SetNumberField(TEXT("during_stroke_ms"), Options.DuringStrokeMs);
	O->SetNumberField(TEXT("caster_during_stroke_ms"), Options.CasterDuringStrokeMs);
	O->SetNumberField(TEXT("budget_ms"), Options.BudgetMs);
	O->SetNumberField(TEXT("load_budget_ms"), Options.LoadBudgetMs);
	O->SetNumberField(TEXT("threads"), Options.Threads);
	O->SetNumberField(TEXT("layers_mask"), Options.LayerMask);
	O->SetNumberField(TEXT("governor"), Options.Governor);
	O->SetStringField(TEXT("mobility"), Options.Mobility);
	O->SetBoolField(TEXT("grass_nanite"), Options.bGrassNanite);
	O->SetStringField(TEXT("apply"), Options.ApplyMode);
	O->SetNumberField(TEXT("fine_tile_m"), Options.FineTileM);
	O->SetNumberField(TEXT("coarse_tile_m"), Options.CoarseTileM);
	O->SetStringField(TEXT("params"), Options.ParamsSpec);
	O->SetStringField(TEXT("params_error"), Options.ParamsError);
	O->SetStringField(TEXT("config_fnv"), Hex64(GetConfigFnv()));
	if (Palette.IsValid())
	{
		// The whole table as stored integers (Q16, bytes, tan^2 * 2^32), for the parser's dump oracle (class limits come from here).
		TSharedRef<FJsonObject> Pal = MakeShared<FJsonObject>();
		for (int32 I = 0; I < ScatterParamCount; ++I)
		{
			Pal->SetNumberField(UTF8_TO_TCHAR(ScatterParamInfo(static_cast<EScatterParam>(I)).Name), static_cast<double>(Palette->V[I]));
		}
		O->SetObjectField(TEXT("palette"), Pal);
		O->SetNumberField(TEXT("palette_seed"), static_cast<double>(Palette->Seed));
	}
	const FScatterSchedulerConfig& Cfg = Scheduler.GetConfig();
	O->SetNumberField(TEXT("predict_a_ms"), Cfg.PredictA);
	O->SetNumberField(TEXT("predict_b_ms_per_change"), Cfg.PredictB);
	O->SetNumberField(TEXT("predict_c_ms_per_instance"), Cfg.PredictC);
	TSharedRef<FJsonObject> MJ = MakeShared<FJsonObject>();
	for (int32 M = 0; M < MeshPaths.Num(); ++M)
	{
		if (MeshPaths[M].IsEmpty())
		{
			continue;
		}
		TSharedRef<FJsonObject> One = MakeShared<FJsonObject>();
		One->SetStringField(TEXT("path"), MeshPaths[M]);
		One->SetStringField(TEXT("sha256"), MeshSha256[M]);
		MJ->SetObjectField(UTF8_TO_TCHAR(ScatterMeshName(static_cast<EScatterMesh>(M))), One);
	}
	O->SetObjectField(TEXT("meshes"), MJ);
	O->SetStringField(TEXT("mesh_errors"), MeshErrors);
	TSharedRef<FJsonObject> SG = MakeShared<FJsonObject>();
	for (const TCHAR* Name : ScalabilityCvars)
	{
		const IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(Name);
		SG->SetStringField(Name, Cv ? Cv->GetString() : FString(TEXT("missing")));
	}
	O->SetObjectField(TEXT("sg"), SG);
	TSharedRef<FJsonObject> GR = MakeShared<FJsonObject>();
	for (const TPair<FString, FString>& R : GovernorReadBack)
	{
		GR->SetStringField(R.Key, R.Value);
	}
	O->SetObjectField(TEXT("governor_readback"), GR);
	return O;
}

TSharedRef<FJsonObject> ATerrainScatter::ResultsJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	O->SetBoolField(TEXT("available"), bAvailable);
	O->SetBoolField(TEXT("enabled"), bEnabledNow);
	O->SetStringField(TEXT("config_fnv"), Hex64(GetConfigFnv()));
	O->SetStringField(TEXT("level"), Options.Palette.Level == EScatterLevel::L1 ? TEXT("L1") : TEXT("L0"));
	if (!bAvailable)
	{
		return O;
	}
	const FScatterTileGrid& FG = Scheduler.GetTileGrid(EScatterGrid::Fine);
	const FScatterTileGrid& CG = Scheduler.GetTileGrid(EScatterGrid::Coarse);
	O->SetNumberField(TEXT("tiles_fine"), FG.NumTiles());
	O->SetNumberField(TEXT("tiles_coarse"), CG.NumTiles());
	int32 Components = 0;
	for (const TArray<TObjectPtr<UChimeraScatterISM>>* A : {&FineUnits, &CoarseUnits})
	{
		for (const UChimeraScatterISM* C : *A)
		{
			Components += C ? 1 : 0;
		}
	}
	O->SetNumberField(TEXT("units"), FineUnits.Num() + CoarseUnits.Num());
	O->SetNumberField(TEXT("components"), Components);
	const FScatterHash Live = Scheduler.FoldApplied();
	TSharedRef<FJsonObject> PerClass = MakeShared<FJsonObject>();
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		PerClass->SetNumberField(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C))), static_cast<double>(Live.Classes[C].Count));
	}
	O->SetObjectField(TEXT("instances_per_class"), PerClass);
	O->SetNumberField(TEXT("instances"), static_cast<double>(Live.TotalCount()));

	const FScatterProxyCounters& PC = ScatterProxyCounters();
	O->SetNumberField(TEXT("proxy_first_creates"), static_cast<double>(PC.FirstCreates.load()));
	O->SetNumberField(TEXT("proxy_expected_rebuilds"), static_cast<double>(PC.ExpectedRebuilds.load()));
	O->SetNumberField(TEXT("proxy_recreates"), static_cast<double>(PC.Recreates.load()));
	O->SetNumberField(TEXT("proxy_recreates_during_edits"), static_cast<double>(PC.RecreatesDuringEdits.load()));
	O->SetNumberField(TEXT("proxy_apply_dirtied_recreates"), static_cast<double>(PC.ApplyDirtiedRecreates.load()));
	O->SetNumberField(TEXT("proxy_engine_recreates"), static_cast<double>(PC.EngineRecreates.load()));
	O->SetNumberField(TEXT("proxy_engine_recreates_during_edits"), static_cast<double>(PC.EngineRecreatesDuringEdits.load()));
	O->SetNumberField(TEXT("proxy_compile_recreates"), static_cast<double>(PC.CompileRecreates.load()));
	O->SetNumberField(TEXT("proxy_compile_recreates_during_edits"), static_cast<double>(PC.CompileRecreatesDuringEdits.load()));
	O->SetNumberField(TEXT("proxy_refills"), static_cast<double>(PC.Refills.load()));
	O->SetNumberField(TEXT("proxy_refills_during_edits"), static_cast<double>(PC.RefillsDuringEdits.load()));
	O->SetNumberField(TEXT("proxy_pso_recreates"), static_cast<double>(PC.PsoRecreates.load()));
	O->SetNumberField(TEXT("proxy_pso_recreates_during_edits"), static_cast<double>(PC.PsoRecreatesDuringEdits.load()));
	O->SetNumberField(TEXT("shader_propagations"), static_cast<double>(PC.ShaderPropagations.load()));
	O->SetNumberField(TEXT("asset_post_compiles"), static_cast<double>(PC.AssetPostCompiles.load()));
	O->SetNumberField(TEXT("compile_busy_frames"), static_cast<double>(PC.CompileBusyFrames.load()));
	O->SetNumberField(TEXT("frame_rows_dropped"), static_cast<double>(FrameRowsDropped));
	O->SetNumberField(TEXT("apply_rows_dropped"), static_cast<double>(ApplyRowsDropped));
	O->SetNumberField(TEXT("max_series_rows"), MaxSeriesRows);
	{
		// Frame-stamped proxy and compile events (task S5): the parser places them in the op windows of the timeline (frame_start / frame_end).
		TArray<FScatterProxyEventRow> Events;
		int64 Dropped = 0;
		ScatterCopyProxyEvents(Events, Dropped);
		TArray<TSharedPtr<FJsonValue>> Kinds;
		for (int32 K = 0; K < static_cast<int32>(EScatterProxyEvent::Count); ++K)
		{
			Kinds.Add(MakeShared<FJsonValueString>(ScatterProxyEventName(static_cast<EScatterProxyEvent>(K))));
		}
		O->SetArrayField(TEXT("proxy_event_kinds"), Kinds);
		O->SetStringField(TEXT("proxy_events_format"), TEXT("frame, kind (index into proxy_event_kinds), unit (grid << 24 | mesh << 16 | tile, -1 = none)"));
		TArray<TSharedPtr<FJsonValue>> Rows;
		Rows.Reserve(Events.Num());
		for (const FScatterProxyEventRow& E : Events)
		{
			TArray<TSharedPtr<FJsonValue>> Row;
			Row.Add(MakeShared<FJsonValueNumber>(static_cast<double>(E.Frame)));
			Row.Add(MakeShared<FJsonValueNumber>(static_cast<double>(static_cast<uint8>(E.Kind))));
			Row.Add(MakeShared<FJsonValueNumber>(static_cast<double>(E.Unit)));
			Rows.Add(MakeShared<FJsonValueArray>(Row));
		}
		O->SetArrayField(TEXT("proxy_events"), Rows);
		O->SetNumberField(TEXT("proxy_events_dropped"), static_cast<double>(Dropped));
	}

	const FScatterCounters& K = Scheduler.GetCounters();
	TSharedRef<FJsonObject> CJ = MakeShared<FJsonObject>();
	CJ->SetNumberField(TEXT("dispatched"), static_cast<double>(K.Dispatched));
	CJ->SetNumberField(TEXT("applied"), static_cast<double>(K.Applied));
	CJ->SetNumberField(TEXT("skipped_identical"), static_cast<double>(K.SkippedIdentical));
	CJ->SetNumberField(TEXT("discarded_epoch"), static_cast<double>(K.DiscardedEpoch));
	CJ->SetNumberField(TEXT("cancelled"), static_cast<double>(K.Cancelled));
	CJ->SetNumberField(TEXT("units_applied"), static_cast<double>(K.UnitsApplied));
	O->SetObjectField(TEXT("counters"), CJ);
	TSharedRef<FJsonObject> PJ = MakeShared<FJsonObject>();
	PJ->SetNumberField(TEXT("mid_stroke_dispatches"), static_cast<double>(K.MidStrokeDispatches));
	PJ->SetNumberField(TEXT("stale_result_redispatches"), static_cast<double>(K.StaleResultRedispatches));
	PJ->SetNumberField(TEXT("in_flight_max"), K.InFlightMax);
	PJ->SetNumberField(TEXT("busy_max"), K.BusyMax);
	PJ->SetNumberField(TEXT("gt_generations"), static_cast<double>(K.GtGenerations));
	O->SetObjectField(TEXT("path_proofs"), PJ);
	O->SetNumberField(TEXT("in_flight_now"), Scheduler.NumInFlight());
	O->SetNumberField(TEXT("busy_now"), Scheduler.NumBusy());
	O->SetNumberField(TEXT("dirty_now"), Scheduler.NumDirty());
	O->SetNumberField(TEXT("ready_now"), Scheduler.NumReady());
	O->SetNumberField(TEXT("enables"), static_cast<double>(Enables));
	O->SetNumberField(TEXT("disables"), static_cast<double>(Disables));
	O->SetNumberField(TEXT("loads"), static_cast<double>(Loads));
	TSharedRef<FJsonObject> EJ = MakeShared<FJsonObject>();
	for (int32 E = 0; E < static_cast<int32>(ETerrainChangeKind::Count); ++E)
	{
		EJ->SetNumberField(TerrainChangeKindName(static_cast<ETerrainChangeKind>(E)), static_cast<double>(EventsSeen[E]));
	}
	O->SetObjectField(TEXT("events_seen"), EJ);
	O->SetNumberField(TEXT("init_ms"), InitMs);
	TArray<TSharedPtr<FJsonValue>> FillJ;
	for (const double F : FillMs)
	{
		FillJ.Add(MakeShared<FJsonValueNumber>(F));
	}
	O->SetArrayField(TEXT("fill_ms"), FillJ);
	O->SetNumberField(TEXT("mark_us_mean"), MarkCount ? MarkUsTotal / MarkCount : 0.0);
	O->SetNumberField(TEXT("mark_us_max"), MarkUsMax);
	O->SetNumberField(TEXT("mark_count"), static_cast<double>(MarkCount));

	// Per-phase series of scatter's own game-thread time and the end-of-frame flush; fill frames carry their own phase "fill".
	const TArray<FString>& PhaseNames = Terrain ? Terrain->GetPhaseNames() : TArray<FString>();
	TMap<FString, TArray<double>> Gt;
	TMap<FString, TArray<double>> Fl;
	TArray<double> GtAll;
	TArray<double> FlAll;
	TArray<double> Queue;
	for (const FScatterFrameRow& R : FrameRows)
	{
		const FString Ph = R.bFill ? FString(TEXT("fill")) : (PhaseNames.IsValidIndex(R.Phase) ? PhaseNames[R.Phase] : FString(TEXT("?")));
		Gt.FindOrAdd(Ph).Add(R.GtMs);
		Fl.FindOrAdd(Ph).Add(R.FlushMs);
		if (!R.bFill)
		{
			GtAll.Add(R.GtMs);
			FlAll.Add(R.FlushMs);
		}
		Queue.Add(R.Ready);
	}
	TSharedRef<FJsonObject> GtJ = MakeShared<FJsonObject>();
	GtJ->SetObjectField(TEXT("all_but_fill"), SeriesJson(GtAll));
	TSharedRef<FJsonObject> FlJ = MakeShared<FJsonObject>();
	FlJ->SetObjectField(TEXT("all_but_fill"), SeriesJson(FlAll));
	for (const TPair<FString, TArray<double>>& Pr : Gt)
	{
		GtJ->SetObjectField(Pr.Key, SeriesJson(Pr.Value));
	}
	for (const TPair<FString, TArray<double>>& Pr : Fl)
	{
		FlJ->SetObjectField(Pr.Key, SeriesJson(Pr.Value));
	}
	O->SetObjectField(TEXT("scatter_gt_ms"), GtJ);
	O->SetObjectField(TEXT("scatter_flush_ms"), FlJ);
	O->SetObjectField(TEXT("queue_depth"), SeriesJson(Queue));
	{
		TArray<double> Gm;
		{
			FScopeLock L(&GenLock);
			Gm = GenMs;
		}
		O->SetObjectField(TEXT("gen_ms"), SeriesJson(Gm));
	}
	TArray<double> ApplyMs;
	TArray<double> ApplyMsEdit;
	for (const FScatterApplyRow& R : ApplyRows)
	{
		ApplyMs.Add(R.Ms);
		if (!R.bFill)
		{
			ApplyMsEdit.Add(R.Ms);
		}
	}
	O->SetObjectField(TEXT("apply_unit_ms"), SeriesJson(ApplyMs));
	O->SetObjectField(TEXT("apply_unit_ms_edits"), SeriesJson(ApplyMsEdit));
	// Rows for the prediction fit (S4 item 5): [ms, predicted, changes, updates, removes, appends, instances_after, mesh, fill].
	TArray<TSharedPtr<FJsonValue>> Rows;
	Rows.Reserve(ApplyRows.Num());
	for (const FScatterApplyRow& R : ApplyRows)
	{
		TArray<TSharedPtr<FJsonValue>> A;
		A.Add(MakeShared<FJsonValueNumber>(R.Ms));
		A.Add(MakeShared<FJsonValueNumber>(R.PredictedMs));
		A.Add(MakeShared<FJsonValueNumber>(R.Changes));
		A.Add(MakeShared<FJsonValueNumber>(R.Updates));
		A.Add(MakeShared<FJsonValueNumber>(R.Removes));
		A.Add(MakeShared<FJsonValueNumber>(R.Appends));
		A.Add(MakeShared<FJsonValueNumber>(R.InstancesAfter));
		A.Add(MakeShared<FJsonValueNumber>(R.Mesh));
		A.Add(MakeShared<FJsonValueNumber>(R.bFill ? 1 : 0));
		Rows.Add(MakeShared<FJsonValueArray>(A));
	}
	O->SetStringField(TEXT("apply_rows_format"), TEXT("ms, predicted_ms, changes, updates, removes, appends, instances_after, mesh, fill"));
	O->SetArrayField(TEXT("apply_rows"), Rows);
	// Frame series: [frame, phase index, fill, gt_ms, flush_ms, flush_calls, ready, in_flight, busy, units_applied].
	TArray<TSharedPtr<FJsonValue>> Frames;
	Frames.Reserve(FrameRows.Num());
	for (const FScatterFrameRow& R : FrameRows)
	{
		TArray<TSharedPtr<FJsonValue>> A;
		A.Add(MakeShared<FJsonValueNumber>(static_cast<double>(R.Frame)));
		A.Add(MakeShared<FJsonValueNumber>(R.Phase));
		A.Add(MakeShared<FJsonValueNumber>(R.bFill ? 1 : 0));
		A.Add(MakeShared<FJsonValueNumber>(R.GtMs));
		A.Add(MakeShared<FJsonValueNumber>(R.FlushMs));
		A.Add(MakeShared<FJsonValueNumber>(R.FlushCalls));
		A.Add(MakeShared<FJsonValueNumber>(R.Ready));
		A.Add(MakeShared<FJsonValueNumber>(R.InFlight));
		A.Add(MakeShared<FJsonValueNumber>(R.Busy));
		A.Add(MakeShared<FJsonValueNumber>(R.UnitsApplied));
		Frames.Add(MakeShared<FJsonValueArray>(A));
	}
	O->SetStringField(TEXT("frames_format"), TEXT("frame, phase_index, fill, gt_ms, flush_ms, flush_calls, ready, in_flight, busy, units_applied"));
	{
		// phase_index -> phase name (the terrain's phase list), so the parser can select frames by phase (SX11's > 2 ms fraction).
		TArray<TSharedPtr<FJsonValue>> Names;
		if (Terrain)
		{
			for (const FString& N : Terrain->GetPhaseNames())
			{
				Names.Add(MakeShared<FJsonValueString>(N));
			}
		}
		O->SetArrayField(TEXT("phase_names"), Names);
	}
	O->SetArrayField(TEXT("frames"), Frames);
	TArray<TSharedPtr<FJsonValue>> Lat;
	for (const FScatterLatencyRow& L : Latencies)
	{
		TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
		J->SetStringField(TEXT("reason"), L.Reason);
		J->SetNumberField(TEXT("id"), static_cast<double>(L.Id));
		J->SetNumberField(TEXT("frames"), L.Frames);
		J->SetNumberField(TEXT("ms"), L.Ms);
		Lat.Add(MakeShared<FJsonValueObject>(J));
	}
	O->SetArrayField(TEXT("latency"), Lat);
	return O;
}

FString ATerrainScatter::HudLine() const
{
	if (!bAvailable)
	{
		return TEXT("Scatter off");
	}
	return FString::Printf(TEXT("Scatter %s  instances %lld  dirty %d  busy %d  last apply %.2f ms"), bEnabledNow ? TEXT("on") : TEXT("disabled"), TotalInstances(),
		Scheduler.NumDirty(), Scheduler.NumBusy(), LastApplyMs);
}

int32 ATerrainScatter::Shutdown()
{
	// Teardown (plan C scatter 3.5): nothing is dispatched any more, and every task handle is waited on before the scheduler goes away.
	const int32 InFlightBefore = bAvailable ? Scheduler.NumInFlight() : 0;
	const int32 Handles = Tasks.Num();
	Scheduler.CancelAll();
	PruneTasks(true);
	Scheduler.PollResults();
	RestoreGovernorCvars();
	SetActorTickEnabled(false);
	if (Terrain)
	{
		Terrain->OnTerrainChanged.RemoveAll(this);
	}
#if WITH_EDITOR
	FEditorSupportDelegates::RedrawAllViewports.RemoveAll(this);
	FAssetCompilingManager::Get().OnAssetPostCompileEvent().RemoveAll(this);
#endif
	if (bAvailable && !bShutDown)
	{
		const FScatterCounters& K = Scheduler.GetCounters();
		UE_LOG(LogChimeraTerrain, Display, TEXT("scatter teardown: in_flight_before=%d task_handles_waited=%d in_flight_after=%d busy_after=%d dispatched=%lld applied=%lld skipped=%lld discarded=%lld cancelled=%lld"),
			InFlightBefore, Handles, Scheduler.NumInFlight(), Scheduler.NumBusy(), static_cast<long long>(K.Dispatched), static_cast<long long>(K.Applied),
			static_cast<long long>(K.SkippedIdentical), static_cast<long long>(K.DiscardedEpoch), static_cast<long long>(K.Cancelled));
	}
	bShutDown = true;
	return InFlightBefore;
}

void ATerrainScatter::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	Shutdown();
	Super::EndPlay(EndPlayReason);
}

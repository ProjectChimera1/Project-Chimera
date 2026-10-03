// Copyright Chimera. See ChimeraUnitRenderer.h.
#include "ChimeraUnitRenderer.h"

#include "ChimeraSimLog.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Components/PostProcessComponent.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "Engine/StaticMesh.h"
#include "Engine/World.h"
#include "Materials/MaterialInterface.h"
#include "Misc/FileHelper.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UObject/UObjectIterator.h"

namespace
{
	const TCHAR* ProxyCubeMesh = TEXT("/Engine/BasicShapes/Cube.Cube");      // 100 cm cube, pivot at its centre
	const TCHAR* ProxyMaterial = TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial");
	constexpr float HeldExposureSpeed = 0.001f; // EV per second while held (ChimeraTerrain TerrainLighting's hold; Scene.h:2005-2010)
}

AChimeraUnitRenderer::AChimeraUnitRenderer()
{
	PrimaryActorTick.bCanEverTick = false; // the director calls Update after it steps, in the same frame

	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	SetRootComponent(Root);

	// Unbound and above the arena's post volume; it overrides nothing until SetExposureHold(true).
	ExposurePost = CreateDefaultSubobject<UPostProcessComponent>(TEXT("ExposureHold"));
	ExposurePost->SetupAttachment(Root);
	ExposurePost->bUnbound = true;
	ExposurePost->Priority = 1000.0f;
	ExposurePost->BlendWeight = 1.0f;
}

FString AChimeraUnitRenderer::FactionName(uint8 Faction)
{
	// chimera_sim.h "Factions": 1 = Player1 = slot 0 = alpha, 2 = Player2 = slot 1 = beta.
	return Faction == 1 ? TEXT("alpha") : Faction == 2 ? TEXT("beta") : FString::Printf(TEXT("f%d"), (int32)Faction);
}

double AChimeraUnitRenderer::InitialYaw(uint8 Faction)
{
	// Facing the enemy before the first move: alpha starts at x < 0 and faces +X (UE yaw 0), beta faces -X (yaw 180).
	return Faction == 2 ? 180.0 : 0.0;
}

FString AChimeraUnitRenderer::ObjectPath(const FString& PackagePath)
{
	// unit_meshes.json stores package paths; LoadObject wants "Package.Object".
	if (PackagePath.Contains(TEXT(".")))
	{
		return PackagePath;
	}
	FString Name;
	if (!PackagePath.Split(TEXT("/"), nullptr, &Name, ESearchCase::CaseSensitive, ESearchDir::FromEnd))
	{
		return PackagePath;
	}
	return PackagePath + TEXT(".") + Name;
}

UMaterialInterface* AChimeraUnitRenderer::LoadMaterial(const FString& Path) const
{
	return LoadObject<UMaterialInterface>(nullptr, *ObjectPath(Path));
}

bool AChimeraUnitRenderer::LoadTable(const FString& Path, FString& OutError)
{
	FString Text;
	if (!FFileHelper::LoadFileToString(Text, *Path))
	{
		OutError = FString::Printf(TEXT("cannot read %s"), *Path);
		return false;
	}
	TSharedPtr<FJsonObject> Doc;
	TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
	if (!FJsonSerializer::Deserialize(Reader, Doc) || !Doc.IsValid())
	{
		OutError = FString::Printf(TEXT("%s is not JSON"), *Path);
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Entries = nullptr;
	if (!Doc->TryGetArrayField(TEXT("entries"), Entries) || Entries == nullptr)
	{
		OutError = FString::Printf(TEXT("%s has no 'entries' list"), *Path);
		return false;
	}
	Table.Reset();
	for (const TSharedPtr<FJsonValue>& V : *Entries)
	{
		const TSharedPtr<FJsonObject> O = V.IsValid() ? V->AsObject() : nullptr;
		if (!O.IsValid())
		{
			continue;
		}
		FMeshEntry E;
		FString Faction, Kind;
		const TArray<TSharedPtr<FJsonValue>>* Size = nullptr;
		const TArray<TSharedPtr<FJsonValue>>* Mis = nullptr;
		if (!O->TryGetStringField(TEXT("id"), E.Id) || !O->TryGetStringField(TEXT("faction"), Faction) || !O->TryGetStringField(TEXT("def_id"), E.DefId)
			|| !O->TryGetStringField(TEXT("kind"), Kind) || !O->TryGetStringField(TEXT("mesh"), E.Mesh) || !O->TryGetNumberField(TEXT("scale"), E.Scale)
			|| !O->TryGetNumberField(TEXT("front_yaw_deg"), E.FrontYawDeg) || !O->TryGetNumberField(TEXT("foot_z_cm"), E.FootZCm)
			|| !O->TryGetArrayField(TEXT("size_cm"), Size) || Size == nullptr || Size->Num() != 3)
		{
			OutError = FString::Printf(TEXT("%s: an entry lacks id/faction/def_id/kind/mesh/scale/front_yaw_deg/foot_z_cm/size_cm[3]"), *Path);
			return false;
		}
		E.Faction = Faction == TEXT("alpha") ? 1 : Faction == TEXT("beta") ? 2 : 0;
		E.bBuilding = Kind == TEXT("building");
		E.HeightCm = (*Size)[2]->AsNumber();
		E.RadiusCm = 0.5 * FMath::Sqrt(FMath::Square((*Size)[0]->AsNumber()) + FMath::Square((*Size)[1]->AsNumber()));
		if (O->TryGetArrayField(TEXT("team_mi"), Mis) && Mis != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& M : *Mis)
			{
				E.TeamMis.Add(M->AsString());
			}
		}
		Table.Add(E);
	}
	return Table.Num() > 0;
}

int32 AChimeraUnitRenderer::FindOrCreateGroup(uint8 Faction, const FString& DefId, bool bBuilding, int32 Reserve)
{
	const FString Key = FString::Printf(TEXT("%s:%d:%s"), bBuilding ? TEXT("b") : TEXT("u"), (int32)Faction, *DefId);
	if (const int32* Found = GroupByKey.Find(Key))
	{
		return *Found;
	}

	const FMeshEntry* Entry = Table.FindByPredicate([&](const FMeshEntry& E) { return E.Faction == Faction && E.DefId == DefId && E.bBuilding == bBuilding; });
	FChimeraDrawGroup G;
	G.Faction = Faction;
	G.DefId = DefId;
	G.bBuilding = bBuilding;

	UStaticMesh* Mesh = nullptr;
	TArray<UMaterialInterface*> Mats;
	if (Entry != nullptr)
	{
		Mesh = LoadObject<UStaticMesh>(nullptr, *ObjectPath(Entry->Mesh));
		for (const FString& Mi : Entry->TeamMis)
		{
			Mats.Add(LoadMaterial(Mi));
		}
		G.Scale = FVector(Entry->Scale);
		G.FrontYawDeg = Entry->FrontYawDeg;
		G.FootZCm = Entry->FootZCm;
		G.HeightCm = Entry->HeightCm;
		G.RadiusCm = Entry->RadiusCm;
		G.MeshPath = Entry->Mesh;
		if (Mesh == nullptr)
		{
			UE_LOG(LogChimeraSim, Warning, TEXT("renderer: mesh %s of %s did not load; using a proxy cube"), *Entry->Mesh, *Entry->Id);
		}
	}
	if (Mesh == nullptr)
	{
		// Plan A 3.7: an unmapped building is a grey proxy cube with the team MI (the faction's command centre MI here); an unmapped
		// unit gets the same treatment at unit size, so an unexpected definition still shows up instead of vanishing.
		G.bProxy = true;
		Mesh = LoadObject<UStaticMesh>(nullptr, ProxyCubeMesh);
		const FMeshEntry* Cc = Table.FindByPredicate([&](const FMeshEntry& E) { return E.Faction == Faction && E.bBuilding; });
		Mats.Reset();
		UMaterialInterface* M = (Cc != nullptr && Cc->TeamMis.Num() > 0) ? LoadMaterial(Cc->TeamMis[0]) : nullptr;
		Mats.Add(M != nullptr ? M : LoadObject<UMaterialInterface>(nullptr, ProxyMaterial));
		G.Scale = bBuilding ? FVector(8.0, 8.0, 5.0) : FVector(0.8, 0.8, 1.8);
		G.HeightCm = G.Scale.Z * 100.0;
		G.FootZCm = G.HeightCm * 0.5; // the cube's pivot is its centre
		G.RadiusCm = 0.5 * FMath::Sqrt(FMath::Square(G.Scale.X * 100.0) + FMath::Square(G.Scale.Y * 100.0));
		G.FrontYawDeg = 0.0;
		G.MeshPath = ProxyCubeMesh;
		if (Entry == nullptr)
		{
			UE_LOG(LogChimeraSim, Warning, TEXT("renderer: no unit_meshes.json entry for %s %s '%s'; drawing a proxy cube"),
				*FactionName(Faction), bBuilding ? TEXT("building") : TEXT("unit"), *DefId);
		}
	}

	const FName CompName = MakeUniqueObjectName(this, UInstancedStaticMeshComponent::StaticClass(),
		FName(*FString::Printf(TEXT("ISM_%s_%s_%s"), bBuilding ? TEXT("b") : TEXT("u"), *FactionName(Faction), DefId.IsEmpty() ? TEXT("none") : *DefId)));
	UInstancedStaticMeshComponent* C = NewObject<UInstancedStaticMeshComponent>(this, CompName);
	C->SetupAttachment(Root);
	C->SetMobility(EComponentMobility::Movable);
	C->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	C->SetCanEverAffectNavigation(false);
	C->SetGenerateOverlapEvents(false);
	C->SetStaticMesh(Mesh);
	for (int32 i = 0; i < Mats.Num(); ++i)
	{
		if (Mats[i] != nullptr)
		{
			C->SetMaterial(i, Mats[i]);
		}
	}
	C->RegisterComponent();
	AddInstanceComponent(C);
	// 25% headroom over the count known now (plan A 3.7).
	C->PreAllocateInstancesMemory(FMath::Max(4, FMath::CeilToInt(Reserve * 1.25)));
	if (!bBuilding)
	{
		C->SetVisibility(!bHide && bUnitsVisible);
	}

	G.Component = Components.Add(C);
	G.Transforms.Reserve(FMath::Max(4, FMath::CeilToInt(Reserve * 1.25)));
	const int32 Index = Groups.Add(MoveTemp(G));
	GroupByKey.Add(Key, Index);
	UE_LOG(LogChimeraSim, Display, TEXT("renderer: group %d %s/%s%s mesh=%s scale=%.3f front_yaw=%.0f foot_z=%.1f reserve=%d"), Index, *FactionName(Faction),
		*DefId, bBuilding ? TEXT(" (building)") : TEXT(""), *Groups[Index].MeshPath, Groups[Index].Scale.X, Groups[Index].FrontYawDeg, Groups[Index].FootZCm, Reserve);
	return Index;
}

int32 AChimeraUnitRenderer::AddInstance(int32 Group, const FTransform& Xf, FString& OutError)
{
	FChimeraDrawGroup& G = Groups[Group];
	const int32 Index = Components[G.Component]->AddInstance(Xf, /*bWorldSpace*/ false);
	// Plain ISM: indices are append order, never reordered (no removals, InstancedStaticMesh.cpp:3692). If that ever fails, the
	// director fails the run with exit 6 (EXECUTION 2.2: files closed, forced exit code) instead of a check() crash.
	if (Index != G.Transforms.Num())
	{
		OutError = FString::Printf(TEXT("group %d (%s/%s): AddInstance returned %d, expected the append index %d"), Group, *FactionName(G.Faction), *G.DefId,
			Index, G.Transforms.Num());
		return INDEX_NONE;
	}
	G.Transforms.Add(Xf);
	return Index;
}

FTransform AChimeraUnitRenderer::UnitTransform(const FChimeraDrawGroup& G, const ChimeraUnit& U, double Alpha, double YawDeg, bool bDrawn) const
{
	// UE(X, Y, Z) = 100 * (x, z, y) in cm from raw / 65536 m (axis swap, no mirror; plan A 3.7, EXECUTION C22).
	FVector Prev(U.prev[0] * PosScale, U.prev[2] * PosScale, U.prev[1] * PosScale);
	const FVector Pos(U.pos[0] * PosScale, U.pos[2] * PosScale, U.pos[1] * PosScale);
	if (FVector::DistSquared(Prev, Pos) > MaxTickDisplacementCm * MaxTickDisplacementCm)
	{
		Prev = Pos; // a spawn or a never-stepped prev (not a move): no interpolation
	}
	FVector L = FMath::Lerp(Prev, Pos, Alpha);
	L.Z += G.FootZCm;
	const FRotator R(0.0, YawDeg + G.FrontYawDeg, 0.0);
	if (bDrawn)
	{
		return FTransform(R, L, G.Scale);
	}
	if (bUnderGround)
	{
		return FTransform(R, FVector(L.X, L.Y, UnderGroundCm), G.Scale);
	}
	return FTransform(R, L, FVector::ZeroVector);
}

FTransform AChimeraUnitRenderer::BuildingTransform(const FChimeraDrawGroup& G, const ChimeraBuilding& B, bool bDrawn) const
{
	FVector L(B.pos[0] * PosScale, B.pos[2] * PosScale, B.pos[1] * PosScale);
	L.Z += G.FootZCm;
	const FRotator R(0.0, G.FrontYawDeg, 0.0);
	if (bDrawn)
	{
		return FTransform(R, L, G.Scale);
	}
	return bUnderGround ? FTransform(R, FVector(L.X, L.Y, UnderGroundCm), G.Scale) : FTransform(R, L, FVector::ZeroVector);
}

bool AChimeraUnitRenderer::Init(int32 InSession, const FString& MeshesJsonPath, bool bHideUnits, bool bDeadUnderGround, FString& OutError)
{
	Session = InSession;
	bHide = bHideUnits;
	bUnderGround = bDeadUnderGround;
	if (!LoadTable(MeshesJsonPath, OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = FString::Printf(TEXT("%s has no entries"), *MeshesJsonPath);
		}
		return false;
	}

	// First read: count per (faction, def) so every initial group pre-allocates once.
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	int32 Count = 0;
	Lib.Call(Lib.Api().ReadUnits, Session, (ChimeraUnit*)nullptr, 0, &Count);
	UnitRows.SetNumZeroed(FMath::Max(Count, 1));
	const int32 Rc = Lib.Call(Lib.Api().ReadUnits, Session, UnitRows.GetData(), UnitRows.Num(), &Count);
	if (Rc != CHIMERA_OK)
	{
		OutError = FString::Printf(TEXT("read_units rc=%d: %s"), Rc, *Lib.LastError(Session));
		return false;
	}
	InitialUnitRows = Count;
	TMap<FString, int32> PerGroup;
	TArray<FString> DefOf;
	DefOf.SetNum(Count);
	for (int32 i = 0; i < Count; ++i)
	{
		DefOf[i] = Lib.UnitDefId(Session, i);
		PerGroup.FindOrAdd(FString::Printf(TEXT("%d:%s"), (int32)UnitRows[i].faction, *DefOf[i]))++;
	}
	for (int32 i = 0; i < Count; ++i)
	{
		const ChimeraUnit& U = UnitRows[i];
		FindOrCreateGroup(U.faction, DefOf[i], false, PerGroup.FindRef(FString::Printf(TEXT("%d:%s"), (int32)U.faction, *DefOf[i])));
	}
	UE_LOG(LogChimeraSim, Display, TEXT("renderer: table %s (%d entries), %d unit rows, %d groups"), *MeshesJsonPath, Table.Num(), Count, Groups.Num());
	if (!Update(1.0, OutError))
	{
		return false;
	}
	InitialBuildingAdds = BuildingJournal.Num(); // the scenario's buildings; later adds are built during the run
	return true;
}

bool AChimeraUnitRenderer::Update(double Alpha, FString& OutError)
{
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	const FChimeraSimApi& Api = Lib.Api();

	// Units: every id 0..HighWaterMark-1 (dead slots included), re-read every frame.
	int32 Count = 0;
	int32 Rc = Lib.Call(Api.ReadUnits, Session, UnitRows.GetData(), UnitRows.Num(), &Count);
	if (Rc == CHIMERA_E_BUFFER)
	{
		UnitRows.SetNumZeroed(Count + Count / 4 + 16);
		Rc = Lib.Call(Api.ReadUnits, Session, UnitRows.GetData(), UnitRows.Num(), &Count);
	}
	if (Rc != CHIMERA_OK)
	{
		OutError = FString::Printf(TEXT("read_units rc=%d: %s"), Rc, *Lib.LastError(Session));
		return false;
	}
	if (Units.Num() < Count)
	{
		Units.SetNum(Count);
	}
	const int64 MinDisp = (int64)(MinYawDisplacementM * 65536.0);
	const int64 MaxDisp = (int64)(MaxTickDisplacementCm / 100.0 * 65536.0);
	int32 Drawn = 0;
	for (int32 i = 0; i < Count; ++i)
	{
		const ChimeraUnit& U = UnitRows[i];
		const int32 Id = U.id;
		if (Id < 0 || Id >= Units.Num())
		{
			OutError = FString::Printf(TEXT("read_units row %d carries id %d (rows %d)"), i, Id, Count);
			return false;
		}
		FUnitSlot& S = Units[Id];
		const int32 Gen = (int32)((uint32)U.ref >> 12);
		const bool bDrawn = (U.flags & 1) != 0 && (U.flags & 8) == 0; // Alive and not Phased
		if (bDrawn && (S.Group < 0 || S.Generation != Gen || S.MeshType != U.mesh_type || S.Faction != U.faction))
		{
			if (S.Group >= 0)
			{
				// A changed generation or mesh type: hide the old instance and add a new one (plan A 3.7).
				FChimeraDrawGroup& Old = Groups[S.Group];
				FTransform Hidden = Old.Transforms[S.Index];
				Hidden.SetScale3D(FVector::ZeroVector);
				Old.Transforms[S.Index] = Hidden;
				++ReAdds;
			}
			else if (Id >= InitialUnitRows)
			{
				++NewIdAdds;
			}
			const FString Def = Lib.UnitDefId(Session, Id);
			const int32 Group = FindOrCreateGroup(U.faction, Def, false, 16);
			S.Generation = Gen;
			S.MeshType = U.mesh_type;
			S.Faction = U.faction;
			S.Group = Group;
			S.YawDeg = InitialYaw(U.faction);
			S.Index = AddInstance(Group, UnitTransform(Groups[Group], U, Alpha, S.YawDeg, true), OutError);
			if (S.Index == INDEX_NONE)
			{
				return false;
			}
			Journal.Add(FChimeraInstanceAdd{ Id, Gen, Group, S.Index });
		}
		if (S.Group < 0)
		{
			continue; // never drawn (dead before it was first seen)
		}
		if (bDrawn && S.Generation == Gen)
		{
			// Facing from the per-tick displacement when it is longer than 0.01 m, else the last yaw (F18: MovementSystem skips zero force).
			const int64 Dx = (int64)U.pos[0] - (int64)U.prev[0];
			const int64 Dz = (int64)U.pos[2] - (int64)U.prev[2];
			const int64 D2 = Dx * Dx + Dz * Dz;
			if (D2 > MinDisp * MinDisp && D2 <= MaxDisp * MaxDisp)
			{
				S.YawDeg = FMath::RadiansToDegrees(FMath::Atan2((double)Dz, (double)Dx)); // UE yaw: atan2(Y, X) with Y = sim z
			}
			++Drawn;
		}
		FChimeraDrawGroup& G = Groups[S.Group];
		G.Transforms[S.Index] = UnitTransform(G, U, Alpha, S.YawDeg, bDrawn && S.Generation == Gen);
	}
	DrawnLastFrame = Drawn;

	// Buildings: recycled slots without a generation, so alive/type/faction/def id are re-checked on every read (chimera_sim.h).
	int32 BCount = 0;
	Rc = Lib.Call(Api.ReadBuildings, Session, BuildingRows.GetData(), BuildingRows.Num(), &BCount);
	if (Rc == CHIMERA_E_BUFFER)
	{
		BuildingRows.SetNumZeroed(BCount + 8);
		Rc = Lib.Call(Api.ReadBuildings, Session, BuildingRows.GetData(), BuildingRows.Num(), &BCount);
	}
	if (Rc != CHIMERA_OK)
	{
		OutError = FString::Printf(TEXT("read_buildings rc=%d: %s"), Rc, *Lib.LastError(Session));
		return false;
	}
	if (Buildings.Num() < BCount)
	{
		Buildings.SetNum(BCount);
	}
	for (int32 j = 0; j < BCount; ++j)
	{
		const ChimeraBuilding& B = BuildingRows[j];
		FBuildingSlot& S = Buildings[j];
		const bool bAlive = B.alive != 0;
		if (bAlive)
		{
			const FString Def = Lib.BuildingDefId(Session, j);
			if (S.Group < 0 || !S.bAlive || S.Type != B.type || S.Faction != B.faction || S.DefId != Def)
			{
				if (S.Group >= 0)
				{
					FChimeraDrawGroup& Old = Groups[S.Group];
					Old.Transforms[S.Index] = BuildingTransform(Old, B, false);
				}
				const int32 Group = FindOrCreateGroup(B.faction, Def, true, 2);
				S.Group = Group;
				S.Index = AddInstance(Group, BuildingTransform(Groups[Group], B, true), OutError);
				if (S.Index == INDEX_NONE)
				{
					return false;
				}
				BuildingJournal.Add(FChimeraBuildingAdd{ j, B.faction, Def, Group, S.Index });
				S.Type = B.type;
				S.Faction = B.faction;
				S.DefId = Def;
				++BuildingAdds;
				UE_LOG(LogChimeraSim, Display, TEXT("renderer: building slot %d %s/%s at (%.1f, %.1f) m -> group %d index %d"), j, *FactionName(B.faction), *Def,
					B.pos[0] / 65536.0, B.pos[2] / 65536.0, Group, S.Index);
			}
		}
		if (S.Group >= 0)
		{
			FChimeraDrawGroup& G = Groups[S.Group];
			G.Transforms[S.Index] = BuildingTransform(G, B, bAlive);
		}
		S.bAlive = bAlive;
	}

	// One batch update per component per frame; no render-state recreate (R3 4.2: the instance data manager flushes the changes).
	for (FChimeraDrawGroup& G : Groups)
	{
		if (G.Transforms.Num() > 0)
		{
			Components[G.Component]->BatchUpdateInstancesTransforms(0, TArrayView<const FTransform>(G.Transforms), /*bWorldSpace*/ false,
				/*bMarkRenderStateDirty*/ false, /*bTeleport*/ false);
		}
	}
	return true;
}

void AChimeraUnitRenderer::SetUnitsVisible(bool bVisible)
{
	bUnitsVisible = bVisible;
	for (const FChimeraDrawGroup& G : Groups)
	{
		if (!G.bBuilding)
		{
			Components[G.Component]->SetVisibility(bVisible && !bHide);
		}
	}
}

void AChimeraUnitRenderer::SetExposureHold(bool bHold)
{
	FPostProcessSettings& S = ExposurePost->Settings;
	S.bOverride_AutoExposureSpeedUp = bHold;
	S.bOverride_AutoExposureSpeedDown = bHold;
	S.AutoExposureSpeedUp = HeldExposureSpeed;
	S.AutoExposureSpeedDown = HeldExposureSpeed;
}

UInstancedStaticMeshComponent* AChimeraUnitRenderer::GetComponent(int32 Group) const
{
	return Groups.IsValidIndex(Group) ? Components[Groups[Group].Component].Get() : nullptr;
}

FVector AChimeraUnitRenderer::GetSunDirection() const
{
	for (TObjectIterator<UDirectionalLightComponent> It; It; ++It)
	{
		if (It->GetWorld() == GetWorld() && It->IsRegistered())
		{
			return It->GetDirection(); // the light travels along the component's +X (LightComponent.cpp:1450-1453)
		}
	}
	return FVector::ZeroVector;
}

FString AChimeraUnitRenderer::Describe() const
{
	TArray<FString> Parts;
	for (const FChimeraDrawGroup& G : Groups)
	{
		Parts.Add(FString::Printf(TEXT("%s/%s%s x%d"), *FactionName(G.Faction), *G.DefId, G.bProxy ? TEXT("(proxy)") : TEXT(""), G.Transforms.Num()));
	}
	return FString::Join(Parts, TEXT(", "));
}

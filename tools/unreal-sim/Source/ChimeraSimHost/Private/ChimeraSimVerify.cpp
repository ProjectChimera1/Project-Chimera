// Copyright Chimera. See ChimeraSimVerify.h.
#include "ChimeraSimVerify.h"

#include "ChimeraUnitRenderer.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Dom/JsonValue.h"
#include "GameFramework/PlayerController.h"

namespace
{
	TArray<TSharedPtr<FJsonValue>> IntList(const TArray<int32>& V, int32 Max)
	{
		TArray<TSharedPtr<FJsonValue>> Out;
		for (int32 i = 0; i < V.Num() && i < Max; ++i)
		{
			Out.Add(MakeShared<FJsonValueNumber>(V[i]));
		}
		return Out;
	}

	/** An instance is drawn when its scale is non-zero and it is not parked under the ground (plan A 3.7's fallback for dead ids). */
	bool IsDrawnXf(const FTransform& Xf)
	{
		return Xf.GetScale3D().GetAbsMax() > ChimeraSimVerify::VisibleScaleEps && Xf.GetLocation().Z > AChimeraUnitRenderer::UnderGroundCm * 0.5;
	}

	FVector SimToUe(const int32 P[3])
	{
		// UE(X, Y, Z) = 100 * (x, z, y), raw / 65536 m (plan A 3.7).
		return FVector(P[0] * AChimeraUnitRenderer::PosScale, P[2] * AChimeraUnitRenderer::PosScale, P[1] * AChimeraUnitRenderer::PosScale);
	}
}

TSharedRef<FJsonObject> FChimeraVerifyResult::ToJson() const
{
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetNumberField(TEXT("tick"), Tick);
	J->SetNumberField(TEXT("rows"), Rows);
	J->SetNumberField(TEXT("alive"), Alive);
	J->SetNumberField(TEXT("phased"), Phased);
	J->SetNumberField(TEXT("expected_visible"), Expected);
	J->SetNumberField(TEXT("visible"), Visible);
	J->SetBoolField(TEXT("visible_eq_alive"), Visible == Alive && DeadVisible == 0 && AliveHidden == 0);
	J->SetNumberField(TEXT("dead_visible"), DeadVisible);
	J->SetNumberField(TEXT("alive_hidden"), AliveHidden);
	J->SetNumberField(TEXT("no_instance"), NoInstance);
	J->SetNumberField(TEXT("group_mismatch"), GroupMismatch);
	J->SetNumberField(TEXT("max_err_cm"), MaxErrCm);
	J->SetNumberField(TEXT("max_err_id"), MaxErrId);
	J->SetNumberField(TEXT("stats_alive"), (double)StatsAlive);
	J->SetNumberField(TEXT("initial_rows"), InitialRows);
	J->SetNumberField(TEXT("new_ids_alive"), NewIdsAlive);
	J->SetNumberField(TEXT("new_ids_visible"), NewIdsVisible);
	J->SetNumberField(TEXT("drawn_instances"), DrawnInstances);
	J->SetBoolField(TEXT("units_hidden_by_option"), bUnitsHiddenByOption);
	J->SetNumberField(TEXT("building_rows"), BuildingRows);
	J->SetNumberField(TEXT("buildings_alive"), BuildingsAlive);
	J->SetNumberField(TEXT("buildings_visible"), BuildingsVisible);
	J->SetNumberField(TEXT("building_dead_visible"), BuildingDeadVisible);
	J->SetNumberField(TEXT("building_alive_hidden"), BuildingAliveHidden);
	J->SetNumberField(TEXT("building_group_mismatch"), BuildingGroupMismatch);
	J->SetNumberField(TEXT("building_drawn_instances"), BuildingDrawnInstances);
	J->SetNumberField(TEXT("building_max_err_cm"), BuildingMaxErrCm);
	J->SetNumberField(TEXT("building_max_err_slot"), BuildingMaxErrSlot);
	J->SetNumberField(TEXT("initial_building_adds"), InitialBuildingAdds);
	J->SetNumberField(TEXT("ai_created_alive"), AiCreatedAlive);
	J->SetNumberField(TEXT("ai_created_visible"), AiCreatedVisible);
	J->SetArrayField(TEXT("ai_created"), AiCreated);
	J->SetBoolField(TEXT("buildings_pass"), bBuildingsPass);
	J->SetArrayField(TEXT("dead_visible_ids"), IntList(DeadVisibleIds, 50));
	J->SetArrayField(TEXT("alive_hidden_ids"), IntList(AliveHiddenIds, 50));
	J->SetBoolField(TEXT("pass"), bPass);
	J->SetArrayField(TEXT("shots"), Shots);
	return J;
}

bool ChimeraSimVerify::Run(int32 Session, const AChimeraUnitRenderer& Renderer, int32 Tick, FChimeraVerifyResult& Out, FString& OutError)
{
	FChimeraSimLibrary& Lib = FChimeraSimLibrary::Get();
	const FChimeraSimApi& Api = Lib.Api();
	Out = FChimeraVerifyResult();
	Out.Tick = Tick;
	Out.InitialRows = Renderer.GetInitialUnitRows();
	Out.bUnitsHiddenByOption = Renderer.IsHidingUnits();
	const bool bIgnoreComponentVisibility = Out.bUnitsHiddenByOption;

	// 1. A fresh read into a buffer of our own.
	int32 Count = 0;
	Lib.Call(Api.ReadUnits, Session, (ChimeraUnit*)nullptr, 0, &Count);
	Out.Units.SetNumZeroed(FMath::Max(Count, 1));
	const int32 Rc = Lib.Call(Api.ReadUnits, Session, Out.Units.GetData(), Out.Units.Num(), &Count);
	if (Rc != CHIMERA_OK)
	{
		OutError = FString::Printf(TEXT("verify read_units rc=%d: %s"), Rc, *Lib.LastError(Session));
		return false;
	}
	Out.Units.SetNum(Count);
	Out.Rows = Count;
	int64 Stats[CHIMERA_STATS_COUNT] = {};
	if (Lib.Call(Api.Stats, Session, Stats) == CHIMERA_OK)
	{
		Out.StatsAlive = Stats[6];
	}

	// 2. Our own id -> (generation, group, index) map, rebuilt from the creation journal (the last entry per id wins).
	struct FRef { int32 Gen; int32 Group; int32 Index; };
	TMap<int32, FRef> Map;
	for (const FChimeraInstanceAdd& A : Renderer.GetJournal())
	{
		Map.Add(A.Id, FRef{ A.Generation, A.Group, A.Index });
	}

	// 3. Compare every id with the ISM's own transform.
	const TArray<FChimeraDrawGroup>& Groups = Renderer.GetGroups();
	Out.GroupOf.Init(-1, Count);
	for (int32 i = 0; i < Count; ++i)
	{
		const ChimeraUnit& U = Out.Units[i];
		const int32 Gen = (int32)((uint32)U.ref >> 12);
		const bool bAlive = (U.flags & 1) != 0;
		const bool bPhased = (U.flags & 8) != 0;
		const bool bExpected = bAlive && !bPhased;
		Out.Alive += bAlive ? 1 : 0;
		Out.Phased += (bAlive && bPhased) ? 1 : 0;
		Out.Expected += bExpected ? 1 : 0;
		const bool bNew = U.id >= Out.InitialRows;
		if (bNew && bAlive)
		{
			++Out.NewIdsAlive;
		}

		bool bVisible = false;
		const FRef* R = Map.Find(U.id);
		if (R != nullptr && R->Gen == Gen && Groups.IsValidIndex(R->Group))
		{
			Out.GroupOf[i] = R->Group;
			const UInstancedStaticMeshComponent* C = Renderer.GetComponent(R->Group);
			FTransform Xf;
			if (C != nullptr && C->GetInstanceTransform(R->Index, Xf, /*bWorldSpace*/ true))
			{
				bVisible = (bIgnoreComponentVisibility || C->IsVisible()) && IsDrawnXf(Xf);
				if (bVisible && bExpected)
				{
					const FChimeraDrawGroup& G = Groups[R->Group];
					const FVector Want = SimToUe(U.pos);
					const FVector Got = Xf.GetLocation() - FVector(0.0, 0.0, G.FootZCm);
					const double Err = FVector::Dist(Want, Got);
					if (Err > Out.MaxErrCm)
					{
						Out.MaxErrCm = Err;
						Out.MaxErrId = U.id;
					}
					// The group must be this row's faction and its fresh definition id.
					if (G.Faction != U.faction || G.DefId != Lib.UnitDefId(Session, U.id))
					{
						++Out.GroupMismatch;
					}
				}
			}
		}
		if (bVisible)
		{
			++Out.Visible;
			if (bNew)
			{
				++Out.NewIdsVisible;
			}
			if (!bExpected)
			{
				++Out.DeadVisible;
				Out.DeadVisibleIds.Add(U.id);
			}
		}
		else if (bExpected)
		{
			++Out.AliveHidden;
			Out.AliveHiddenIds.Add(U.id);
			if (R == nullptr || R->Gen != Gen)
			{
				++Out.NoInstance;
			}
		}
	}
	// 4. Drawn instances over every ISM, without the journals: an instance the maps cannot reach (an old generation left drawn,
	//    or one never journalled) would make these exceed Expected / BuildingsAlive.
	for (int32 g = 0; g < Groups.Num(); ++g)
	{
		const UInstancedStaticMeshComponent* C = Renderer.GetComponent(g);
		if (C == nullptr)
		{
			continue;
		}
		const bool bCompVisible = (Groups[g].bBuilding ? false : bIgnoreComponentVisibility) || C->IsVisible();
		const int32 N = C->GetInstanceCount();
		int32 Drawn = 0;
		for (int32 k = 0; k < N; ++k)
		{
			FTransform Xf;
			if (bCompVisible && C->GetInstanceTransform(k, Xf, /*bWorldSpace*/ true) && IsDrawnXf(Xf))
			{
				++Drawn;
			}
		}
		(Groups[g].bBuilding ? Out.BuildingDrawnInstances : Out.DrawnInstances) += Drawn;
	}

	// 5. Buildings: a fresh chimera_read_buildings into a buffer of our own; slot -> current (group, index) from the building
	//    journal (the last entry per slot wins: slots are recycled, chimera_sim.h); every alive slot must be drawn in the group of
	//    its faction and fresh definition id at 100 * map(pos), every dead slot's last instance hidden.
	TArray<ChimeraBuilding> BRows;
	int32 BCount = 0;
	Lib.Call(Api.ReadBuildings, Session, (ChimeraBuilding*)nullptr, 0, &BCount);
	BRows.SetNumZeroed(FMath::Max(BCount, 1));
	const int32 BRc = Lib.Call(Api.ReadBuildings, Session, BRows.GetData(), BRows.Num(), &BCount);
	if (BRc != CHIMERA_OK)
	{
		OutError = FString::Printf(TEXT("verify read_buildings rc=%d: %s"), BRc, *Lib.LastError(Session));
		return false;
	}
	Out.BuildingRows = BCount;
	Out.InitialBuildingAdds = Renderer.GetInitialBuildingAdds();
	struct FBRef { int32 Group; int32 Index; int32 JournalPos; };
	TMap<int32, FBRef> BMap;
	const TArray<FChimeraBuildingAdd>& BJ = Renderer.GetBuildingJournal();
	for (int32 k = 0; k < BJ.Num(); ++k)
	{
		BMap.Add(BJ[k].Slot, FBRef{ BJ[k].Group, BJ[k].Index, k });
	}
	for (int32 j = 0; j < BCount; ++j)
	{
		const ChimeraBuilding& B = BRows[j];
		const bool bAlive = B.alive != 0;
		Out.BuildingsAlive += bAlive ? 1 : 0;
		const FBRef* R = BMap.Find(B.slot);
		bool bVisible = false;
		if (R != nullptr && Groups.IsValidIndex(R->Group))
		{
			const UInstancedStaticMeshComponent* C = Renderer.GetComponent(R->Group);
			FTransform Xf;
			if (C != nullptr && C->GetInstanceTransform(R->Index, Xf, /*bWorldSpace*/ true))
			{
				bVisible = C->IsVisible() && IsDrawnXf(Xf);
				if (bVisible && bAlive)
				{
					const FChimeraDrawGroup& G = Groups[R->Group];
					const double Err = FVector::Dist(SimToUe(B.pos), Xf.GetLocation() - FVector(0.0, 0.0, G.FootZCm));
					if (Err > Out.BuildingMaxErrCm)
					{
						Out.BuildingMaxErrCm = Err;
						Out.BuildingMaxErrSlot = B.slot;
					}
					if (!G.bBuilding || G.Faction != B.faction || G.DefId != Lib.BuildingDefId(Session, B.slot))
					{
						++Out.BuildingGroupMismatch;
					}
				}
			}
		}
		if (bAlive && bVisible)
		{
			++Out.BuildingsVisible;
		}
		else if (bAlive)
		{
			++Out.BuildingAliveHidden;
		}
		else if (bVisible)
		{
			++Out.BuildingDeadVisible;
		}
		if (bAlive && R != nullptr && R->JournalPos >= Out.InitialBuildingAdds)
		{
			++Out.AiCreatedAlive;
			Out.AiCreatedVisible += bVisible ? 1 : 0;
			TArray<TSharedPtr<FJsonValue>> E = { MakeShared<FJsonValueNumber>(B.slot), MakeShared<FJsonValueNumber>(B.faction),
				MakeShared<FJsonValueString>(Lib.BuildingDefId(Session, B.slot)), MakeShared<FJsonValueNumber>(bVisible ? 1 : 0) };
			Out.AiCreated.Add(MakeShared<FJsonValueArray>(E));
		}
	}
	Out.bBuildingsPass = Out.BuildingsVisible == Out.BuildingsAlive && Out.BuildingDeadVisible == 0 && Out.BuildingAliveHidden == 0
		&& Out.BuildingGroupMismatch == 0 && Out.BuildingDrawnInstances == Out.BuildingsAlive && Out.BuildingMaxErrCm <= MaxErrCmBar
		&& Out.AiCreatedVisible == Out.AiCreatedAlive;

	Out.bPass = Out.DeadVisible == 0 && Out.AliveHidden == 0 && Out.GroupMismatch == 0 && Out.Visible == Out.Expected
		&& Out.DrawnInstances == Out.Expected && Out.MaxErrCm <= MaxErrCmBar && (Out.StatsAlive < 0 || Out.StatsAlive == Out.Alive)
		&& Out.bBuildingsPass;
	return true;
}

TSharedRef<FJsonObject> ChimeraSimVerify::ProjectShot(const APlayerController& PC, const AChimeraUnitRenderer& Renderer, const FChimeraVerifyResult& V,
	const FIntPoint& ViewportSize, int32 SamplesPerArmy)
{
	const TArray<FChimeraDrawGroup>& Groups = Renderer.GetGroups();
	const FVector Sun = Renderer.GetSunDirection();
	TSharedRef<FJsonObject> Armies = MakeShared<FJsonObject>();
	const FBox2D Screen(FVector2D(0.0, 0.0), FVector2D(ViewportSize.X, ViewportSize.Y));

	for (uint8 Faction = 1; Faction <= 2; ++Faction)
	{
		TArray<int32> Ids; // rows of this army that must be visible
		for (int32 i = 0; i < V.Units.Num(); ++i)
		{
			const ChimeraUnit& U = V.Units[i];
			if (U.faction == Faction && (U.flags & 1) != 0 && (U.flags & 8) == 0 && V.GroupOf[i] >= 0)
			{
				Ids.Add(i);
			}
		}
		// Sample: min(SamplesPerArmy, n) alive ids spread evenly over the id order (fractional id stride).
		const int32 NSamples = FMath::Min(SamplesPerArmy, Ids.Num());
		TSet<int32> Pick;
		for (int32 s = 0; s < NSamples; ++s)
		{
			Pick.Add((int32)(((int64)s * Ids.Num()) / NSamples));
		}
		TArray<TSharedPtr<FJsonValue>> Points;
		TArray<TSharedPtr<FJsonValue>> UnitBoxes; // [x0, y0, x1, y1] per unit whose footprint reaches the screen
		int32 OnScreen = 0;
		double SumX = 0.0;
		double SumWorldX = 0.0;
		FBox2D Box(ForceInit);
		for (int32 k = 0; k < Ids.Num(); ++k)
		{
			const ChimeraUnit& U = V.Units[Ids[k]];
			const FChimeraDrawGroup& G = Groups[V.GroupOf[Ids[k]]];
			const FVector Foot = SimToUe(U.pos);
			const FVector Top = Foot + FVector(0.0, 0.0, G.HeightCm);
			// Box corners: foot, top, and the top's shadow on the ground plane of the foot (the sun's ray from the top).
			TArray<FVector, TInlineAllocator<3>> Corners = { Foot, Top };
			if (Sun.Z < -0.05)
			{
				Corners.Add(Top + Sun * (G.HeightCm / -Sun.Z));
			}
			FBox2D UBox(ForceInit);
			for (const FVector& W : Corners)
			{
				FVector2D S;
				if (PC.ProjectWorldLocationToScreen(W, S, false))
				{
					Box += S;
				}
				// The unit's own footprint: each corner widened by the mesh radius along world X and Y.
				for (int32 c = 0; c < 4; ++c)
				{
					const FVector Wc = W + FVector((c & 1) ? G.RadiusCm : -G.RadiusCm, (c & 2) ? G.RadiusCm : -G.RadiusCm, 0.0);
					FVector2D Sc;
					if (PC.ProjectWorldLocationToScreen(Wc, Sc, false))
					{
						UBox += Sc;
					}
				}
			}
			if (UBox.bIsValid && UBox.Max.X >= 0.0 && UBox.Max.Y >= 0.0 && UBox.Min.X < ViewportSize.X && UBox.Min.Y < ViewportSize.Y)
			{
				TArray<TSharedPtr<FJsonValue>> UB = {
					MakeShared<FJsonValueNumber>(FMath::FloorToDouble(FMath::Max(UBox.Min.X, 0.0))),
					MakeShared<FJsonValueNumber>(FMath::FloorToDouble(FMath::Max(UBox.Min.Y, 0.0))),
					MakeShared<FJsonValueNumber>(FMath::CeilToDouble(FMath::Min(UBox.Max.X, (double)ViewportSize.X))),
					MakeShared<FJsonValueNumber>(FMath::CeilToDouble(FMath::Min(UBox.Max.Y, (double)ViewportSize.Y))) };
				UnitBoxes.Add(MakeShared<FJsonValueArray>(UB));
			}
			if (Pick.Contains(k))
			{
				FVector2D S;
				const bool bOk = PC.ProjectWorldLocationToScreen(Foot + FVector(0.0, 0.0, G.HeightCm * 0.5), S, false);
				const bool bIn = bOk && S.X >= 0.0 && S.Y >= 0.0 && S.X < ViewportSize.X && S.Y < ViewportSize.Y;
				TArray<TSharedPtr<FJsonValue>> P;
				P.Add(MakeShared<FJsonValueNumber>(FMath::RoundToDouble(S.X * 10.0) / 10.0));
				P.Add(MakeShared<FJsonValueNumber>(FMath::RoundToDouble(S.Y * 10.0) / 10.0));
				P.Add(MakeShared<FJsonValueNumber>(U.id));
				P.Add(MakeShared<FJsonValueNumber>(bIn ? 1 : 0));
				P.Add(MakeShared<FJsonValueNumber>(FMath::RoundToDouble(Foot.X))); // world X (cm) from the fresh read, for the mirroring check
				Points.Add(MakeShared<FJsonValueArray>(P));
				if (bIn)
				{
					++OnScreen;
					SumX += S.X;
					SumWorldX += Foot.X;
				}
			}
		}
		TSharedRef<FJsonObject> A = MakeShared<FJsonObject>();
		A->SetNumberField(TEXT("faction"), Faction);
		A->SetStringField(TEXT("name"), Faction == 1 ? TEXT("alpha") : TEXT("beta"));
		A->SetNumberField(TEXT("alive_drawn"), Ids.Num());
		A->SetNumberField(TEXT("sampled"), Points.Num());
		A->SetNumberField(TEXT("stride"), NSamples > 0 ? (double)Ids.Num() / NSamples : 0.0);
		A->SetNumberField(TEXT("on_screen"), OnScreen);
		A->SetNumberField(TEXT("mean_x"), OnScreen > 0 ? SumX / OnScreen : -1.0);
		A->SetNumberField(TEXT("mean_world_x_cm"), OnScreen > 0 ? SumWorldX / OnScreen : 0.0);
		TArray<TSharedPtr<FJsonValue>> B;
		if (Box.bIsValid)
		{
			// Clamped to the screen; check_shots.py dilates it.
			const FVector2D Mn(FMath::Clamp(Box.Min.X, 0.0, (double)ViewportSize.X), FMath::Clamp(Box.Min.Y, 0.0, (double)ViewportSize.Y));
			const FVector2D Mx(FMath::Clamp(Box.Max.X, 0.0, (double)ViewportSize.X), FMath::Clamp(Box.Max.Y, 0.0, (double)ViewportSize.Y));
			B = { MakeShared<FJsonValueNumber>(FMath::FloorToDouble(Mn.X)), MakeShared<FJsonValueNumber>(FMath::FloorToDouble(Mn.Y)),
				MakeShared<FJsonValueNumber>(FMath::CeilToDouble(Mx.X)), MakeShared<FJsonValueNumber>(FMath::CeilToDouble(Mx.Y)) };
		}
		A->SetArrayField(TEXT("box"), B);
		A->SetArrayField(TEXT("points"), Points);
		A->SetArrayField(TEXT("unit_boxes"), UnitBoxes);
		Armies->SetObjectField(FString::Printf(TEXT("%d"), (int32)Faction), A);
	}
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetNumberField(TEXT("viewport_w"), ViewportSize.X);
	J->SetNumberField(TEXT("viewport_h"), ViewportSize.Y);
	TArray<TSharedPtr<FJsonValue>> SunJ = { MakeShared<FJsonValueNumber>(Sun.X), MakeShared<FJsonValueNumber>(Sun.Y), MakeShared<FJsonValueNumber>(Sun.Z) };
	J->SetArrayField(TEXT("sun_dir"), SunJ);
	J->SetObjectField(TEXT("armies"), Armies);
	return J;
}

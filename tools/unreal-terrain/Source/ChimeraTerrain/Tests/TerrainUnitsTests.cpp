// Project Chimera terrain trial (plan C 4 C9). Original Chimera code.
// Automation tests of the unit layer's pure part (Chimera.Terrain.Units.*): layout, standing on the drawn surface, and "only the units that read changed
// vertices are re-posed, and that equals a full recompute" (the dirty-rect rule of Data/TerrainUnits.h, proven by single-vertex edits and by brush
// strokes with undo and redo). Pure data, no rendering, so they run under -nullrhi.
// Run: Tools/run_tests.ps1 -Filter Chimera.Terrain.Units

#include "CoreMinimal.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Misc/AutomationTest.h"
#include "Data/TerrainHeightfield.h"
#include "Data/TerrainBrush.h"
#include "Data/TerrainUndo.h"
#include "Data/TerrainUnits.h"

using namespace ChimeraTerrain;

#define UN_FLAGS (EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)
#define UN_TEST(Name) \
	IMPLEMENT_SIMPLE_AUTOMATION_TEST(FChimeraTerrainUnits##Name##Test, "Chimera.Terrain.Units." #Name, UN_FLAGS) \
	bool FChimeraTerrainUnits##Name##Test::RunTest(const FString& Parameters)

namespace
{
	/** Deterministic test random numbers (no engine random: the tests themselves must repeat). */
	struct FUnRng
	{
		uint32 S;
		explicit FUnRng(uint32 Seed) : S(Seed) {}
		uint32 Next()
		{
			S = S * 1664525u + 1013904223u;
			uint32 H = S;
			H ^= H >> 16;
			H *= 0x45D9F3Bu;
			H ^= H >> 16;
			return H;
		}
		int32 Range(int32 Lo, int32 Hi) { return Lo + static_cast<int32>(Next() % static_cast<uint32>(Hi - Lo + 1)); }
		float Frac() { return static_cast<float>(Next() >> 8) / 16777216.0f; }
		float Between(float Lo, float Hi) { return Lo + (Hi - Lo) * Frac(); }
	};

	/** A rolling, never-flat map: a few random bumps over a gentle tilt, including steep flanks. */
	void UnMakeMap(FTerrainHeightfield& HF, int32 Half, uint32 Seed)
	{
		HF.Init(Half, 32);
		FUnRng R(Seed);
		struct FBump { float X, Y, Rad, H; };
		TArray<FBump> Bumps;
		for (int32 I = 0; I < 14; ++I)
		{
			Bumps.Add({R.Between(-Half * 0.8f, Half * 0.8f), R.Between(-Half * 0.8f, Half * 0.8f), R.Between(6.0f, 30.0f), R.Between(-12.0f, 28.0f)});
		}
		for (int32 Y = 0; Y < HF.Width(); ++Y)
		{
			for (int32 X = 0; X < HF.Width(); ++X)
			{
				const float WX = HF.VertexToWorld(X);
				const float WY = HF.VertexToWorld(Y);
				float H = 0.04f * WX - 0.02f * WY;
				for (const FBump& B : Bumps)
				{
					const float D2 = (WX - B.X) * (WX - B.X) + (WY - B.Y) * (WY - B.Y);
					H += B.H * FMath::Exp(-D2 / (2.0f * B.Rad * B.Rad));
				}
				HF.SetHeight(X, Y, H);
			}
		}
	}

	bool UnSame(const FTransform& A, const FTransform& B)
	{
		return A.GetLocation() == B.GetLocation() && A.GetRotation() == B.GetRotation() && A.GetScale3D() == B.GetScale3D();
	}
}

UN_TEST(LayoutIsDeterministicInsideTheMap)
{
	bool bOk = true;
	for (const int32 Count : {1, 7, 300, 1000, 2000})
	{
		TArray<FUnitSlot> A;
		TArray<FUnitSlot> B;
		BuildUnitLayout(Count, 160, A);
		BuildUnitLayout(Count, 160, B);
		if (A.Num() != Count || B.Num() != Count)
		{
			AddError(FString::Printf(TEXT("count %d: layout holds %d / %d"), Count, A.Num(), B.Num()));
			bOk = false;
			continue;
		}
		double MinDist = TNumericLimits<double>::Max();
		for (int32 I = 0; I < Count; ++I)
		{
			if (A[I].X != B[I].X || A[I].Y != B[I].Y || A[I].YawDeg != B[I].YawDeg)
			{
				AddError(FString::Printf(TEXT("count %d: layout differs between two builds at %d"), Count, I));
				bOk = false;
				break;
			}
			if (FMath::Abs(A[I].X) > 158.0f || FMath::Abs(A[I].Y) > 158.0f)
			{
				AddError(FString::Printf(TEXT("count %d: unit %d at (%.1f, %.1f) is outside the map margin"), Count, I, A[I].X, A[I].Y));
				bOk = false;
				break;
			}
			if (A[I].YawDeg < 0.0f || A[I].YawDeg > 360.0f)
			{
				AddError(FString::Printf(TEXT("count %d: yaw %f out of range"), Count, A[I].YawDeg));
				bOk = false;
				break;
			}
		}
		if (Count <= 1000)
		{
			for (int32 I = 0; I < Count; ++I)
			{
				for (int32 J = I + 1; J < Count; ++J)
				{
					MinDist = FMath::Min(MinDist, FVector2D::Distance(FVector2D(A[I].X, A[I].Y), FVector2D(A[J].X, A[J].Y)));
				}
			}
			// Jitter is 0.3 spacing each way: neighbours stay at least 0.4 spacing apart; a 3.6 m unit then overlaps only its immediate neighbour's corner.
			if (Count >= 300 && MinDist < 3.0)
			{
				AddError(FString::Printf(TEXT("count %d: two units only %.2f m apart"), Count, MinDist));
				bOk = false;
			}
		}
	}
	// The layout depends only on (Count, HalfExtentM): a smaller map packs the same count closer, never outside it.
	TArray<FUnitSlot> Small;
	BuildUnitLayout(1000, 64, Small);
	for (const FUnitSlot& S : Small)
	{
		if (FMath::Abs(S.X) > 62.0f || FMath::Abs(S.Y) > 62.0f)
		{
			AddError(TEXT("1000 units on a 64 m half extent left the map"));
			bOk = false;
			break;
		}
	}
	return bOk;
}

UN_TEST(UnitsStandOnTheDrawnSurface)
{
	FTerrainHeightfield HF;
	UnMakeMap(HF, 160, 4242);
	TArray<FUnitSlot> Slots;
	BuildUnitLayout(1000, 160, Slots);
	double MaxZErrCm = 0.0;
	double MaxUpErr = 0.0;
	double MaxTilt = 0.0;
	bool bOk = true;
	for (const FUnitSlot& S : Slots)
	{
		const FTransform T = UnitTransform(HF, S);
		const FVector L = T.GetLocation();
		// Z is exactly the drawn surface at the unit's own x, y (HF.SampleSurface, metres -> cm), x and y are the slot's.
		MaxZErrCm = FMath::Max(MaxZErrCm, FMath::Abs(L.Z - static_cast<double>(HF.SampleSurface(S.X, S.Y)) * 100.0));
		if (FMath::Abs(L.X - static_cast<double>(S.X) * 100.0) > 1.0e-9 || FMath::Abs(L.Y - static_cast<double>(S.Y) * 100.0) > 1.0e-9)
		{
			AddError(TEXT("unit x, y differ from its slot"));
			bOk = false;
			break;
		}
		if (FMath::Abs(T.GetScale3D().X - UnitScale) > 1.0e-12 || !T.GetRotation().IsNormalized())
		{
			AddError(TEXT("scale or rotation of a unit is wrong"));
			bOk = false;
			break;
		}
		// The unit's up axis is the surface normal from central differences at +-UnitProbeM.
		const double D = UnitProbeM;
		const double Dx = (static_cast<double>(HF.SampleSurface(S.X + D, S.Y)) - HF.SampleSurface(S.X - D, S.Y)) / (2.0 * D);
		const double Dy = (static_cast<double>(HF.SampleSurface(S.X, S.Y + D)) - HF.SampleSurface(S.X, S.Y - D)) / (2.0 * D);
		const FVector N = FVector(-Dx, -Dy, 1.0).GetSafeNormal();
		const FVector Up = T.GetRotation().GetUpVector();
		MaxUpErr = FMath::Max(MaxUpErr, (Up - N).Size());
		MaxTilt = FMath::Max(MaxTilt, FMath::RadiansToDegrees(FMath::Acos(FMath::Clamp(N.Z, -1.0, 1.0))));
	}
	if (MaxZErrCm > 1.0e-9 || MaxUpErr > 1.0e-9)
	{
		AddError(FString::Printf(TEXT("pose errors: z %.3e cm, up axis %.3e"), MaxZErrCm, MaxUpErr));
		bOk = false;
	}
	if (MaxTilt < 3.0)
	{
		AddError(FString::Printf(TEXT("the test map is too flat to prove the tilt (max slope %.2f deg)"), MaxTilt));
		bOk = false;
	}
	// A flat map: no tilt, z = 0, a unit's yaw is the only rotation.
	FTerrainHeightfield Flat;
	Flat.Init(160, 32);
	for (const FUnitSlot& S : Slots)
	{
		const FTransform T = UnitTransform(Flat, S);
		if (T.GetLocation().Z != 0.0 || FMath::Abs(T.GetRotation().GetUpVector().Z - 1.0) > 1.0e-12)
		{
			AddError(TEXT("a unit on flat ground is not upright at z 0"));
			bOk = false;
			break;
		}
	}
	return bOk;
}

UN_TEST(DependencyRuleCoversEveryChangedUnit)
{
	// Edit one vertex at a time (a bump of 3 m), recompute every unit in full, and require that each unit whose pose changed is flagged by
	// UnitDependsOnVertexRect for the rect [v, v+1). The rule may flag more (it is conservative by a cell) but never fewer.
	FTerrainHeightfield HF;
	UnMakeMap(HF, 160, 99);
	TArray<FUnitSlot> Slots;
	BuildUnitLayout(300, 160, Slots);
	TArray<FTransform> Base;
	for (const FUnitSlot& S : Slots)
	{
		Base.Add(UnitTransform(HF, S));
	}
	FUnRng R(7);
	int32 Tries = 0;
	int32 ChangedUnits = 0;
	int32 Over = 0;
	bool bOk = true;
	for (int32 K = 0; K < 400 && bOk; ++K)
	{
		// Aim at a unit half the time so the edit lands near units; otherwise anywhere.
		int32 VX;
		int32 VY;
		if (K % 2 == 0)
		{
			const FUnitSlot& S = Slots[R.Range(0, Slots.Num() - 1)];
			VX = FMath::FloorToInt(S.X) + 160 + R.Range(-3, 4);
			VY = FMath::FloorToInt(S.Y) + 160 + R.Range(-3, 4);
		}
		else
		{
			VX = R.Range(0, HF.Width() - 1);
			VY = R.Range(0, HF.Width() - 1);
		}
		VX = FMath::Clamp(VX, 0, HF.Width() - 1);
		VY = FMath::Clamp(VY, 0, HF.Width() - 1);
		const float Old = HF.GetHeight(VX, VY);
		HF.SetHeight(VX, VY, Old + 3.0f);
		const FTerrainRect Rect(VX, VY, VX + 1, VY + 1);
		++Tries;
		for (int32 I = 0; I < Slots.Num(); ++I)
		{
			const FTransform Now = UnitTransform(HF, Slots[I]);
			const bool bChanged = !UnSame(Now, Base[I]);
			const bool bFlagged = UnitDependsOnVertexRect(Slots[I], 160, Rect);
			if (bChanged)
			{
				++ChangedUnits;
				if (!bFlagged)
				{
					AddError(FString::Printf(TEXT("unit %d at (%.2f, %.2f) changed for an edit of vertex (%d, %d) but the rule did not flag it"), I, Slots[I].X, Slots[I].Y, VX, VY));
					bOk = false;
					break;
				}
			}
			else if (bFlagged)
			{
				++Over;
			}
		}
		HF.SetHeight(VX, VY, Old);
	}
	if (ChangedUnits < 20)
	{
		AddError(FString::Printf(TEXT("only %d changed units over %d edits: the test proves nothing"), ChangedUnits, Tries));
		bOk = false;
	}
	AddInfo(FString::Printf(TEXT("%d single-vertex edits, %d unit poses changed (all flagged), %d flagged without a change (the cell of slack)"), Tries, ChangedUnits, Over));
	return bOk;
}

UN_TEST(IncrementalUpdateEqualsFullRecompute)
{
	// Brush strokes (all modes) with undo and redo, as the game applies them: each tick's HeightRect marks units, only those are re-posed. After
	// every tick, stroke end, undo and redo the incrementally kept poses must equal a full recompute, bit for bit.
	FTerrainHeightfield HF;
	UnMakeMap(HF, 160, 31337);
	FTerrainUndo Undo;
	TArray<FUnitSlot> Slots;
	BuildUnitLayout(300, 160, Slots);
	TArray<FTransform> Kept;
	for (const FUnitSlot& S : Slots)
	{
		Kept.Add(UnitTransform(HF, S));
	}
	int64 Updated = 0;
	int64 Ticks = 0;
	int32 MaxPerFlush = 0;
	bool bOk = true;
	auto Refresh = [&](const FTerrainRect& Rect)
	{
		int32 N = 0;
		for (int32 I = 0; I < Slots.Num(); ++I)
		{
			if (UnitDependsOnVertexRect(Slots[I], 160, Rect))
			{
				const FTransform T = UnitTransform(HF, Slots[I]);
				if (!UnSame(T, Kept[I]))
				{
					Kept[I] = T;
					++N;
				}
			}
		}
		Updated += N;
		MaxPerFlush = FMath::Max(MaxPerFlush, N);
	};
	auto CheckAll = [&](const TCHAR* Where)
	{
		for (int32 I = 0; I < Slots.Num(); ++I)
		{
			if (!UnSame(Kept[I], UnitTransform(HF, Slots[I])))
			{
				AddError(FString::Printf(TEXT("%s: unit %d at (%.2f, %.2f) is stale after the incremental update"), Where, I, Slots[I].X, Slots[I].Y));
				bOk = false;
				return false;
			}
		}
		return true;
	};
	FUnRng R(5);
	const ETerrainBrushMode Modes[] = {ETerrainBrushMode::Raise, ETerrainBrushMode::Lower, ETerrainBrushMode::Smooth, ETerrainBrushMode::Flatten, ETerrainBrushMode::Paint};
	for (int32 Stroke = 0; Stroke < 10 && bOk; ++Stroke)
	{
		FTerrainBrushParams P;
		P.Mode = Modes[Stroke % 5];
		P.DiameterM = R.Between(8.0f, 60.0f);
		P.Strength = R.Between(20.0f, 100.0f);
		P.PaintLayer = 1;
		float CX = R.Between(-70.0f, 70.0f);
		float CY = R.Between(-70.0f, 70.0f);
		P.FlattenTarget = FTerrainBrush::FlattenTargetAt(HF, CX, CY);
		Undo.BeginStroke();
		for (int32 T = 0; T < 12; ++T)
		{
			const FTerrainTickResult Res = FTerrainBrush::ApplyTick(HF, P, CX, CY, &Undo);
			++Ticks;
			Refresh(Res.HeightRect);
			CX += 1.2f;
			CY += 0.5f;
		}
		Undo.EndStroke(HF);
		if (!CheckAll(TEXT("stroke")))
		{
			break;
		}
	}
	// Undo three strokes and redo two: the delta's rect is what the event carries.
	FTerrainEditDelta D;
	for (int32 I = 0; I < 3 && bOk; ++I)
	{
		if (Undo.Undo(HF, D))
		{
			Refresh(D.HeightRect);
			CheckAll(TEXT("undo"));
		}
	}
	for (int32 I = 0; I < 2 && bOk; ++I)
	{
		if (Undo.Redo(HF, D))
		{
			Refresh(D.HeightRect);
			CheckAll(TEXT("redo"));
		}
	}
	const int64 FullEveryTick = Ticks * Slots.Num();
	AddInfo(FString::Printf(TEXT("%lld ticks, %lld unit updates against %lld for a full update every tick, at most %d per flush of %d units"), Ticks, Updated, FullEveryTick, MaxPerFlush, Slots.Num()));
	if (Updated == 0 || Updated * 5 > FullEveryTick || MaxPerFlush >= Slots.Num())
	{
		AddError(TEXT("updates were not local (every tick re-posed most units) or nothing was re-posed"));
		bOk = false;
	}
	return bOk;
}

UN_TEST(FnvChangesWithAPoseAndRepeatsWithout)
{
	FTerrainHeightfield HF;
	UnMakeMap(HF, 160, 8);
	TArray<FUnitSlot> Slots;
	BuildUnitLayout(100, 160, Slots);
	TArray<FTransform> A;
	TArray<FTransform> B;
	for (const FUnitSlot& S : Slots)
	{
		A.Add(UnitTransform(HF, S));
		B.Add(UnitTransform(HF, S));
	}
	bool bOk = UnitsFnv(A) == UnitsFnv(B);
	HF.SetHeight(160 + FMath::RoundToInt(Slots[40].X), 160 + FMath::RoundToInt(Slots[40].Y), HF.GetHeight(160 + FMath::RoundToInt(Slots[40].X), 160 + FMath::RoundToInt(Slots[40].Y)) + 5.0f);
	B[40] = UnitTransform(HF, Slots[40]);
	bOk = bOk && UnitsFnv(A) != UnitsFnv(B);
	if (!bOk)
	{
		AddError(TEXT("units_fnv does not repeat for equal poses or does not change with a pose"));
	}
	return bOk;
}

#endif

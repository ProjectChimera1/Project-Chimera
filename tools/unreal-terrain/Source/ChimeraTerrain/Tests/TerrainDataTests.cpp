// Project Chimera terrain trial (plan C). Original Chimera code.
// Automation tests for the terrain data core (plan C 4 C2). Run: Tools/run_tests.ps1 -Filter Chimera.Terrain.Data

#include "CoreMinimal.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Guid.h"
#include "Misc/Paths.h"
#include "HAL/FileManager.h"
#include "Math/RandomStream.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

#include "Data/TerrainHeightfield.h"
#include "Data/TerrainBrush.h"
#include "Data/TerrainUndo.h"
#include "Data/TerrainPick.h"
#include "Data/TerrainIO.h"
#include "Data/TerrainSimExport.h"

using namespace ChimeraTerrain;

#define TT_FLAGS (EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)
#define TT_TEST(Name) \
	IMPLEMENT_SIMPLE_AUTOMATION_TEST(FChimeraTerrain##Name##Test, "Chimera.Terrain.Data." #Name, TT_FLAGS) \
	bool FChimeraTerrain##Name##Test::RunTest(const FString& Parameters)

namespace
{
	FTerrainBrushParams TTParams(ETerrainBrushMode Mode, float DiameterM, float Strength, int32 Layer = 0, float Target = 0.0f)
	{
		FTerrainBrushParams P;
		P.Mode = Mode;
		P.DiameterM = DiameterM;
		P.Strength = Strength;
		P.PaintLayer = Layer;
		P.FlattenTarget = Target;
		return P;
	}

	/** Absolute-difference check with a readable failure message. */
	bool TTNear(FAutomationTestBase& T, const FString& What, double Actual, double Expected, double Tol)
	{
		if (!(FMath::Abs(Actual - Expected) <= Tol))
		{
			T.AddError(FString::Printf(TEXT("%s: expected %.9g, got %.9g (tol %g)"), *What, Expected, Actual, Tol));
			return false;
		}
		return true;
	}

	bool TTEqU32(FAutomationTestBase& T, const FString& What, uint32 Actual, uint32 Expected)
	{
		if (Actual != Expected)
		{
			T.AddError(FString::Printf(TEXT("%s: expected 0x%08x, got 0x%08x"), *What, Expected, Actual));
			return false;
		}
		return true;
	}

	bool TTEqI64(FAutomationTestBase& T, const FString& What, int64 Actual, int64 Expected)
	{
		if (Actual != Expected)
		{
			T.AddError(FString::Printf(TEXT("%s: expected %lld, got %lld"), *What, Expected, Actual));
			return false;
		}
		return true;
	}

	struct FTTHashes
	{
		uint32 Height = 0;
		uint32 Splat = 0;
	};

	FTTHashes TTHashes(const FTerrainHeightfield& HF)
	{
		FTTHashes H;
		H.Height = HF.HeightFnv();
		H.Splat = HF.SplatFnv();
		return H;
	}

	bool TTExpectHashes(FAutomationTestBase& T, const FString& What, const FTerrainHeightfield& HF, const FTTHashes& Want)
	{
		const bool bH = TTEqU32(T, What + TEXT(" height_fnv"), HF.HeightFnv(), Want.Height);
		const bool bS = TTEqU32(T, What + TEXT(" splat_fnv"), HF.SplatFnv(), Want.Splat);
		return bH && bS;
	}

	/** One whole stroke: Begin, `Ticks` ticks, End. Returns whether an undo entry was pushed. */
	bool TTStroke(FTerrainHeightfield& HF, FTerrainUndo& Undo, const FTerrainBrushParams& P, float X, float Y, int32 Ticks)
	{
		Undo.BeginStroke();
		for (int32 I = 0; I < Ticks; ++I)
		{
			FTerrainBrush::ApplyTick(HF, P, X, Y, &Undo);
		}
		return Undo.EndStroke(HF);
	}

	FString TTTempDir(const TCHAR* Leaf)
	{
		return FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Automation"), TEXT("TerrainData"), FGuid::NewGuid().ToString(EGuidFormats::Digits), Leaf);
	}

	/** The deterministic mixed tick list used by the determinism tests. */
	void TTRunTickList(FTerrainHeightfield& HF, FTerrainUndo* Undo)
	{
		FRandomStream Rand(1234);
		for (int32 I = 0; I < 300; ++I)
		{
			const ETerrainBrushMode Mode = static_cast<ETerrainBrushMode>(Rand.RandRange(0, 4));
			const float D = Rand.FRandRange(5.0f, 100.0f);
			const float S = Rand.FRandRange(1.0f, 100.0f);
			const int32 Layer = Rand.RandRange(0, SplatLayerCount - 1);
			const float Target = Rand.FRandRange(-20.0f, 20.0f);
			const float X = Rand.FRandRange(-100.0f, 100.0f);
			const float Y = Rand.FRandRange(-100.0f, 100.0f);
			if (Undo && (I % 10) == 0)
			{
				if (I > 0)
				{
					Undo->EndStroke(HF);
				}
				Undo->BeginStroke();
			}
			FTerrainBrush::ApplyTick(HF, TTParams(Mode, D, S, Layer, Target), X, Y, Undo);
		}
		if (Undo)
		{
			Undo->EndStroke(HF);
		}
	}
}

// ---- brushes (plan C 3.4) -------------------------------------------------------------------------------------------

TT_TEST(RaiseParity)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	const FTerrainTickResult R = FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Raise, 20.0f, 10.0f), 0.0f, 0.0f);
	// Godot live probe (R7 2.2): d=20, s=10 -> +0.100 at the centre, +0.050 at 5 m, 0 at 10 m.
	TTNear(*this, TEXT("centre"), HF.GetHeight(160, 160), 0.100, 1e-6);
	TTNear(*this, TEXT("5 m +x"), HF.GetHeight(165, 160), 0.050, 1e-6);
	TTNear(*this, TEXT("5 m -x"), HF.GetHeight(155, 160), 0.050, 1e-6);
	TTNear(*this, TEXT("5 m +y"), HF.GetHeight(160, 165), 0.050, 1e-6);
	TTNear(*this, TEXT("5 m -y"), HF.GetHeight(160, 155), 0.050, 1e-6);
	TestTrue(TEXT("10 m is exactly 0"), HF.GetHeight(170, 160) == 0.0f && HF.GetHeight(150, 160) == 0.0f && HF.GetHeight(160, 170) == 0.0f && HF.GetHeight(160, 150) == 0.0f);
	TestTrue(TEXT("outside is exactly 0"), HF.GetHeight(171, 160) == 0.0f && HF.GetHeight(100, 100) == 0.0f);
	TTEqI64(*this, TEXT("vertices with r < 10"), R.VerticesChanged, 305);
	TestTrue(TEXT("changed rect is [151,170)^2"), R.HeightRect == FTerrainRect(151, 151, 170, 170));
	TestTrue(TEXT("a height tick changes no splat"), R.SplatRect.IsEmpty() && R.TexelsChanged == 0);
	return true;
}

TT_TEST(LowerSymmetric)
{
	FTerrainHeightfield Up;
	Up.Init(160, 64);
	FTerrainHeightfield Down;
	Down.Init(160, 64);
	FTerrainBrush::ApplyTick(Up, TTParams(ETerrainBrushMode::Raise, 40.0f, 37.0f), 3.5f, -7.25f);
	FTerrainBrush::ApplyTick(Down, TTParams(ETerrainBrushMode::Lower, 40.0f, 37.0f), 3.5f, -7.25f);
	int32 Mismatches = 0;
	for (int32 I = 0; I < Up.Heights.Num(); ++I)
	{
		if (!(Down.Heights[I] == -Up.Heights[I]))
		{
			++Mismatches;
		}
	}
	TTEqI64(*this, TEXT("vertices where lower != -raise"), Mismatches, 0);

	FTerrainHeightfield Flat;
	Flat.Init(160, 64);
	FTerrainBrush::ApplyTick(Up, TTParams(ETerrainBrushMode::Lower, 40.0f, 37.0f), 3.5f, -7.25f);
	TTEqU32(*this, TEXT("raise then lower returns to flat"), Up.HeightFnv(), Flat.HeightFnv());
	return true;
}

TT_TEST(SmoothReducesSpike)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	HF.SetHeight(160, 160, 10.0f);
	FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Smooth, 20.0f, 50.0f), 0.0f, 0.0f);
	// Tick-start snapshot: avg5 at the centre = 10/5 = 2, blend t = clamp(2*1*0.5,0.02,1) = 1 -> 2.
	TTNear(*this, TEXT("centre after one tick"), HF.GetHeight(160, 160), 2.0, 1e-5);
	// Neighbour (161,160): avg5 = (0 + 10 + 0 + 0 + 0)/5 = 2 from the SNAPSHOT, a = 0.9, t = 0.9 -> 1.8.
	// An in-place read order would see the already-smoothed centre and give a different value.
	TTNear(*this, TEXT("east neighbour (snapshot read)"), HF.GetHeight(161, 160), 1.8, 1e-5);
	TTNear(*this, TEXT("west neighbour"), HF.GetHeight(159, 160), 1.8, 1e-5);
	TTNear(*this, TEXT("north neighbour"), HF.GetHeight(160, 161), 1.8, 1e-5);
	TTNear(*this, TEXT("south neighbour"), HF.GetHeight(160, 159), 1.8, 1e-5);

	float PrevMax = 10.0f;
	for (int32 I = 0; I < 20; ++I)
	{
		FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Smooth, 20.0f, 50.0f), 0.0f, 0.0f);
		float Max = -1e9f;
		for (const float H : HF.Heights)
		{
			Max = FMath::Max(Max, H);
		}
		TestTrue(*FString::Printf(TEXT("peak does not grow at tick %d (%g -> %g)"), I, PrevMax, Max), Max <= PrevMax);
		PrevMax = Max;
	}
	TestTrue(TEXT("the spike has been flattened well below 10"), PrevMax < 1.0f);
	return true;
}

TT_TEST(FlattenConvergesToStrokeStartTarget)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	for (int32 Y = 140; Y <= 180; ++Y)
	{
		for (int32 X = 140; X <= 180; ++X)
		{
			HF.SetHeight(X, Y, 10.0f);
		}
	}
	// The caller fixes the target at stroke start; the brush must keep using it while the ground changes under it.
	FTerrainBrushParams P = TTParams(ETerrainBrushMode::Flatten, 20.0f, 30.0f, 0, 3.0f);
	TTNear(*this, TEXT("FlattenTargetAt reads the surface under the cursor"), FTerrainBrush::FlattenTargetAt(HF, 0.0f, 0.0f), 10.0, 1e-6);
	bool bOvershoot = false;
	for (int32 I = 0; I < 60; ++I)
	{
		FTerrainBrush::ApplyTick(HF, P, 0.0f, 0.0f);
		bOvershoot |= HF.GetHeight(160, 160) < 3.0f;
	}
	TestFalse(TEXT("never overshoots the target"), bOvershoot);
	TTNear(*this, TEXT("centre converged"), HF.GetHeight(160, 160), 3.0, 1e-3);
	TTNear(*this, TEXT("5 m converged"), HF.GetHeight(165, 160), 3.0, 1e-3);
	TestTrue(TEXT("outside the radius untouched"), HF.GetHeight(171, 160) == 10.0f && HF.GetHeight(160, 171) == 10.0f);

	// Stroke-start capture on a slope (h = 0.1 * x): the target is sampled ONCE where the stroke starts (x = -5 m, h = -0.5) and held
	// while the cursor drags uphill to x = +5 m (h = +0.5). Every vertex the stroke reached ends on -0.5, a plateau at the start height.
	{
		auto MakeSlope = [](FTerrainHeightfield& S)
		{
			S.Init(160, 64);
			for (int32 Y = 0; Y < S.Width(); ++Y)
			{
				for (int32 X = 0; X < S.Width(); ++X)
				{
					S.SetHeight(X, Y, 0.1f * static_cast<float>(X - 160));
				}
			}
		};
		auto Drag = [](FTerrainHeightfield& S, bool bResampleEveryTick, float StartTarget)
		{
			FTerrainBrushParams D = TTParams(ETerrainBrushMode::Flatten, 20.0f, 30.0f, 0, StartTarget);
			// A fast drag: 10 ticks at the start point, then the cursor lands 10 m uphill (one brush radius, so the end point is still
			// untouched ground) and stays there for 60 ticks.
			for (int32 Step = 0; Step < 2; ++Step)
			{
				const float Cx = (Step == 0) ? -5.0f : 5.0f;
				const int32 Hold = (Step == 0) ? 10 : 60;
				for (int32 I = 0; I < Hold; ++I)
				{
					if (bResampleEveryTick)
					{
						D.FlattenTarget = FTerrainBrush::FlattenTargetAt(S, Cx, 0.0f);
					}
					FTerrainBrush::ApplyTick(S, D, Cx, 0.0f);
				}
			}
		};
		FTerrainHeightfield Slope;
		MakeSlope(Slope);
		const float StartTarget = FTerrainBrush::FlattenTargetAt(Slope, -5.0f, 0.0f);
		TTNear(*this, TEXT("slope: target sampled at stroke start"), StartTarget, -0.5, 1e-6);
		TTNear(*this, TEXT("slope: ground under the end point before the stroke"), FTerrainBrush::FlattenTargetAt(Slope, 5.0f, 0.0f), 0.5, 1e-6);
		Drag(Slope, false, StartTarget);
		TTNear(*this, TEXT("slope: end point converged to the stroke-start target"), Slope.GetHeight(165, 160), -0.5, 1e-3);
		TTNear(*this, TEXT("slope: 2 m short of the end point"), Slope.GetHeight(163, 160), -0.5, 1e-3);
		TTNear(*this, TEXT("slope: middle of the drag"), Slope.GetHeight(160, 160), -0.5, 1e-3);
		TTNear(*this, TEXT("slope: surface under the end point now equals the target"), FTerrainBrush::FlattenTargetAt(Slope, 5.0f, 0.0f), -0.5, 1e-3);

		// Control: a caller that re-samples the target every tick does NOT produce the plateau; this check would catch it.
		FTerrainHeightfield Resampled;
		MakeSlope(Resampled);
		Drag(Resampled, true, 0.0f);
		TestTrue(*FString::Printf(TEXT("slope control: re-sampling every tick leaves the end point far from -0.5 (got %g)"), Resampled.GetHeight(165, 160)),
			FMath::Abs(Resampled.GetHeight(165, 160) - (-0.5f)) > 0.5f);
	}

	// Strength 100 (k = 1): the centre lands exactly on the target in one tick (t = a*k = 1).
	FTerrainHeightfield HF2;
	HF2.Init(160, 64);
	HF2.SetHeight(160, 160, -9.0f);
	FTerrainBrush::ApplyTick(HF2, TTParams(ETerrainBrushMode::Flatten, 20.0f, 100.0f, 0, 4.0f), 0.0f, 0.0f);
	TestTrue(TEXT("k = 1 centre equals target exactly"), HF2.GetHeight(160, 160) == 4.0f);
	return true;
}

TT_TEST(HeightClamp)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	for (int32 I = 0; I < 200; ++I)
	{
		FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Raise, 20.0f, 100.0f), 0.0f, 0.0f);
	}
	TestTrue(TEXT("raise stops at +128 exactly"), HF.GetHeight(160, 160) == 128.0f);
	for (int32 I = 0; I < 600; ++I)
	{
		FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Lower, 20.0f, 100.0f), 0.0f, 0.0f);
	}
	TestTrue(TEXT("lower stops at -128 exactly"), HF.GetHeight(160, 160) == -128.0f);
	float Min = 1e9f;
	float Max = -1e9f;
	for (const float H : HF.Heights)
	{
		Min = FMath::Min(Min, H);
		Max = FMath::Max(Max, H);
	}
	TestTrue(TEXT("every height within +-128"), Min >= -128.0f && Max <= 128.0f);
	HF.SetHeight(0, 0, 1000.0f);
	HF.SetHeight(1, 0, -1000.0f);
	const uint32 NaNBits = 0x7FC00000u;
	float NaN;
	FMemory::Memcpy(&NaN, &NaNBits, sizeof(NaN));
	HF.SetHeight(2, 0, NaN);
	TestTrue(TEXT("SetHeight clamps and maps NaN to 0"), HF.GetHeight(0, 0) == 128.0f && HF.GetHeight(1, 0) == -128.0f && HF.GetHeight(2, 0) == 0.0f);
	return true;
}

TT_TEST(PaintSumNeverWraps)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	FRandomStream Rand(99);
	for (int32 I = 0; I < 400; ++I)
	{
		FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Paint, Rand.FRandRange(5.0f, 100.0f), Rand.FRandRange(1.0f, 100.0f), I % SplatLayerCount),
			Rand.FRandRange(-90.0f, 90.0f), Rand.FRandRange(-90.0f, 90.0f));
	}
	int32 BadSum = 0;
	int32 Painted = 0;
	const int32 Texels = HF.SplatSize() * HF.SplatSize();
	for (int32 T = 0; T < Texels; ++T)
	{
		const uint8* P = &HF.Splat[T * SplatLayerCount];
		if (static_cast<int32>(P[0]) + P[1] + P[2] + P[3] != 255)
		{
			++BadSum;
		}
		if (P[0] != 255)
		{
			++Painted;
		}
	}
	TTEqI64(*this, TEXT("texels whose weights do not sum to 255"), BadSum, 0);
	TestTrue(TEXT("the strokes painted something"), Painted > 1000);

	// Saturation: a layer already near 255 caps at 255 and takes only what the others hold.
	uint8 A[4] = {250, 5, 0, 0};
	TTEqI64(*this, TEXT("capped delta"), FTerrainBrush::PaintTexel(A, 0, 100), 5);
	TestTrue(TEXT("{250,5,0,0} + 100 on layer 0 -> {255,0,0,0}"), A[0] == 255 && A[1] == 0 && A[2] == 0 && A[3] == 0);
	uint8 B[4] = {255, 0, 0, 0};
	TTEqI64(*this, TEXT("huge delta is capped"), FTerrainBrush::PaintTexel(B, 1, 300), 255);
	TestTrue(TEXT("{255,0,0,0} + 300 on layer 1 -> {0,255,0,0}"), B[0] == 0 && B[1] == 255 && B[2] == 0 && B[3] == 0);
	TTEqI64(*this, TEXT("painting a full layer is a no-op"), FTerrainBrush::PaintTexel(B, 1, 50), 0);
	TTEqI64(*this, TEXT("zero delta is a no-op"), FTerrainBrush::PaintTexel(B, 2, 0), 0);
	TestTrue(TEXT("no-ops leave the texel alone"), B[0] == 0 && B[1] == 255 && B[2] == 0 && B[3] == 0);
	return true;
}

TT_TEST(PaintRemainderRule)
{
	// Equal remainders: ties go to the lower layer id.
	uint8 A[4] = {85, 85, 85, 0};
	FTerrainBrush::PaintTexel(A, 3, 1);
	TestTrue(TEXT("delta 1: layer 0 loses it"), A[0] == 84 && A[1] == 85 && A[2] == 85 && A[3] == 1);
	uint8 B[4] = {85, 85, 85, 0};
	FTerrainBrush::PaintTexel(B, 3, 2);
	TestTrue(TEXT("delta 2: layers 0 and 1 lose one each"), B[0] == 84 && B[1] == 84 && B[2] == 85 && B[3] == 2);
	uint8 C[4] = {0, 85, 85, 85};
	FTerrainBrush::PaintTexel(C, 0, 2);
	TestTrue(TEXT("painting layer 0: layers 1 and 2 lose one each"), C[0] == 2 && C[1] == 84 && C[2] == 84 && C[3] == 85);

	// Unequal remainders: 100*{100,60,95}/255 = 39.2 / 23.5 / 37.25 -> floors 39 / 23 / 37 (sum 99), one leftover to the largest remainder (layer 1).
	uint8 D[4] = {100, 60, 95, 0};
	TTEqI64(*this, TEXT("delta applied"), FTerrainBrush::PaintTexel(D, 3, 100), 100);
	TestTrue(TEXT("largest fractional remainder takes the leftover"), D[0] == 61 && D[1] == 36 && D[2] == 58 && D[3] == 100);
	TTEqI64(*this, TEXT("sum"), static_cast<int32>(D[0]) + D[1] + D[2] + D[3], 255);

	// Proportional take: a texel of only layer 0 painted to layer 2 loses everything from layer 0.
	uint8 E[4] = {255, 0, 0, 0};
	FTerrainBrush::PaintTexel(E, 2, 76);
	TestTrue(TEXT("{255,0,0,0} + 76 on layer 2"), E[0] == 179 && E[1] == 0 && E[2] == 76 && E[3] == 0);
	return true;
}

// ---- normals ----------------------------------------------------------------------------------------------------------

TT_TEST(BorderNormalsExactOnTiltedPlane)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	// h = 0.25 x + 0.5 y in metres: |h| <= 120 so nothing clamps; all values are exact in float.
	for (int32 Y = 0; Y < HF.Width(); ++Y)
	{
		for (int32 X = 0; X < HF.Width(); ++X)
		{
			HF.SetHeight(X, Y, 0.25f * HF.VertexToWorld(X) + 0.5f * HF.VertexToWorld(Y));
		}
	}
	const FVector3f WantN = FVector3f(-0.25f, -0.5f, 1.0f).GetSafeNormal();
	const FVector3f WantT = FVector3f(1.0f, 0.0f, 0.25f).GetSafeNormal();
	int32 Bad = 0;
	int32 NonFinite = 0;
	for (int32 Y = 0; Y < HF.Width(); ++Y)
	{
		for (int32 X = 0; X < HF.Width(); ++X)
		{
			const FVector3f N = HF.GetNormal(X, Y);
			const FVector3f T = HF.GetTangent(X, Y);
			if (!FMath::IsFinite(N.X) || !FMath::IsFinite(N.Y) || !FMath::IsFinite(N.Z) || !FMath::IsFinite(T.X) || !FMath::IsFinite(T.Z))
			{
				++NonFinite;
			}
			if (!N.Equals(WantN, 1e-6f) || !T.Equals(WantT, 1e-6f))
			{
				++Bad;
			}
		}
	}
	TTEqI64(*this, TEXT("non-finite normals or tangents"), NonFinite, 0);
	TTEqI64(*this, TEXT("vertices (incl. all four corners and every border) off the exact plane normal"), Bad, 0);
	TestTrue(TEXT("corner (0,0) is exact"), HF.GetNormal(0, 0).Equals(WantN, 1e-6f));
	TestTrue(TEXT("corner (320,320) is exact"), HF.GetNormal(320, 320).Equals(WantN, 1e-6f));
	return true;
}

// ---- undo and redo (plan C 3.7) ----------------------------------------------------------------------------------------

TT_TEST(UndoRedoExactHashes)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	FTerrainUndo Undo;
	const FTTHashes H0 = TTHashes(HF);

	// Stroke 1 raises across the corner shared by four chunks (vertex (64,64) = world (-96,-96)).
	TestTrue(TEXT("stroke 1 pushed"), TTStroke(HF, Undo, TTParams(ETerrainBrushMode::Raise, 30.0f, 40.0f), -96.0f, -96.0f, 10));
	const FTTHashes H1 = TTHashes(HF);
	// Stroke 2 paints snow at the same corner (splat tiles of four chunks).
	TestTrue(TEXT("stroke 2 pushed"), TTStroke(HF, Undo, TTParams(ETerrainBrushMode::Paint, 30.0f, 40.0f, 3), -96.0f, -96.0f, 10));
	const FTTHashes H2 = TTHashes(HF);
	// Stroke 3 smooths the flank of the new hill (smoothing flat ground would change nothing).
	TestTrue(TEXT("stroke 3 pushed"), TTStroke(HF, Undo, TTParams(ETerrainBrushMode::Smooth, 20.0f, 60.0f), -96.0f, -96.0f, 5));
	const FTTHashes H3 = TTHashes(HF);
	TestTrue(TEXT("hashes differ per stroke"), H0.Height != H1.Height && H1.Splat != H2.Splat && H2.Height != H3.Height);
	TTEqI64(*this, TEXT("undo entries"), Undo.NumUndo(), 3);

	FTerrainEditDelta Delta;
	TestTrue(TEXT("undo 3"), Undo.Undo(HF, Delta));
	TTExpectHashes(*this, TEXT("after undo of stroke 3"), HF, H2);
	TestTrue(TEXT("undo 2"), Undo.Undo(HF, Delta));
	TTExpectHashes(*this, TEXT("after undo of stroke 2"), HF, H1);
	TTEqI64(*this, TEXT("stroke 2 touched four chunks"), Delta.Chunks.Num(), 4);
	TestTrue(TEXT("their ids are {0,1,5,6}"), Delta.Chunks.Num() == 4 && Delta.Chunks[0] == 0 && Delta.Chunks[1] == 1 && Delta.Chunks[2] == 5 && Delta.Chunks[3] == 6);
	TestTrue(TEXT("delta carries a splat rect and no stale height rect needed"), !Delta.SplatRect.IsEmpty());
	TestTrue(TEXT("undo 1"), Undo.Undo(HF, Delta));
	TTExpectHashes(*this, TEXT("after undo of stroke 1"), HF, H0);
	TestFalse(TEXT("nothing left to undo"), Undo.Undo(HF, Delta));
	TestFalse(TEXT("an empty undo reports no chunks"), Delta.Any());

	TestTrue(TEXT("redo 1"), Undo.Redo(HF, Delta));
	TTExpectHashes(*this, TEXT("after redo of stroke 1"), HF, H1);
	TestTrue(TEXT("redo 2"), Undo.Redo(HF, Delta));
	TTExpectHashes(*this, TEXT("after redo of stroke 2"), HF, H2);
	TestTrue(TEXT("redo 3"), Undo.Redo(HF, Delta));
	TTExpectHashes(*this, TEXT("after redo of stroke 3"), HF, H3);
	TestFalse(TEXT("nothing left to redo"), Undo.Redo(HF, Delta));

	// A new stroke after an undo clears the redo stack.
	Undo.Undo(HF, Delta);
	TestTrue(TEXT("can redo after undo"), Undo.CanRedo());
	TTStroke(HF, Undo, TTParams(ETerrainBrushMode::Lower, 20.0f, 30.0f), -20.0f, 20.0f, 3);
	TestFalse(TEXT("a new stroke clears redo"), Undo.CanRedo());
	// Undo is refused while a stroke is open.
	Undo.BeginStroke();
	TestFalse(TEXT("undo refused mid-stroke"), Undo.Undo(HF, Delta));
	Undo.EndStroke(HF);
	return true;
}

TT_TEST(NoChangeStrokePushesNothing)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	FTerrainUndo Undo;
	const FTTHashes H0 = TTHashes(HF);
	// Painting layer 0 on an all-grass splat changes no byte.
	TestFalse(TEXT("paint grass on grass pushes nothing"), TTStroke(HF, Undo, TTParams(ETerrainBrushMode::Paint, 40.0f, 80.0f, 0), 0.0f, 0.0f, 5));
	TTEqI64(*this, TEXT("undo entries"), Undo.NumUndo(), 0);
	TTExpectHashes(*this, TEXT("data after the no-op stroke"), HF, H0);

	// Raising where every vertex is already at the +128 clamp changes no byte either.
	for (int32 Y = 100; Y < 220; ++Y)
	{
		for (int32 X = 100; X < 220; ++X)
		{
			HF.SetHeight(X, Y, 128.0f);
		}
	}
	TestFalse(TEXT("raise on the clamp pushes nothing"), TTStroke(HF, Undo, TTParams(ETerrainBrushMode::Raise, 40.0f, 80.0f), 0.0f, 0.0f, 5));
	TTEqI64(*this, TEXT("undo entries still 0"), Undo.NumUndo(), 0);
	TestFalse(TEXT("cannot undo"), Undo.CanUndo());

	// A stroke that does change something does push, and only the changed chunks are kept.
	TestTrue(TEXT("a real stroke pushes"), TTStroke(HF, Undo, TTParams(ETerrainBrushMode::Paint, 40.0f, 80.0f, 2), 0.0f, 0.0f, 2));
	TTEqI64(*this, TEXT("one entry"), Undo.NumUndo(), 1);
	return true;
}

TT_TEST(UndoCapEvictsOldest)
{
	const FTerrainBrushParams P = TTParams(ETerrainBrushMode::Raise, 20.0f, 50.0f);
	const float Xs[5] = {-120.0f, -60.0f, 0.0f, 60.0f, 120.0f}; // one chunk column each (vertex x 40, 100, 160, 220, 280)

	// Entry-count cap.
	{
		FTerrainHeightfield HF;
		HF.Init(160, 64);
		FTerrainUndo Undo(int64(1) << 40, 3);
		FTTHashes After[5];
		for (int32 I = 0; I < 5; ++I)
		{
			TestTrue(TEXT("stroke pushes"), TTStroke(HF, Undo, P, Xs[I], 0.0f, 1));
			After[I] = TTHashes(HF);
		}
		TTEqI64(*this, TEXT("entries kept"), Undo.NumUndo(), 3);
		TTEqI64(*this, TEXT("entries evicted"), Undo.NumEvicted(), 2);
		FTerrainEditDelta Delta;
		TestTrue(TEXT("undo 1"), Undo.Undo(HF, Delta));
		TestTrue(TEXT("undo 2"), Undo.Undo(HF, Delta));
		TestTrue(TEXT("undo 3"), Undo.Undo(HF, Delta));
		TestFalse(TEXT("the two oldest were dropped: a 4th undo fails"), Undo.Undo(HF, Delta));
		TTExpectHashes(*this, TEXT("state after the 3 retained undos = state after stroke 2"), HF, After[1]);
	}

	// Byte cap: three times the first entry's size holds the last two (the last chunk column is 65 vertices wide, so entries differ slightly).
	{
		FTerrainHeightfield HF;
		HF.Init(160, 64);
		FTerrainUndo Probe;
		TTStroke(HF, Probe, P, Xs[0], 0.0f, 1);
		const int64 One = Probe.TotalBytes();
		TestTrue(TEXT("an entry holds real bytes"), One > 64 * 64 * 4);

		FTerrainHeightfield HF2;
		HF2.Init(160, 64);
		FTerrainUndo Capped(3 * One, 1000);
		for (int32 I = 0; I < 5; ++I)
		{
			TTStroke(HF2, Capped, P, Xs[I], 0.0f, 1);
		}
		TTEqI64(*this, TEXT("entries kept under a byte cap"), Capped.NumUndo(), 2);
		TestTrue(TEXT("bytes within the cap"), Capped.TotalBytes() <= 3 * One);
	}
	return true;
}

// ---- pick (plan C 3.6) ----------------------------------------------------------------------------------------------------

TT_TEST(PickOnSlopeWithin1mm)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	// h = 0.25 x + 0.125 y: planar, so every triangle of every cell lies on the plane and the analytic hit is exact.
	for (int32 Y = 0; Y < HF.Width(); ++Y)
	{
		for (int32 X = 0; X < HF.Width(); ++X)
		{
			HF.SetHeight(X, Y, 0.25f * HF.VertexToWorld(X) + 0.125f * HF.VertexToWorld(Y));
		}
	}
	FRandomStream Rand(7);
	int32 Asserted = 0;
	for (int32 I = 0; I < 40; ++I)
	{
		const FVector O(Rand.FRandRange(-40.0f, 40.0f), Rand.FRandRange(-40.0f, 40.0f), Rand.FRandRange(100.0f, 200.0f));
		const FVector D = FVector(Rand.FRandRange(-0.4f, 0.4f), Rand.FRandRange(-0.4f, 0.4f), -1.0).GetSafeNormal();
		// Solve O.z + D.z t = 0.25 (O.x + D.x t) + 0.125 (O.y + D.y t).
		const double T = (0.25 * O.X + 0.125 * O.Y - O.Z) / (D.Z - 0.25 * D.X - 0.125 * D.Y);
		const FVector Want = O + D * T;
		if (T <= 0.0 || FMath::Abs(Want.X) > 159.0 || FMath::Abs(Want.Y) > 159.0)
		{
			continue;
		}
		FTerrainHit Hit;
		const bool bHit = TerrainPick::RayCast(HF, O, D, 1000.0, Hit);
		if (!bHit)
		{
			AddError(FString::Printf(TEXT("ray %d: expected a hit at (%.3f, %.3f, %.3f)"), I, Want.X, Want.Y, Want.Z));
			continue;
		}
		++Asserted;
		TTNear(*this, FString::Printf(TEXT("ray %d x"), I), Hit.Position.X, Want.X, 1e-3);
		TTNear(*this, FString::Printf(TEXT("ray %d y"), I), Hit.Position.Y, Want.Y, 1e-3);
		TTNear(*this, FString::Printf(TEXT("ray %d z"), I), Hit.Position.Z, Want.Z, 1e-3);
		TTNear(*this, FString::Printf(TEXT("ray %d distance"), I), Hit.Distance, T, 1e-3);
	}
	TestTrue(*FString::Printf(TEXT("at least 25 rays were checked (%d)"), Asserted), Asserted >= 25);

	// A ray along the negative x direction exercises the descending DDA step.
	FTerrainHit Hit;
	const FVector O(100.0, 20.0, 40.0);
	const FVector D = FVector(-1.0, 0.0, -0.5).GetSafeNormal();
	const double T = (0.25 * O.X + 0.125 * O.Y - O.Z) / (D.Z - 0.25 * D.X - 0.125 * D.Y);
	const FVector Want = O + D * T;
	TestTrue(TEXT("negative-x ray hits"), TerrainPick::RayCast(HF, O, D, 1000.0, Hit));
	TTNear(*this, TEXT("negative-x x"), Hit.Position.X, Want.X, 1e-3);
	TTNear(*this, TEXT("negative-x z"), Hit.Position.Z, Want.Z, 1e-3);
	return true;
}

TT_TEST(PickMissesAboveTheMap)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	FTerrainHit Hit;
	TestFalse(TEXT("horizontal ray far above the map"), TerrainPick::RayCast(HF, FVector(-200.0, 0.0, 200.0), FVector(1.0, 0.0, 0.0), 1000.0, Hit));
	TestFalse(TEXT("ray pointing up from above"), TerrainPick::RayCast(HF, FVector(0.0, 0.0, 100.0), FVector(0.0, 0.0, 1.0), 1000.0, Hit));
	TestFalse(TEXT("ray pointing straight down outside the footprint"), TerrainPick::RayCast(HF, FVector(500.0, 0.0, 300.0), FVector(0.0, 0.0, -1.0), 1000.0, Hit));
	TestFalse(TEXT("shallow ray that crosses the whole map without reaching the ground"), TerrainPick::RayCast(HF, FVector(-170.0, 0.0, 100.0), FVector(1.0, 0.0, -0.001), 1000.0, Hit));
	TestFalse(TEXT("ray too short to reach the ground"), TerrainPick::RayCast(HF, FVector(0.0, 0.0, 50.0), FVector(0.0, 0.0, -1.0), 10.0, Hit));
	TestFalse(TEXT("zero direction"), TerrainPick::RayCast(HF, FVector(0.0, 0.0, 50.0), FVector::ZeroVector, 10.0, Hit));
	TestFalse(TEXT("a miss clears bHit"), Hit.bHit);

	TestTrue(TEXT("straight down from 50 m hits the flat ground"), TerrainPick::RayCast(HF, FVector(12.3, -45.6, 50.0), FVector(0.0, 0.0, -1.0), 100.0, Hit));
	TTNear(*this, TEXT("hit z"), Hit.Position.Z, 0.0, 1e-9);
	TTNear(*this, TEXT("hit distance"), Hit.Distance, 50.0, 1e-9);
	TTNear(*this, TEXT("hit x"), Hit.Position.X, 12.3, 1e-9);
	// Entering from the side, below the slab top, over the map edge.
	TestTrue(TEXT("a descending ray entering over the map edge hits"), TerrainPick::RayCast(HF, FVector(-200.0, 0.0, 40.0), FVector(1.0, 0.0, -0.25), 1000.0, Hit));
	TTNear(*this, TEXT("edge ray hit z"), Hit.Position.Z, 0.0, 1e-9);
	TTNear(*this, TEXT("edge ray hit x"), Hit.Position.X, -40.0, 1e-9);
	return true;
}

TT_TEST(BothDiagonalsAgreeWithTheTriangulation)
{
	// Cell (10,10): BL=(10,10) BR=(11,10) TL=(10,11) TR=(11,11); the mesh splits it BL-TR.
	const auto Check = [this](float BL, float BR, float TL, float TR, const TCHAR* Name)
	{
		FTerrainHeightfield HF;
		HF.Init(160, 64);
		HF.SetHeight(10, 10, BL);
		HF.SetHeight(11, 10, BR);
		HF.SetHeight(10, 11, TL);
		HF.SetHeight(11, 11, TR);
		const double Ox = 10.0 - 160.0;
		const double Oy = 10.0 - 160.0;
		struct FCase
		{
			double Fu;
			double Fv;
		};
		const FCase Cases[3] = {{0.5, 0.5}, {0.75, 0.25}, {0.25, 0.75}};
		for (const FCase& C : Cases)
		{
			// Expected from the triangulation: on or above the BL-TR diagonal use (BL,TL,TR), below use (BL,TR,BR).
			const double Want = (C.Fv >= C.Fu) ? BL + C.Fu * (TR - TL) + C.Fv * (TL - BL) : BL + C.Fu * (BR - BL) + C.Fv * (TR - BR);
			const FVector O(Ox + C.Fu, Oy + C.Fv, 50.0);
			FTerrainHit Hit;
			const bool bHit = TerrainPick::RayCast(HF, O, FVector(0.0, 0.0, -1.0), 100.0, Hit);
			TestTrue(*FString::Printf(TEXT("%s (%.2f,%.2f) pick hits"), Name, C.Fu, C.Fv), bHit);
			TTNear(*this, FString::Printf(TEXT("%s (%.2f,%.2f) pick z"), Name, C.Fu, C.Fv), Hit.Position.Z, Want, 1e-9);
			TTNear(*this, FString::Printf(TEXT("%s (%.2f,%.2f) SampleSurface"), Name, C.Fu, C.Fv), HF.SampleSurface(O.X, O.Y), Want, 1e-6);
		}
		// The centre sits ON the BL-TR diagonal: it is the mean of BL and TR, not of BR and TL.
		TTNear(*this, FString::Printf(TEXT("%s centre = mean(BL,TR)"), Name), HF.SampleSurface(Ox + 0.5, Oy + 0.5), 0.5 * (BL + TR), 1e-6);
	};
	Check(1.0f, 0.0f, 0.0f, 1.0f, TEXT("ridge on the BL-TR diagonal")); // centre 1.0 (the other diagonal would give 0.0)
	Check(0.0f, 1.0f, 1.0f, 0.0f, TEXT("valley on the BL-TR diagonal")); // centre 0.0 (the other diagonal would give 1.0)
	Check(0.0f, 2.0f, 3.0f, 1.0f, TEXT("skew cell"));
	return true;
}

// ---- sim export (plan C 3.2) ------------------------------------------------------------------------------------------------

TT_TEST(FixedTruncationTowardZero)
{
	using namespace TerrainSimExport;
	struct FCase
	{
		float H;
		int32 Raw;
	};
	// Truncation toward zero, as the C# cast: floor would give -1, -65537 and -19661 for the three negative non-exact values.
	const FCase Cases[] = {
		{0.99999f, 65535}, {-0.5f, -32768}, {1.0f, 65536}, {-0.00001f, 0}, {-1.00001f, -65536}, {-0.3f, -19660},
		{0.3f, 19660}, {0.0f, 0}, {-1.0f, -65536}, {-128.0f, -8388608}, {128.0f, 8388608}, {127.99999f, 8388607}, {-127.99999f, -8388607},
	};
	for (const FCase& C : Cases)
	{
		TTEqI64(*this, FString::Printf(TEXT("FixedRawFromFloat(%.8g)"), C.H), FixedRawFromFloat(C.H), C.Raw);
	}
	return true;
}

TT_TEST(CellToVertexMapping)
{
	using namespace TerrainSimExport;
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	TTEqI64(*this, TEXT("B = E - 128"), VertexOffset(HF), 32);
	// CellToTexel is the identity for 256 cells over a 256-texel region.
	bool bIdentity = true;
	for (int32 C = 0; C < 256; ++C)
	{
		bIdentity &= CellToTexel(C, 256, 256) == C;
	}
	TestTrue(TEXT("CellToTexel(c,256,256) == c"), bIdentity);
	// Same expected values as HeightmapCellMapping's integer formula: ((2c+1)*region) / (2*count).
	TTEqI64(*this, TEXT("CellToTexel(0,128,256)"), CellToTexel(0, 128, 256), 1);
	TTEqI64(*this, TEXT("CellToTexel(5,128,256)"), CellToTexel(5, 128, 256), 11);
	TTEqI64(*this, TEXT("CellToTexel(127,128,256)"), CellToTexel(127, 128, 256), 255);
	TTEqI64(*this, TEXT("CellToTexel(-3,256,256) clamps to 0"), CellToTexel(-3, 256, 256), 0);
	TTEqI64(*this, TEXT("CellToTexel(999,256,256) clamps to 255"), CellToTexel(999, 256, 256), 255);
	TTEqI64(*this, TEXT("degenerate count"), CellToTexel(3, 0, 256), 0);
	TTEqI64(*this, TEXT("degenerate region"), CellToTexel(3, 256, 0), 0);
	// Cell -> vertex: cell 0 is world -128 = vertex 32, cell 128 is the origin = vertex 160, cell 255 is vertex 287.
	TTEqI64(*this, TEXT("cell 0 -> vertex 32"), CellToVertex(HF, 0), 32);
	TTEqI64(*this, TEXT("cell 128 -> vertex 160"), CellToVertex(HF, 128), 160);
	TTEqI64(*this, TEXT("cell 255 -> vertex 287"), CellToVertex(HF, 255), 287);
	TTNear(*this, TEXT("vertex 32 is world -128"), HF.VertexToWorld(CellToVertex(HF, 0)), -128.0, 0.0);
	TTNear(*this, TEXT("vertex 160 is world 0"), HF.VertexToWorld(CellToVertex(HF, 128)), 0.0, 0.0);

	// The headroom run (E = 320): B = 192.
	FTerrainHeightfield Big;
	Big.Init(320, 64);
	TTEqI64(*this, TEXT("B for E=320"), VertexOffset(Big), 192);
	TTEqI64(*this, TEXT("cell 0 -> vertex 192"), CellToVertex(Big, 0), 192);

	// BuildSimGrid indexes [row * 256 + col] = height at vertex (col+32, row+32) and ignores the border ring.
	for (int32 R = 0; R < 256; ++R)
	{
		for (int32 C = 0; C < 256; ++C)
		{
			HF.SetHeight(C + 32, R + 32, (static_cast<float>((C * 7 + R * 13) % 200) - 100.0f) * 0.0173f);
		}
	}
	HF.SetHeight(5, 5, 77.0f);
	TArray<int32> Raw;
	BuildSimGrid(HF, Raw);
	int32 Bad = 0;
	for (int32 R = 0; R < 256; ++R)
	{
		for (int32 C = 0; C < 256; ++C)
		{
			if (Raw[R * 256 + C] != FixedRawFromFloat(HF.GetHeight(C + 32, R + 32)))
			{
				++Bad;
			}
		}
	}
	TTEqI64(*this, TEXT("sim cells that differ from (int)(h*65536)"), Bad, 0);
	const uint32 Fnv = SimGridFnv(Raw);
	HF.SetHeight(6, 6, -50.0f); // outside the 256 window: the sim grid does not see it
	TArray<int32> Raw2;
	BuildSimGrid(HF, Raw2);
	TTEqU32(*this, TEXT("border ring does not move sim_grid_fnv"), SimGridFnv(Raw2), Fnv);
	HF.SetHeight(100, 100, HF.GetHeight(100, 100) + 1.0f);
	BuildSimGrid(HF, Raw2);
	TestTrue(TEXT("a window vertex does move sim_grid_fnv"), SimGridFnv(Raw2) != Fnv);
	return true;
}

TT_TEST(SimGridSampleMatchesElevationGrid)
{
	using namespace TerrainSimExport;
	TArray<int32> Raw;
	Raw.SetNumUninitialized(256 * 256);
	for (int32 I = 0; I < Raw.Num(); ++I)
	{
		Raw[I] = I * 3 - 1000;
	}
	constexpr int32 One = 65536;
	// ElevationGrid.Sample: col = ((x - minX) / cell).ToInt() = floor, clamped. WorldMin = -128.
	TTEqI64(*this, TEXT("x = -128 -> col 0"), SampleSimGridRaw(Raw, -128 * One, -128 * One), Raw[0]);
	TTEqI64(*this, TEXT("x = -122 -> col 6, z = -121 -> row 7"), SampleSimGridRaw(Raw, -122 * One, -121 * One), Raw[7 * 256 + 6]);
	TTEqI64(*this, TEXT("one LSB below the boundary stays in col 5"), SampleSimGridRaw(Raw, -122 * One - 1, -128 * One), Raw[5]);
	TTEqI64(*this, TEXT("x = 0, z = 0 -> col 128, row 128"), SampleSimGridRaw(Raw, 0, 0), Raw[128 * 256 + 128]);
	TTEqI64(*this, TEXT("x = -1 LSB lands in col 127"), SampleSimGridRaw(Raw, -1, 0), Raw[128 * 256 + 127]);
	TTEqI64(*this, TEXT("beyond +edge clamps to col 255"), SampleSimGridRaw(Raw, 200 * One, 200 * One), Raw[255 * 256 + 255]);
	TTEqI64(*this, TEXT("beyond -edge clamps to col 0"), SampleSimGridRaw(Raw, -200 * One, -129 * One), Raw[0]);
	TArray<FIntPoint> Probes;
	GetProbePoints(Probes);
	TTEqI64(*this, TEXT("probe count"), Probes.Num(), 16);
	return true;
}

TT_TEST(SimToUeAxes)
{
	using namespace TerrainSimExport;
	const FVector P = SimToUe(1.0, 2.0, 3.0);
	TestTrue(TEXT("SimToUe(1,2,3) = (100,200,300) cm"), P.X == 100.0 && P.Y == 200.0 && P.Z == 300.0);
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	// Terrain space is Unreal cm / 100: vertex (170,155) is world (10,-5) m -> UE (1000,-500) cm.
	const FVector Q = SimToUe(HF.VertexToWorld(170), HF.VertexToWorld(155), 1.25);
	TestTrue(TEXT("vertex (170,155) maps to UE (1000,-500,125)"), Q.X == 1000.0 && Q.Y == -500.0 && Q.Z == 125.0);
	TestEqual(TEXT("axes string"), FString(AxesString()), FString(TEXT("sim(x,z,h)->ue_cm(100x,100z,100h)")));
	return true;
}

// ---- IO ----------------------------------------------------------------------------------------------------------------------

TT_TEST(IoRoundTripByteIdentical)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	TTRunTickList(HF, nullptr);
	const FString DirA = TTTempDir(TEXT("a"));
	FString Error;
	const bool bSavedA = TerrainIO::Save(HF, DirA, Error);
	TestTrue(*FString::Printf(TEXT("save A: %s"), *Error), bSavedA);

	FTerrainHeightfield Loaded;
	const bool bLoadedA = TerrainIO::Load(Loaded, 64, DirA, Error);
	TestTrue(*FString::Printf(TEXT("load A: %s"), *Error), bLoadedA);
	TestTrue(TEXT("height arrays equal"), Loaded.Heights == HF.Heights);
	TestTrue(TEXT("splat arrays equal"), Loaded.Splat == HF.Splat);
	TTEqU32(*this, TEXT("height_fnv"), Loaded.HeightFnv(), HF.HeightFnv());
	TTEqU32(*this, TEXT("splat_fnv"), Loaded.SplatFnv(), HF.SplatFnv());

	const FString DirB = TTTempDir(TEXT("b"));
	const bool bSavedB = TerrainIO::Save(Loaded, DirB, Error);
	TestTrue(*FString::Printf(TEXT("save B: %s"), *Error), bSavedB);
	const TCHAR* Files[3] = {TerrainIO::JsonFileName, TerrainIO::HeightFileName, TerrainIO::SplatFileName};
	for (const TCHAR* File : Files)
	{
		TArray<uint8> A;
		TArray<uint8> B;
		const bool bA = FFileHelper::LoadFileToArray(A, *FPaths::Combine(DirA, File));
		const bool bB = FFileHelper::LoadFileToArray(B, *FPaths::Combine(DirB, File));
		TestTrue(*FString::Printf(TEXT("%s readable"), File), bA && bB);
		TestTrue(*FString::Printf(TEXT("%s byte-identical after save-load-save (%d bytes)"), File, A.Num()), A == B);
	}

	// Sizes: 321^2 * 4 bytes of float32, 640^2 * 4 bytes of RGBA8.
	TArray<uint8> HeightBytes;
	FFileHelper::LoadFileToArray(HeightBytes, *FPaths::Combine(DirA, TerrainIO::HeightFileName));
	TTEqI64(*this, TEXT("height.r32 size"), HeightBytes.Num(), 321 * 321 * 4);
	TArray<uint8> SplatBytes;
	FFileHelper::LoadFileToArray(SplatBytes, *FPaths::Combine(DirA, TerrainIO::SplatFileName));
	TTEqI64(*this, TEXT("splat.rgba8 size"), SplatBytes.Num(), 640 * 640 * 4);
	// height.r32 is little-endian float32: the first value read back by hand equals Heights[0].
	const uint32 Bits = HeightBytes[0] | (HeightBytes[1] << 8) | (HeightBytes[2] << 16) | (static_cast<uint32>(HeightBytes[3]) << 24);
	float First;
	FMemory::Memcpy(&First, &Bits, sizeof(First));
	TestTrue(TEXT("first float32 LE"), First == HF.Heights[0]);

	// terrain.json carries dims, spacing, axes, fnvs, sim_grid_fnv and 16 probes.
	FString JsonText;
	FFileHelper::LoadFileToString(JsonText, *FPaths::Combine(DirA, TerrainIO::JsonFileName));
	TSharedPtr<FJsonObject> Obj;
	TestTrue(TEXT("terrain.json parses"), FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(JsonText), Obj) && Obj.IsValid());
	if (Obj.IsValid())
	{
		int32 Width = 0;
		int32 Half = 0;
		Obj->TryGetNumberField(TEXT("width"), Width);
		Obj->TryGetNumberField(TEXT("half_extent_m"), Half);
		TTEqI64(*this, TEXT("json width"), Width, 321);
		TTEqI64(*this, TEXT("json half_extent_m"), Half, 160);
		FString Axes;
		Obj->TryGetStringField(TEXT("axes"), Axes);
		TestEqual(TEXT("json axes"), Axes, FString(TerrainSimExport::AxesString()));
		FString SimFnv;
		Obj->TryGetStringField(TEXT("sim_grid_fnv"), SimFnv);
		TArray<int32> SimRaw;
		TerrainSimExport::BuildSimGrid(HF, SimRaw);
		TestEqual(TEXT("json sim_grid_fnv"), SimFnv, TerrainIO::HashToString(TerrainSimExport::SimGridFnv(SimRaw)));
		const TArray<TSharedPtr<FJsonValue>>* Probes = nullptr;
		TestTrue(TEXT("json probes array"), Obj->TryGetArrayField(TEXT("probes"), Probes) && Probes && Probes->Num() == 16);
		const TArray<TSharedPtr<FJsonValue>>* Layers = nullptr;
		TestTrue(TEXT("json layers array"), Obj->TryGetArrayField(TEXT("layers"), Layers) && Layers && Layers->Num() == 4);
	}
	IFileManager::Get().DeleteDirectory(*FPaths::GetPath(DirA), false, true);
	IFileManager::Get().DeleteDirectory(*FPaths::GetPath(DirB), false, true);
	return true;
}

TT_TEST(IoRejectsDamagedFiles)
{
	FTerrainHeightfield HF;
	HF.Init(160, 64);
	TTRunTickList(HF, nullptr);
	const FString Dir = TTTempDir(TEXT("t"));
	FString Error;
	TestTrue(TEXT("save"), TerrainIO::Save(HF, Dir, Error));

	FTerrainHeightfield Out;
	// Flip one byte of a height: the stored height_fnv no longer matches.
	{
		TArray<uint8> Bytes;
		FFileHelper::LoadFileToArray(Bytes, *FPaths::Combine(Dir, TerrainIO::HeightFileName));
		Bytes[1000] ^= 0x01;
		FFileHelper::SaveArrayToFile(Bytes, *FPaths::Combine(Dir, TerrainIO::HeightFileName));
		TestFalse(TEXT("flipped height byte rejected"), TerrainIO::Load(Out, 64, Dir, Error));
		TestTrue(*FString::Printf(TEXT("error names the hash (%s)"), *Error), Error.Contains(TEXT("height")));
		Bytes[1000] ^= 0x01;
		FFileHelper::SaveArrayToFile(Bytes, *FPaths::Combine(Dir, TerrainIO::HeightFileName));
		TestTrue(TEXT("restored file loads again"), TerrainIO::Load(Out, 64, Dir, Error));
	}
	// Truncated splat.
	{
		TArray<uint8> Bytes;
		FFileHelper::LoadFileToArray(Bytes, *FPaths::Combine(Dir, TerrainIO::SplatFileName));
		Bytes.SetNum(Bytes.Num() - 4);
		FFileHelper::SaveArrayToFile(Bytes, *FPaths::Combine(Dir, TerrainIO::SplatFileName));
		TestFalse(TEXT("truncated splat rejected"), TerrainIO::Load(Out, 64, Dir, Error));
		TestTrue(*FString::Printf(TEXT("error names the size (%s)"), *Error), Error.Contains(TEXT("bytes")));
	}
	// A rejected file leaves the caller's heightfield untouched (the truncated splat above was rejected after a good load into Out).
	{
		FTerrainHeightfield Keep;
		Keep.Init(160, 64);
		TTRunTickList(Keep, nullptr);
		Keep.SetHeight(3, 4, 7.25f);
		const FTTHashes Before = TTHashes(Keep);
		TestFalse(TEXT("truncated splat rejected again"), TerrainIO::Load(Keep, 32, Dir, Error));
		TTExpectHashes(*this, TEXT("rejected load leaves the target unchanged"), Keep, Before);
		TestEqual(TEXT("rejected load keeps the target's width"), Keep.Width(), 321);
	}
	// A terrain.json whose axes field differs from TerrainSimExport::AxesString() is rejected.
	{
		const FString Dir2 = TTTempDir(TEXT("axes"));
		TestTrue(TEXT("save for axes check"), TerrainIO::Save(HF, Dir2, Error));
		const FString JsonPath = FPaths::Combine(Dir2, TerrainIO::JsonFileName);
		FString Json;
		FFileHelper::LoadFileToString(Json, *JsonPath);
		const FString Axes = TerrainSimExport::AxesString();
		TestTrue(TEXT("terrain.json records the axes string"), Json.Contains(Axes));
		FTerrainHeightfield Out2;
		TestTrue(TEXT("unmodified axes load"), TerrainIO::Load(Out2, 64, Dir2, Error));
		Json.ReplaceInline(*Axes, TEXT("sim(x,z,h)->ue_cm(100z,100x,100h)"));
		FFileHelper::SaveStringToFile(Json, *JsonPath, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
		TestFalse(TEXT("changed axes rejected"), TerrainIO::Load(Out2, 64, Dir2, Error));
		TestTrue(*FString::Printf(TEXT("error names the axes (%s)"), *Error), Error.Contains(TEXT("axes")));
		IFileManager::Get().DeleteDirectory(*FPaths::GetPath(Dir2), false, true);
	}
	// Missing directory.
	TestFalse(TEXT("missing directory rejected"), TerrainIO::Load(Out, 64, Dir + TEXT("_nope"), Error));
	IFileManager::Get().DeleteDirectory(*FPaths::GetPath(Dir), false, true);
	return true;
}

// ---- determinism, partition, edges -----------------------------------------------------------------------------------------

TT_TEST(SameTickListTwiceSameHash)
{
	FTerrainHeightfield A;
	A.Init(160, 64);
	FTerrainHeightfield B;
	B.Init(160, 64);
	FTerrainHeightfield C;
	C.Init(160, 32);
	FTerrainUndo Undo;
	TTRunTickList(A, nullptr);
	TTRunTickList(B, nullptr);
	TTRunTickList(C, &Undo); // recording undo and a different chunk size must not change the result
	TTEqU32(*this, TEXT("height_fnv, run 2 vs run 1"), B.HeightFnv(), A.HeightFnv());
	TTEqU32(*this, TEXT("splat_fnv, run 2 vs run 1"), B.SplatFnv(), A.SplatFnv());
	TTEqU32(*this, TEXT("height_fnv, undo-recording run"), C.HeightFnv(), A.HeightFnv());
	TTEqU32(*this, TEXT("splat_fnv, undo-recording run"), C.SplatFnv(), A.SplatFnv());
	FTerrainHeightfield Flat;
	Flat.Init(160, 64);
	TestTrue(TEXT("the tick list changed the terrain"), A.HeightFnv() != Flat.HeightFnv() && A.SplatFnv() != Flat.SplatFnv());

	// Undoing every recorded stroke returns to flat, bit for bit.
	FTerrainEditDelta Delta;
	while (Undo.Undo(C, Delta))
	{
	}
	TTEqU32(*this, TEXT("height_fnv after undoing everything"), C.HeightFnv(), Flat.HeightFnv());
	TTEqU32(*this, TEXT("splat_fnv after undoing everything"), C.SplatFnv(), Flat.SplatFnv());
	return true;
}

TT_TEST(ChunkPartitionTilesTheGrid)
{
	struct FConfig
	{
		int32 Half;
		int32 Chunk;
		int32 ExpectChunks;
	};
	const FConfig Configs[] = {{160, 64, 25}, {160, 32, 100}, {100, 64, 16}, {320, 64, 100}};
	for (const FConfig& Cfg : Configs)
	{
		FTerrainHeightfield HF;
		HF.Init(Cfg.Half, Cfg.Chunk);
		const FString Tag = FString::Printf(TEXT("E=%d C=%d"), Cfg.Half, Cfg.Chunk);
		TTEqI64(*this, Tag + TEXT(" chunk count"), HF.NumChunks(), Cfg.ExpectChunks);

		// Owned vertex blocks tile the grid exactly once, and OwnerChunkOfVertex agrees.
		TArray<uint8> Count;
		Count.Init(0, HF.Width() * HF.Width());
		int32 OwnerMismatch = 0;
		for (int32 Id = 0; Id < HF.NumChunks(); ++Id)
		{
			const FTerrainRect R = HF.ChunkOwnedRect(Id);
			for (int32 Y = R.Y0; Y < R.Y1; ++Y)
			{
				for (int32 X = R.X0; X < R.X1; ++X)
				{
					++Count[Y * HF.Width() + X];
					OwnerMismatch += HF.OwnerChunkOfVertex(X, Y) != Id;
				}
			}
		}
		int32 NotOnce = 0;
		for (const uint8 N : Count)
		{
			NotOnce += N != 1;
		}
		TTEqI64(*this, Tag + TEXT(" vertices not owned exactly once"), NotOnce, 0);
		TTEqI64(*this, Tag + TEXT(" owner mismatches"), OwnerMismatch, 0);

		// Owned splat blocks tile the splat map exactly once.
		TArray<uint8> SplatCount;
		SplatCount.Init(0, HF.SplatSize() * HF.SplatSize());
		for (int32 Id = 0; Id < HF.NumChunks(); ++Id)
		{
			const FTerrainRect R = HF.ChunkOwnedSplatRect(Id);
			for (int32 Y = R.Y0; Y < R.Y1; ++Y)
			{
				for (int32 X = R.X0; X < R.X1; ++X)
				{
					++SplatCount[Y * HF.SplatSize() + X];
				}
			}
		}
		int32 SplatNotOnce = 0;
		for (const uint8 N : SplatCount)
		{
			SplatNotOnce += N != 1;
		}
		TTEqI64(*this, Tag + TEXT(" splat texels not owned exactly once"), SplatNotOnce, 0);

		// Render rects duplicate the border: a full chunk renders C+1 vertices per side.
		const FTerrainRect Render0 = HF.ChunkRenderRect(0);
		TTEqI64(*this, Tag + TEXT(" chunk 0 render width"), Render0.Width(), Cfg.Chunk + 1);
		if (HF.ChunksPerSide() > 1)
		{
			const FTerrainRect Render1 = HF.ChunkRenderRect(1);
			TTEqI64(*this, Tag + TEXT(" chunk 0 and 1 share exactly one vertex column"), Render0.X1 - Render1.X0, 1);
		}
	}

	FTerrainHeightfield HF;
	HF.Init(160, 64);
	TArray<int32> Ids;
	HF.ChunksRenderOverlappingRect(FTerrainRect(64, 10, 65, 11), Ids);
	TestTrue(TEXT("a vertex on the x=64 border is rendered by chunks 0 and 1"), Ids.Num() == 2 && Ids[0] == 0 && Ids[1] == 1);
	HF.ChunksOwningRect(FTerrainRect(64, 10, 65, 11), Ids);
	TestTrue(TEXT("but owned by chunk 1 only"), Ids.Num() == 1 && Ids[0] == 1);
	HF.ChunksRenderOverlappingRect(FTerrainRect(64, 64, 65, 65), Ids);
	TestTrue(TEXT("the corner vertex (64,64) is rendered by four chunks"), Ids.Num() == 4);
	HF.ChunksOwningRect(FTerrainRect(0, 0, 321, 321), Ids);
	TTEqI64(*this, TEXT("the whole grid is owned by 25 chunks"), Ids.Num(), 25);
	float ZMin = 0.0f;
	float ZMax = 0.0f;
	HF.SetHeight(64, 64, 9.0f);
	HF.GetChunkZRange(0, ZMin, ZMax);
	TestTrue(TEXT("chunk 0 sees the shared corner vertex in its z range"), ZMax == 9.0f && ZMin == 0.0f);
	return true;
}

TT_TEST(FootprintAndMapEdges)
{
	FTerrainBrushParams P = TTParams(ETerrainBrushMode::Raise, 1000.0f, 0.0f, 9);
	P.Clamp();
	TestTrue(TEXT("diameter clamps to 100"), P.DiameterM == 100.0f);
	TestTrue(TEXT("strength clamps to 1"), P.Strength == 1.0f);
	TestTrue(TEXT("layer clamps to 3"), P.PaintLayer == 3);
	P.DiameterM = 0.5f;
	P.Strength = 500.0f;
	P.PaintLayer = -2;
	P.Clamp();
	TestTrue(TEXT("diameter clamps to 5, strength to 100, layer to 0"), P.DiameterM == 5.0f && P.Strength == 100.0f && P.PaintLayer == 0);

	FTerrainHeightfield HF;
	HF.Init(160, 64);
	// A brush on the map corner changes only in-map vertices, nothing outside the circle, and does not crash.
	const FTerrainTickResult R = FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Raise, 100.0f, 50.0f), -160.0f, -160.0f);
	TestTrue(TEXT("corner brush changed vertices"), R.VerticesChanged > 1000);
	TestTrue(TEXT("rect stays inside the grid"), R.HeightRect.X0 >= 0 && R.HeightRect.Y0 >= 0 && R.HeightRect.X1 <= 321 && R.HeightRect.Y1 <= 321);
	TestTrue(TEXT("corner vertex is the peak"), HF.GetHeight(0, 0) == 0.5f);
	int32 Outside = 0;
	for (int32 Y = 0; Y < 321; ++Y)
	{
		for (int32 X = 0; X < 321; ++X)
		{
			if (FMath::Sqrt(static_cast<float>(X * X + Y * Y)) >= 50.0f && HF.GetHeight(X, Y) != 0.0f)
			{
				++Outside;
			}
		}
	}
	TTEqI64(*this, TEXT("vertices outside the radius that changed"), Outside, 0);

	// A brush entirely off the map does nothing; a paint brush on the edge keeps the sum invariant.
	const FTerrainTickResult Off = FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Raise, 20.0f, 50.0f), 500.0f, 500.0f);
	TestTrue(TEXT("off-map brush changes nothing"), !Off.Changed());
	FTerrainBrush::ApplyTick(HF, TTParams(ETerrainBrushMode::Paint, 100.0f, 100.0f, 1), 160.0f, 160.0f);
	int32 BadSum = 0;
	for (int32 T = 0; T < 640 * 640; ++T)
	{
		const uint8* Texel = &HF.Splat[T * 4];
		BadSum += static_cast<int32>(Texel[0]) + Texel[1] + Texel[2] + Texel[3] != 255;
	}
	TTEqI64(*this, TEXT("edge paint keeps every texel at 255"), BadSum, 0);
	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS

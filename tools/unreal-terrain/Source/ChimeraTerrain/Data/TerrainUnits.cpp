// Project Chimera terrain trial (plan C 4 C9). Original Chimera code. See TerrainUnits.h.

#include "Data/TerrainUnits.h"

#include <cmath>

namespace ChimeraTerrain
{
	namespace
	{
		/** 32-bit integer finaliser (murmur3 fmix32): the layout never uses an engine random, so it repeats on every machine. */
		uint32 MixU32(uint32 H)
		{
			H ^= H >> 16;
			H *= 0x85EBCA6Bu;
			H ^= H >> 13;
			H *= 0xC2B2AE35u;
			H ^= H >> 16;
			return H;
		}

		/** A value in [-1, 1) from a hash. */
		double SignedUnit(uint32 H)
		{
			return static_cast<double>(H >> 8) / 8388608.0 - 1.0;
		}

		void FnvFloat(uint64& H, float V)
		{
			uint32 Bits;
			FMemory::Memcpy(&Bits, &V, sizeof(Bits));
			for (int32 B = 0; B < 4; ++B)
			{
				H ^= static_cast<uint64>((Bits >> (8 * B)) & 0xFFu);
				H *= 1099511628211ull;
			}
		}
	}

	void BuildUnitLayout(int32 Count, int32 HalfExtentM, TArray<FUnitSlot>& Out)
	{
		Out.Reset();
		if (Count <= 0)
		{
			return;
		}
		const int32 Cols = FMath::CeilToInt(FMath::Sqrt(static_cast<double>(Count)));
		const int32 Rows = (Count + Cols - 1) / Cols;
		const double Usable = FMath::Max(8.0, 2.0 * (static_cast<double>(HalfExtentM) - UnitEdgeMarginM));
		const double Spacing = FMath::Min(static_cast<double>(UnitSpacingMaxM), 0.95 * Usable / static_cast<double>(FMath::Max(Cols, Rows)));
		const double Limit = FMath::Max(1.0, static_cast<double>(HalfExtentM) - 2.0);
		Out.Reserve(Count);
		for (int32 I = 0; I < Count; ++I)
		{
			const int32 C = I % Cols;
			const int32 R = I / Cols;
			const uint32 H0 = MixU32(static_cast<uint32>(I) * 2654435761u + 0x51ED270Bu);
			const uint32 H1 = MixU32(H0 + 0x9E3779B9u);
			const uint32 H2 = MixU32(H1 + 0x9E3779B9u);
			FUnitSlot S;
			const double X = (static_cast<double>(C) - 0.5 * (Cols - 1)) * Spacing + 0.3 * Spacing * SignedUnit(H0);
			const double Y = (static_cast<double>(R) - 0.5 * (Rows - 1)) * Spacing + 0.3 * Spacing * SignedUnit(H1);
			S.X = static_cast<float>(FMath::Clamp(X, -Limit, Limit));
			S.Y = static_cast<float>(FMath::Clamp(Y, -Limit, Limit));
			S.YawDeg = static_cast<float>(180.0 * (SignedUnit(H2) + 1.0));
			Out.Add(S);
		}
	}

	FTransform UnitTransform(const FTerrainHeightfield& HF, const FUnitSlot& Slot)
	{
		const double X = Slot.X;
		const double Y = Slot.Y;
		const double D = UnitProbeM;
		const double Z = HF.SampleSurface(X, Y);
		const double DzDx = (static_cast<double>(HF.SampleSurface(X + D, Y)) - static_cast<double>(HF.SampleSurface(X - D, Y))) / (2.0 * D);
		const double DzDy = (static_cast<double>(HF.SampleSurface(X, Y + D)) - static_cast<double>(HF.SampleSurface(X, Y - D))) / (2.0 * D);
		const FVector Normal = FVector(-DzDx, -DzDy, 1.0).GetSafeNormal();
		const FQuat Tilt = FQuat::FindBetweenNormals(FVector::UpVector, Normal);
		const FQuat Yaw(FVector::UpVector, FMath::DegreesToRadians(static_cast<double>(Slot.YawDeg)));
		return FTransform((Tilt * Yaw).GetNormalized(), FVector(X * 100.0, Y * 100.0, Z * 100.0), FVector(UnitScale));
	}

	bool UnitDependsOnVertexRect(const FUnitSlot& Slot, int32 HalfExtentM, const FTerrainRect& R)
	{
		if (R.IsEmpty())
		{
			return false;
		}
		// A sample at vertex-space u reads vertices floor(u) and floor(u) + 1 (clamped to the grid); the rect holds vertices [X0, X1), so a sample is
		// affected when u >= X0 - 1 and u < X1. The unit reads u in [x + E - D, x + E + D].
		const double D = static_cast<double>(UnitProbeM);
		const double U = static_cast<double>(Slot.X) + HalfExtentM;
		const double V = static_cast<double>(Slot.Y) + HalfExtentM;
		return U + D >= static_cast<double>(R.X0) - 1.0 && U - D < static_cast<double>(R.X1) && V + D >= static_cast<double>(R.Y0) - 1.0 && V - D < static_cast<double>(R.Y1);
	}

	uint64 UnitsFnv(TConstArrayView<FTransform> Transforms)
	{
		uint64 H = 14695981039346656037ull;
		for (const FTransform& T : Transforms)
		{
			const FVector L = T.GetLocation();
			const FQuat Q = T.GetRotation();
			FnvFloat(H, static_cast<float>(L.X));
			FnvFloat(H, static_cast<float>(L.Y));
			FnvFloat(H, static_cast<float>(L.Z));
			FnvFloat(H, static_cast<float>(Q.X));
			FnvFloat(H, static_cast<float>(Q.Y));
			FnvFloat(H, static_cast<float>(Q.Z));
			FnvFloat(H, static_cast<float>(Q.W));
		}
		return H;
	}
}

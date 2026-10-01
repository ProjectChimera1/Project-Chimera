// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Data/TerrainPick.h"

namespace ChimeraTerrain
{
	namespace
	{
		/** Moller-Trumbore, double sided. Returns the ray parameter in OutT. Ray and triangle are in cell space. */
		bool TerrainPickTriangle(const FVector& O, const FVector& D, const FVector& A, const FVector& B, const FVector& C, double& OutT)
		{
			const FVector E1 = B - A;
			const FVector E2 = C - A;
			const FVector P = FVector::CrossProduct(D, E2);
			const double Det = FVector::DotProduct(E1, P);
			if (FMath::Abs(Det) < 1e-14)
			{
				return false; // ray parallel to the triangle
			}
			const double InvDet = 1.0 / Det;
			const FVector S = O - A;
			const double U = FVector::DotProduct(S, P) * InvDet;
			constexpr double Tol = 1e-9;
			if (U < -Tol || U > 1.0 + Tol)
			{
				return false;
			}
			const FVector Q = FVector::CrossProduct(S, E1);
			const double V = FVector::DotProduct(D, Q) * InvDet;
			if (V < -Tol || U + V > 1.0 + Tol)
			{
				return false;
			}
			OutT = FVector::DotProduct(E2, Q) * InvDet;
			return true;
		}
	}

	bool TerrainPick::RayCast(const FTerrainHeightfield& HF, const FVector& Origin, const FVector& Direction, double MaxDistance, FTerrainHit& OutHit)
	{
		OutHit = FTerrainHit();
		if (!HF.IsInitialized() || MaxDistance <= 0.0)
		{
			return false;
		}
		const double Len = Direction.Size();
		if (Len < 1e-12)
		{
			return false;
		}
		const FVector D = Direction / Len;
		const double E = HF.HalfExtentM();
		const int32 Q = HF.Quads();

		// Cell space: u = x + E, v = y + E, so cell (i, j) spans [i, i+1] x [j, j+1] and vertex (i, j) is at (i, j).
		const FVector O(Origin.X + E, Origin.Y + E, Origin.Z);

		double T0 = 0.0;
		double T1 = MaxDistance;
		const auto Slab = [&](double Org, double Dir, double Lo, double Hi) -> bool
		{
			if (FMath::Abs(Dir) < 1e-15)
			{
				return Org >= Lo && Org <= Hi;
			}
			double A = (Lo - Org) / Dir;
			double B = (Hi - Org) / Dir;
			if (A > B)
			{
				Swap(A, B);
			}
			T0 = FMath::Max(T0, A);
			T1 = FMath::Min(T1, B);
			return T0 <= T1;
		};
		constexpr double ZPad = 1e-6;
		if (!Slab(O.X, D.X, 0.0, static_cast<double>(Q)) || !Slab(O.Y, D.Y, 0.0, static_cast<double>(Q)) || !Slab(O.Z, D.Z, -MaxHeightM - ZPad, MaxHeightM + ZPad))
		{
			return false;
		}

		const FVector Start = O + D * T0;
		int32 CellX = FMath::Clamp(static_cast<int32>(FMath::FloorToDouble(Start.X)), 0, Q - 1);
		int32 CellY = FMath::Clamp(static_cast<int32>(FMath::FloorToDouble(Start.Y)), 0, Q - 1);

		const int32 StepX = D.X > 0.0 ? 1 : -1;
		const int32 StepY = D.Y > 0.0 ? 1 : -1;
		const double Inf = TNumericLimits<double>::Max();
		const double DeltaX = FMath::Abs(D.X) < 1e-15 ? Inf : 1.0 / FMath::Abs(D.X);
		const double DeltaY = FMath::Abs(D.Y) < 1e-15 ? Inf : 1.0 / FMath::Abs(D.Y);
		double TMaxX = Inf;
		double TMaxY = Inf;
		if (DeltaX != Inf)
		{
			TMaxX = ((D.X > 0.0 ? CellX + 1 : CellX) - O.X) / D.X;
		}
		if (DeltaY != Inf)
		{
			TMaxY = ((D.Y > 0.0 ? CellY + 1 : CellY) - O.Y) / D.Y;
		}

		const int32 MaxSteps = 2 * Q + 8;
		for (int32 Step = 0; Step < MaxSteps; ++Step)
		{
			const double HBL = HF.GetHeight(CellX, CellY);
			const double HBR = HF.GetHeight(CellX + 1, CellY);
			const double HTL = HF.GetHeight(CellX, CellY + 1);
			const double HTR = HF.GetHeight(CellX + 1, CellY + 1);
			const double CX = CellX;
			const double CY = CellY;
			const FVector BL(CX, CY, HBL);
			const FVector BR(CX + 1.0, CY, HBR);
			const FVector TL(CX, CY + 1.0, HTL);
			const FVector TR(CX + 1.0, CY + 1.0, HTR);

			double Best = Inf;
			double T;
			if (TerrainPickTriangle(O, D, BL, TL, TR, T) && T >= -1e-9 && T <= MaxDistance && T < Best)
			{
				Best = T;
			}
			if (TerrainPickTriangle(O, D, BL, TR, BR, T) && T >= -1e-9 && T <= MaxDistance && T < Best)
			{
				Best = T;
			}
			if (Best != Inf)
			{
				Best = FMath::Max(Best, 0.0);
				OutHit.bHit = true;
				OutHit.Distance = Best;
				OutHit.Position = FVector(O.X + D.X * Best - E, O.Y + D.Y * Best - E, O.Z + D.Z * Best);
				OutHit.CellX = CellX;
				OutHit.CellY = CellY;
				return true;
			}

			// Advance to the next cell along the ray.
			double TNext;
			if (TMaxX < TMaxY)
			{
				TNext = TMaxX;
				CellX += StepX;
				TMaxX += DeltaX;
			}
			else
			{
				TNext = TMaxY;
				CellY += StepY;
				TMaxY += DeltaY;
			}
			if (TNext > T1 + 1e-9 || CellX < 0 || CellX >= Q || CellY < 0 || CellY >= Q)
			{
				break;
			}
		}
		return false;
	}
}

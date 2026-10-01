// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Data/TerrainBrush.h"
#include "Data/TerrainUndo.h"

namespace ChimeraTerrain
{
	void FTerrainBrushParams::Clamp()
	{
		DiameterM = FMath::Clamp(DiameterM, MinDiameterM, MaxDiameterM);
		Strength = FMath::Clamp(Strength, MinStrength, MaxStrength);
		PaintLayer = FMath::Clamp(PaintLayer, 0, SplatLayerCount - 1);
	}

	float FTerrainBrush::FlattenTargetAt(const FTerrainHeightfield& HF, float CenterX, float CenterY)
	{
		return HF.SampleSurface(CenterX, CenterY);
	}

	int32 FTerrainBrush::PaintTexel(uint8* Texel, int32 Layer, int32 Delta)
	{
		if (Delta <= 0 || Layer < 0 || Layer >= SplatLayerCount)
		{
			return 0;
		}
		// S = total weight held by the other layers (255 - Texel[Layer] while the sum invariant holds).
		int32 OthersSum = 0;
		for (int32 I = 0; I < SplatLayerCount; ++I)
		{
			if (I != Layer)
			{
				OthersSum += Texel[I];
			}
		}
		Delta = FMath::Min(Delta, FMath::Min(OthersSum, 255 - static_cast<int32>(Texel[Layer])));
		if (Delta <= 0)
		{
			return 0;
		}

		int32 Take[SplatLayerCount] = {0, 0, 0, 0};
		int32 Rem[SplatLayerCount] = {-1, -1, -1, -1};
		int32 FloorSum = 0;
		for (int32 I = 0; I < SplatLayerCount; ++I)
		{
			if (I == Layer)
			{
				continue;
			}
			const int32 Product = Delta * static_cast<int32>(Texel[I]);
			Take[I] = Product / OthersSum;
			Rem[I] = Product % OthersSum;
			FloorSum += Take[I];
		}

		// Leftover units, one each, to the largest fractional remainder; scanning ascending with a strict '>' resolves ties to the lower id.
		for (int32 Leftover = Delta - FloorSum; Leftover > 0; --Leftover)
		{
			int32 Best = -1;
			int32 BestRem = 0;
			for (int32 I = 0; I < SplatLayerCount; ++I)
			{
				if (Rem[I] > BestRem)
				{
					Best = I;
					BestRem = Rem[I];
				}
			}
			if (Best < 0)
			{
				break;
			}
			++Take[Best];
			Rem[Best] = -1;
		}

		for (int32 I = 0; I < SplatLayerCount; ++I)
		{
			if (I != Layer)
			{
				Texel[I] = static_cast<uint8>(Texel[I] - Take[I]);
			}
		}
		Texel[Layer] = static_cast<uint8>(Texel[Layer] + Delta);
		return Delta;
	}

	FTerrainTickResult FTerrainBrush::ApplyTick(FTerrainHeightfield& HF, const FTerrainBrushParams& InParams, float CenterX, float CenterY, FTerrainUndo* Undo)
	{
		FTerrainTickResult Result;
		if (!HF.IsInitialized())
		{
			return Result;
		}
		FTerrainBrushParams Params = InParams;
		Params.Clamp();

		const float R = Params.Radius();
		const float K = Params.K();
		const float E = static_cast<float>(HF.HalfExtentM());
		// Centre in vertex units (vertex index == world + E).
		const float VX = CenterX + E;
		const float VY = CenterY + E;
		const int32 W = HF.Width();

		if (Params.Mode == ETerrainBrushMode::Paint)
		{
			// Texel centre in vertex units is (T + 0.5) * 0.5.
			const int32 S = HF.SplatSize();
			const FTerrainRect Rect = FTerrainRect(
				FMath::FloorToInt((VX - R) * SplatTexelsPerMeter) - 1, FMath::FloorToInt((VY - R) * SplatTexelsPerMeter) - 1,
				FMath::FloorToInt((VX + R) * SplatTexelsPerMeter) + 2, FMath::FloorToInt((VY + R) * SplatTexelsPerMeter) + 2).Intersect(FTerrainRect(0, 0, S, S));
			if (Rect.IsEmpty())
			{
				return Result;
			}
			if (Undo)
			{
				Undo->Touch(HF, FTerrainRect(), Rect);
			}
			for (int32 TY = Rect.Y0; TY < Rect.Y1; ++TY)
			{
				const float DY = (static_cast<float>(TY) + 0.5f) * 0.5f - VY;
				for (int32 TX = Rect.X0; TX < Rect.X1; ++TX)
				{
					const float DX = (static_cast<float>(TX) + 0.5f) * 0.5f - VX;
					const float Dist = FMath::Sqrt(DX * DX + DY * DY);
					if (Dist >= R)
					{
						continue;
					}
					const float A = 1.0f - Dist / R;
					const int32 Delta = FMath::FloorToInt(A * K * 255.0f + 0.5f);
					if (PaintTexel(HF.SplatTexel(TX, TY), Params.PaintLayer, Delta) > 0)
					{
						Result.SplatRect.Include(TX, TY);
						++Result.TexelsChanged;
					}
				}
			}
			return Result;
		}

		const FTerrainRect Rect = FTerrainRect(
			FMath::FloorToInt(VX - R) - 1, FMath::FloorToInt(VY - R) - 1, FMath::FloorToInt(VX + R) + 2, FMath::FloorToInt(VY + R) + 2).Intersect(FTerrainRect(0, 0, W, W));
		if (Rect.IsEmpty())
		{
			return Result;
		}
		if (Undo)
		{
			Undo->Touch(HF, Rect, FTerrainRect());
		}

		// Smooth reads the tick-start heights of the footprint plus a one-vertex apron, so the result does not depend on visit order.
		FTerrainRect Snap;
		TArray<float> Snapshot;
		if (Params.Mode == ETerrainBrushMode::Smooth)
		{
			Snap = Rect.Expanded(1).Intersect(FTerrainRect(0, 0, W, W));
			Snapshot.SetNumUninitialized(static_cast<int32>(Snap.Area()));
			for (int32 Y = Snap.Y0; Y < Snap.Y1; ++Y)
			{
				for (int32 X = Snap.X0; X < Snap.X1; ++X)
				{
					Snapshot[(Y - Snap.Y0) * Snap.Width() + (X - Snap.X0)] = HF.GetHeight(X, Y);
				}
			}
		}
		const auto H0 = [&](int32 X, int32 Y) -> float
		{
			X = FMath::Clamp(X, Snap.X0, Snap.X1 - 1);
			Y = FMath::Clamp(Y, Snap.Y0, Snap.Y1 - 1);
			return Snapshot[(Y - Snap.Y0) * Snap.Width() + (X - Snap.X0)];
		};

		for (int32 Y = Rect.Y0; Y < Rect.Y1; ++Y)
		{
			const float DY = static_cast<float>(Y) - VY;
			for (int32 X = Rect.X0; X < Rect.X1; ++X)
			{
				const float DX = static_cast<float>(X) - VX;
				const float Dist = FMath::Sqrt(DX * DX + DY * DY);
				if (Dist >= R)
				{
					continue;
				}
				const float A = 1.0f - Dist / R;
				const float Old = HF.GetHeight(X, Y);
				float New = Old;
				switch (Params.Mode)
				{
				case ETerrainBrushMode::Raise:
					New = Old + A * K;
					break;
				case ETerrainBrushMode::Lower:
					New = Old - A * K;
					break;
				case ETerrainBrushMode::Smooth:
				{
					// Terrain3D AVERAGE: lerp(h, avg5(h0), clamp(2*a*k, 0.02, 1)), avg5 = centre plus the four neighbours.
					const float Avg = (H0(X, Y) + H0(X - 1, Y) + H0(X + 1, Y) + H0(X, Y - 1) + H0(X, Y + 1)) * 0.2f;
					const float T = FMath::Clamp(2.0f * A * K, 0.02f, 1.0f);
					New = FMath::Lerp(Old, Avg, T);
					break;
				}
				case ETerrainBrushMode::Flatten:
				{
					const float T = FMath::Clamp(A * K, 0.0f, 1.0f);
					New = FMath::Lerp(Old, Params.FlattenTarget, T);
					break;
				}
				default:
					break;
				}
				New = FMath::Clamp(New, -MaxHeightM, MaxHeightM);
				if (FMemory::Memcmp(&New, &Old, sizeof(float)) != 0)
				{
					HF.Heights[Y * W + X] = New;
					Result.HeightRect.Include(X, Y);
					++Result.VerticesChanged;
				}
			}
		}
		return Result;
	}
}

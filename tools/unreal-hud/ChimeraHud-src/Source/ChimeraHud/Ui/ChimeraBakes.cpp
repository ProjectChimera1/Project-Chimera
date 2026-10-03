// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/ChimeraBakes.h"
#include "ChimeraHud.h"
#include "Brushes/SlateDynamicImageBrush.h"
#include <cmath>

namespace ChimeraBakes
{
	namespace
	{
		/** An RGBA accumulator in straight sRGB bytes space (0..255 per channel, alpha 0..1). */
		struct FPix
		{
			double R = 0, G = 0, B = 0, A = 0;	// colour premultiplied by alpha (0..255 scale), alpha 0..1
		};

		/** Composites a straight colour (0..255) with alpha over the accumulator (sRGB maths, as Skia blends in the back buffer). */
		void Over(FPix& Dst, uint32 Rgb, double Alpha)
		{
			const double Sr = (double)((Rgb >> 16) & 0xFF), Sg = (double)((Rgb >> 8) & 0xFF), Sb = (double)(Rgb & 0xFF);
			const double Keep = 1.0 - Alpha;
			Dst.R = Sr * Alpha + Dst.R * Keep;
			Dst.G = Sg * Alpha + Dst.G * Keep;
			Dst.B = Sb * Alpha + Dst.B * Keep;
			Dst.A = Alpha + Dst.A * Keep;
		}

		/** Process-lifetime brush cache (deliberately leaked: widgets keep raw FSlateBrush pointers; static teardown order is unsafe). */
		TMap<FString, FBake>& Cache()
		{
			static TMap<FString, FBake>* Map = new TMap<FString, FBake>();
			return *Map;
		}

		/** Packs premultiplied accumulators (already averaged) into straight-alpha BGRA bytes. */
		void Store(TArray<uint8>& Bgra, int32 Index, const FPix& P)
		{
			uint8* Out = &Bgra[Index * 4];
			if (P.A <= 1e-6)
			{
				Out[0] = Out[1] = Out[2] = Out[3] = 0;
				return;
			}
			auto Q = [](double V) { return (uint8)FMath::Clamp((int32)FMath::FloorToInt((float)(V + 0.5)), 0, 255); };
			Out[0] = Q(P.B / P.A);
			Out[1] = Q(P.G / P.A);
			Out[2] = Q(P.R / P.A);
			Out[3] = Q(P.A * 255.0);
		}

		FBake Finish(const FString& Key, const FIntPoint& Origin, const FIntPoint& Size, const TArray<uint8>& Bgra)
		{
			FBake Result;
			Result.Origin = Origin;
			Result.Size = Size;
			TSharedPtr<FSlateDynamicImageBrush> Brush = FSlateDynamicImageBrush::CreateWithImageData(
				FName(*(FString(TEXT("ChimeraBake_")) + Key)), FVector2f((float)Size.X, (float)Size.Y), Bgra);
			// Leak the shared pointer on purpose (see Cache()).
			new TSharedPtr<FSlateDynamicImageBrush>(Brush);
			Result.Brush = Brush.Get();
			UE_LOG(LogChimeraHud, Display, TEXT("bake %s %dx%d origin %d,%d"), *Key, Size.X, Size.Y, Origin.X, Origin.Y);
			Cache().Add(Key, Result);
			return Result;
		}

		/** Gaussian-blurred coverage of the 1D interval [A, B] at x (unit-area kernel of the given sigma). */
		double BlurredSpan(double A, double B, double X, double Sigma)
		{
			const double K = 1.0 / (Sigma * 1.41421356237309505);
			return 0.5 * (std::erf((B - X) * K) - std::erf((A - X) * K));
		}

		/**
		 * Anti-aliased coverage of the ellipse (centre Cx,Cy; radii Rx,Ry) at the pixel centre (X, Y), the way Skia's analytic ellipse
		 * and circle shaders compute it: the implicit function divided by its gradient length approximates the signed distance (px,
		 * positive outside), and coverage = clamp(0.5 - distance). A non-positive radius covers nothing.
		 */
		double EllipseCoverage(double X, double Y, double Cx, double Cy, double Rx, double Ry)
		{
			if (Rx <= 0.0 || Ry <= 0.0)
			{
				return 0.0;
			}
			const double Dx = X - Cx, Dy = Y - Cy;
			if (FMath::IsNearlyEqual(Rx, Ry))
			{
				// A circle: Skia's circle shader uses the true distance to the edge.
				return FMath::Clamp(0.5 - (std::sqrt(Dx * Dx + Dy * Dy) - Rx), 0.0, 1.0);
			}
			const double Implicit = Dx * Dx / (Rx * Rx) + Dy * Dy / (Ry * Ry) - 1.0;
			const double GradX = Dx / (Rx * Rx), GradY = Dy / (Ry * Ry);
			const double GradLen = 2.0 * std::sqrt(GradX * GradX + GradY * GradY);
			const double Dist = GradLen > 1e-9 ? Implicit / GradLen : -FMath::Min(Rx, Ry);
			return FMath::Clamp(0.5 - Dist, 0.0, 1.0);
		}

	}

	FBake OuterShadow(int32 W, int32 H, int32 OffsetX, int32 OffsetY, float Blur, uint32 Rgb, float Alpha)
	{
		const FString Key = FString::Printf(TEXT("outer_%dx%d_%d_%d_%.1f_%06x_%.3f"), W, H, OffsetX, OffsetY, Blur, Rgb, Alpha);
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		const double Sigma = Blur * 0.5;
		const int32 M = FMath::CeilToInt(Sigma * 3.0) + FMath::Max(FMath::Abs(OffsetX), FMath::Abs(OffsetY));
		const int32 IW = W + 2 * M, IH = H + 2 * M;
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(IW * IH * 4);
		for (int32 J = 0; J < IH; ++J)
		{
			for (int32 I = 0; I < IW; ++I)
			{
				const double Px = (double)(I - M) + 0.5, Py = (double)(J - M) + 0.5;	// box-local pixel centre
				FPix P;
				const bool bInsideBox = Px > 0.0 && Px < (double)W && Py > 0.0 && Py < (double)H;
				if (!bInsideBox)
				{
					const double Cov = BlurredSpan(OffsetX, OffsetX + W, Px, Sigma) * BlurredSpan(OffsetY, OffsetY + H, Py, Sigma);
					Over(P, Rgb, Cov * (double)Alpha);
				}
				Store(Bgra, J * IW + I, P);
			}
		}
		return Finish(Key, FIntPoint(-M, -M), FIntPoint(IW, IH), Bgra);
	}

	FBake InsetShadow(int32 W, int32 H, int32 OffsetX, int32 OffsetY, float Blur, uint32 Rgb, float Alpha)
	{
		const FString Key = FString::Printf(TEXT("inset_%dx%d_%d_%d_%.1f_%06x_%.3f"), W, H, OffsetX, OffsetY, Blur, Rgb, Alpha);
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		const double Sigma = Blur * 0.5;
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(W * H * 4);
		for (int32 J = 0; J < H; ++J)
		{
			for (int32 I = 0; I < W; ++I)
			{
				const double Px = (double)I + 0.5, Py = (double)J + 0.5;
				// The shadow is everything outside the offset box, blurred: 1 - coverage of the offset box.
				const double Cov = BlurredSpan(OffsetX, OffsetX + W, Px, Sigma) * BlurredSpan(OffsetY, OffsetY + H, Py, Sigma);
				FPix P;
				Over(P, Rgb, (1.0 - Cov) * (double)Alpha);
				Store(Bgra, J * W + I, P);
			}
		}
		return Finish(Key, FIntPoint::ZeroValue, FIntPoint(W, H), Bgra);
	}

	FBake RadialGradient(int32 W, int32 H, float CenterXPct, float CenterYPct, const TArray<FGradStop>& Stops)
	{
		FString Key = FString::Printf(TEXT("radial_%dx%d_%.2f_%.2f"), W, H, CenterXPct, CenterYPct);
		for (const FGradStop& S : Stops) { Key += FString::Printf(TEXT("_%.4f_%06x_%.4f"), S.Pos, S.Rgb, S.Alpha); }
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		const double Cx = CenterXPct * 0.01 * W, Cy = CenterYPct * 0.01 * H;
		// farthest-corner circle: the radius reaches the corner farthest from the centre.
		const double Fx = FMath::Max(Cx, (double)W - Cx), Fy = FMath::Max(Cy, (double)H - Cy);
		const double Radius = std::sqrt(Fx * Fx + Fy * Fy);
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(W * H * 4);
		for (int32 J = 0; J < H; ++J)
		{
			for (int32 I = 0; I < W; ++I)
			{
				const double Dx = (double)I + 0.5 - Cx, Dy = (double)J + 0.5 - Cy;
				const double T = std::sqrt(Dx * Dx + Dy * Dy) / Radius;
				// Premultiplied interpolation between the surrounding stops (constant outside the first and last stop).
				double Pr = 0, Pg = 0, Pb = 0, Pa = 0;
				auto Premult = [](const FGradStop& S, double& R, double& G, double& B, double& A)
				{
					A = S.Alpha;
					R = (double)((S.Rgb >> 16) & 0xFF) * S.Alpha;
					G = (double)((S.Rgb >> 8) & 0xFF) * S.Alpha;
					B = (double)(S.Rgb & 0xFF) * S.Alpha;
				};
				if (T <= Stops[0].Pos)
				{
					Premult(Stops[0], Pr, Pg, Pb, Pa);
				}
				else if (T >= Stops.Last().Pos)
				{
					Premult(Stops.Last(), Pr, Pg, Pb, Pa);
				}
				else
				{
					for (int32 K = 0; K + 1 < Stops.Num(); ++K)
					{
						if (T >= Stops[K].Pos && T <= Stops[K + 1].Pos)
						{
							const double U = (T - Stops[K].Pos) / FMath::Max((double)(Stops[K + 1].Pos - Stops[K].Pos), 1e-9);
							double R0, G0, B0, A0, R1, G1, B1, A1;
							Premult(Stops[K], R0, G0, B0, A0);
							Premult(Stops[K + 1], R1, G1, B1, A1);
							Pr = R0 + (R1 - R0) * U; Pg = G0 + (G1 - G0) * U; Pb = B0 + (B1 - B0) * U; Pa = A0 + (A1 - A0) * U;
							break;
						}
					}
				}
				FPix P;
				P.R = Pr; P.G = Pg; P.B = Pb; P.A = Pa;
				Store(Bgra, J * W + I, P);
			}
		}
		return Finish(Key, FIntPoint::ZeroValue, FIntPoint(W, H), Bgra);
	}

	FBake RingSet(int32 W, int32 H, int32 Border, uint32 RingRgb, int32 OuterSpread, uint32 OuterRgb, float OuterAlpha, uint32 InnerRgb, float InnerAlpha)
	{
		const FString Key = FString::Printf(TEXT("ring_%dx%d_%d_%06x_%d_%06x_%.3f_%06x_%.3f"), W, H, Border, RingRgb, OuterSpread, OuterRgb, OuterAlpha, InnerRgb, InnerAlpha);
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		const int32 IW = W + 2 * OuterSpread, IH = H + 2 * OuterSpread;
		const double Cx = IW * 0.5, Cy = IH * 0.5;
		const double Rx = W * 0.5, Ry = H * 0.5;	// border-radius 50%: a true ellipse
		const double Sp = (double)OuterSpread, Bw = (double)Border;
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(IW * IH * 4);
		for (int32 J = 0; J < IH; ++J)
		{
			for (int32 I = 0; I < IW; ++I)
			{
				// Blink paints each CSS layer with its own analytic coverage, one after the other (outer shadow clipped out of the border
				// box, inset hairline clipped to the padding box, border ring): a fringe pixel shared by two layers keeps a bit of what lies
				// under both, so the layers are composited in paint order rather than merged into one opaque union.
				const double X = (double)I + 0.5, Y = (double)J + 0.5;
				const double CovSpread = EllipseCoverage(X, Y, Cx, Cy, Rx + Sp, Ry + Sp);
				const double CovBorderBox = EllipseCoverage(X, Y, Cx, Cy, Rx, Ry);
				const double CovPadding = EllipseCoverage(X, Y, Cx, Cy, Rx - Bw, Ry - Bw);
				const double CovInner = EllipseCoverage(X, Y, Cx, Cy, Rx - Bw - 1.0, Ry - Bw - 1.0);
				FPix Sum;
				Over(Sum, OuterRgb, CovSpread * (1.0 - CovBorderBox) * (double)OuterAlpha);
				Over(Sum, InnerRgb, CovPadding * (1.0 - CovInner) * (double)InnerAlpha);
				Over(Sum, RingRgb, CovBorderBox * (1.0 - CovPadding));
				Store(Bgra, J * IW + I, Sum);
			}
		}
		return Finish(Key, FIntPoint(-OuterSpread, -OuterSpread), FIntPoint(IW, IH), Bgra);
	}

	FBake RingedNode(int32 Size, uint32 FillRgb, uint32 BorderRgb)
	{
		const FString Key = FString::Printf(TEXT("node_%d_%06x_%06x"), Size, FillRgb, BorderRgb);
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		const double C = Size * 0.5;
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(Size * Size * 4);
		for (int32 J = 0; J < Size; ++J)
		{
			for (int32 I = 0; I < Size; ++I)
			{
				// Background over the border box first, then the border ring, each with its own analytic coverage (see RingSet).
				const double X = (double)I + 0.5, Y = (double)J + 0.5;
				const double CovOuter = EllipseCoverage(X, Y, C, C, C, C);
				const double CovInner = EllipseCoverage(X, Y, C, C, C - 1.0, C - 1.0);
				FPix Sum;
				Over(Sum, FillRgb, CovOuter);
				Over(Sum, BorderRgb, CovOuter * (1.0 - CovInner));
				Store(Bgra, J * Size + I, Sum);
			}
		}
		return Finish(Key, FIntPoint::ZeroValue, FIntPoint(Size, Size), Bgra);
	}

	FBake DotSquare(int32 Size, uint32 FillRgb, uint32 BorderRgb)
	{
		const FString Key = FString::Printf(TEXT("dot_%d_%06x_%06x"), Size, FillRgb, BorderRgb);
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		const bool bHalf = (Size & 1) != 0;
		const int32 Out = bHalf ? Size + 1 : Size;
		// The box is anti-aliased at its true position inside the image: x.5 for an odd size (translate(-50%) of an odd box on a
		// snapped layout position), whole pixels for an even one. Blink paints the background over the border box and then the border
		// ring (outer box minus the box inset by 1), each with the exact area coverage of its edge pixels, so the fringe pixels carry the
		// fill colour under the border colour (the bluish halo of the reference).
		const double Left = bHalf ? 0.5 : 0.0;
		auto Span = [](double A0, double A1, int32 Px) -> double
		{
			return FMath::Max(0.0, FMath::Min(A1, (double)Px + 1.0) - FMath::Max(A0, (double)Px));
		};
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(Out * Out * 4);
		for (int32 J = 0; J < Out; ++J)
		{
			for (int32 I = 0; I < Out; ++I)
			{
				const double CoverBox = Span(Left, Left + Size, I) * Span(Left, Left + Size, J);
				const double CoverIn = Span(Left + 1.0, Left + Size - 1.0, I) * Span(Left + 1.0, Left + Size - 1.0, J);
				FPix P;
				Over(P, FillRgb, CoverBox);
				Over(P, BorderRgb, CoverBox - CoverIn);
				Store(Bgra, J * Out + I, P);
			}
		}
		return Finish(Key, FIntPoint::ZeroValue, FIntPoint(Out, Out), Bgra);
	}
}

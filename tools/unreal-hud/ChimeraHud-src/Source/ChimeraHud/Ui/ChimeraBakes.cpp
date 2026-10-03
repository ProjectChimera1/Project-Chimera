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

		FBake Finish(const FString& Key, const FIntPoint& Origin, const FIntPoint& Size, const TArray<uint8>& Bgra, const FMargin* NineSlice = nullptr)
		{
			FBake Result;
			Result.Origin = Origin;
			Result.Size = Size;
			TSharedPtr<FSlateDynamicImageBrush> Brush = FSlateDynamicImageBrush::CreateWithImageData(
				FName(*(FString(TEXT("ChimeraBake_")) + Key)), FVector2f((float)Size.X, (float)Size.Y), Bgra);
			if (!Brush.IsValid())
			{
				// CreateWithImageData returns null without a Slate renderer or when the texture cannot be made.
				UE_LOG(LogChimeraHud, Error, TEXT("bake %s %dx%d: no brush (Slate renderer unavailable or texture creation failed)"), *Key, Size.X, Size.Y);
				Cache().Add(Key, Result);
				return Result;
			}
			if (NineSlice)
			{
				// Margins are fractions of the image (SlateBrush.h); the corner blocks then draw 1:1 (ElementBatcher.cpp box path).
				Brush->DrawAs = ESlateBrushDrawType::Box;
				Brush->Margin = *NineSlice;
			}
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

		/** sRGB bytes of a 0xRRGGBB colour. */
		void Bytes(uint32 Rgb, int32 Out[3])
		{
			Out[0] = (int32)((Rgb >> 16) & 0xFF);
			Out[1] = (int32)((Rgb >> 8) & 0xFF);
			Out[2] = (int32)(Rgb & 0xFF);
		}

		/**
		 * SkBlendRaster model: how Skia's 8-bit raster pipeline lays an anti-aliased path of colour Src over an opaque Dst. The
		 * coverage becomes an alpha byte by truncation (a = floor(coverage x 255), as the analytic AA scan converter emits it), the
		 * source is premultiplied and the destination scaled with rounding divides (SkMulDiv255Round):
		 * out = round(Src a / 255) + round(Dst (255 - a) / 255). Fitted against the empty slot's dashed border (88 partially covered
		 * pixels: 18 one-level misses, against 38 for an exact-coverage blend) and confirmed on the slot keycap's corners.
		 */
		void BlendRaster(int32 Dst[3], uint32 Src, double Coverage)
		{
			const int32 A = FMath::Clamp((int32)FMath::FloorToDouble(Coverage * 255.0 + 1e-9), 0, 255);
			int32 S[3];
			Bytes(Src, S);
			for (int32 C = 0; C < 3; ++C)
			{
				const int32 Sp = (int32)FMath::FloorToDouble((double)(S[C] * A) / 255.0 + 0.5);
				const int32 Dp = (int32)FMath::FloorToDouble((double)(Dst[C] * (255 - A)) / 255.0 + 0.5);
				Dst[C] = FMath::Min(Sp + Dp, 255);
			}
		}

		/** Writes an opaque sRGB pixel. */
		void StoreOpaque(TArray<uint8>& Bgra, int32 Index, const int32 Rgb[3])
		{
			uint8* Out = &Bgra[Index * 4];
			Out[0] = (uint8)Rgb[2];
			Out[1] = (uint8)Rgb[1];
			Out[2] = (uint8)Rgb[0];
			Out[3] = 255;
		}

		/**
		 * Skia's analytic coverage of a rounded rect [X0, X1] x [Y0, Y1] at the pixel centre (X, Y): the straight edges give
		 * clamp(0.5 + inside distance) per axis (exactly 0 or 1 on this pixel-aligned board), and inside a corner's quadrant the
		 * corner ellipse's EllipseCoverage. Radii per corner (TL, TR, BR, BL); a radius of 0 on either axis is a square corner.
		 */
		double RRectCoverage(double X, double Y, double X0, double Y0, double X1, double Y1, const FVector4f& Rx, const FVector4f& Ry)
		{
			double Cov = FMath::Min(FMath::Min(FMath::Clamp(X - X0 + 0.5, 0.0, 1.0), FMath::Clamp(X1 - X + 0.5, 0.0, 1.0)),
				FMath::Min(FMath::Clamp(Y - Y0 + 0.5, 0.0, 1.0), FMath::Clamp(Y1 - Y + 0.5, 0.0, 1.0)));
			const double CRx[4] = { Rx.X, Rx.Y, Rx.Z, Rx.W };
			const double CRy[4] = { Ry.X, Ry.Y, Ry.Z, Ry.W };
			for (int32 K = 0; K < 4; ++K)
			{
				if (CRx[K] <= 0.0 || CRy[K] <= 0.0)
				{
					continue;
				}
				const bool bRight = (K == 1 || K == 2), bBottom = (K == 2 || K == 3);
				const double Cx = bRight ? X1 - CRx[K] : X0 + CRx[K];
				const double Cy = bBottom ? Y1 - CRy[K] : Y0 + CRy[K];
				const bool bInQuadrant = (bRight ? X > Cx : X < Cx) && (bBottom ? Y > Cy : Y < Cy);
				if (bInQuadrant)
				{
					Cov = FMath::Min(Cov, EllipseCoverage(X, Y, Cx, Cy, CRx[K], CRy[K]));
				}
			}
			return Cov;
		}
	}

	FBake OuterShadow(int32 W, int32 H, int32 OffsetX, int32 OffsetY, float Blur, uint32 Rgb, float Alpha, TOptional<uint32> Backdrop)
	{
		const FString Key = FString::Printf(TEXT("outer_%dx%d_%d_%d_%.1f_%06x_%.3f"), W, H, OffsetX, OffsetY, Blur, Rgb, Alpha)
			+ (Backdrop.IsSet() ? FString::Printf(TEXT("_on%06x"), Backdrop.GetValue()) : FString());
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
					if (Backdrop.IsSet())
					{
						// Skia's blur mask alpha byte, then one rounded sRGB blend over the known backdrop, drawn opaque.
						const int32 A8 = FMath::Clamp((int32)FMath::FloorToDouble(Cov * (double)Alpha * 255.0 + 0.5), 0, 255);
						if (A8 > 0)
						{
							int32 S[3], D[3], O[3];
							Bytes(Rgb, S);
							Bytes(Backdrop.GetValue(), D);
							const double A = (double)A8 / 255.0;
							for (int32 C = 0; C < 3; ++C)
							{
								O[C] = FMath::Clamp((int32)FMath::FloorToDouble((double)S[C] * A + (double)D[C] * (1.0 - A) + 0.5), 0, 255);
							}
							StoreOpaque(Bgra, J * IW + I, O);
						}
						continue;
					}
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

	float DashGap(float SideLength, float DashLength, float GapLength)
	{
		// Blink SelectBestDashGap, open path.
		const float Available = SideLength + GapLength;
		const float MinDashes = FMath::FloorToFloat(Available / (DashLength + GapLength));
		const float MaxDashes = MinDashes + 1.f;
		const float MinGap = (SideLength - MinDashes * DashLength) / (MinDashes - 1.f);
		const float MaxGap = (SideLength - MaxDashes * DashLength) / (MaxDashes - 1.f);
		return (MaxGap <= 0.f || FMath::Abs(MinGap - GapLength) < FMath::Abs(MaxGap - GapLength)) ? MinGap : MaxGap;
	}

	FBake DashedBorder(int32 W, int32 H, uint32 Rgb, TOptional<uint32> Backdrop)
	{
		const FString Key = FString::Printf(TEXT("dashed_%dx%d_%06x"), W, H, Rgb)
			+ (Backdrop.IsSet() ? FString::Printf(TEXT("_on%06x"), Backdrop.GetValue()) : FString());
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		constexpr double Dash = 3.0, NominalGap = 2.0;
		// Coverage along one side: pixel P of a side of length L is covered by the dashes' overlap with [P, P + 1].
		auto SideCoverage = [&](int32 Length, TArray<double>& Cov)
		{
			Cov.Init(0.0, Length);
			const double Gap = (double)DashGap((float)Length, (float)Dash, (float)NominalGap);
			for (double Start = 0.0; Start < (double)Length - 1e-6; Start += Dash + Gap)
			{
				const double End = FMath::Min(Start + Dash, (double)Length);
				for (int32 Px = FMath::FloorToInt((float)Start); Px < Length && (double)Px < End; ++Px)
				{
					Cov[Px] += FMath::Max(0.0, FMath::Min(End, (double)Px + 1.0) - FMath::Max(Start, (double)Px));
				}
			}
		};
		TArray<double> AlongX, AlongY;
		SideCoverage(W, AlongX);
		SideCoverage(H, AlongY);
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(W * H * 4);
		for (int32 J = 0; J < H; ++J)
		{
			for (int32 I = 0; I < W; ++I)
			{
				double Keep = 1.0;
				if (J == 0 || J == H - 1) { Keep *= 1.0 - FMath::Min(AlongX[I], 1.0); }
				if (I == 0 || I == W - 1) { Keep *= 1.0 - FMath::Min(AlongY[J], 1.0); }
				if (Backdrop.IsSet())
				{
					if (J == 0 || J == H - 1 || I == 0 || I == W - 1)
					{
						int32 D[3];
						Bytes(Backdrop.GetValue(), D);
						BlendRaster(D, Rgb, 1.0 - Keep);
						StoreOpaque(Bgra, J * W + I, D);
					}
					continue;	// inside the border: transparent (the slot shows the card through)
				}
				FPix P;
				Over(P, Rgb, 1.0 - Keep);
				Store(Bgra, J * W + I, P);
			}
		}
		return Finish(Key, FIntPoint::ZeroValue, FIntPoint(W, H), Bgra);
	}

	FBake KeycapFace(float Radius, uint32 BorderRgb, uint32 FillRgb, TOptional<uint32> Backdrop)
	{
		const FString Key = FString::Printf(TEXT("keycap_%.2f_%06x_%06x"), Radius, BorderRgb, FillRgb)
			+ (Backdrop.IsSet() ? FString::Printf(TEXT("_on%06x"), Backdrop.GetValue()) : FString());
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		// 9-slice blocks: every curved pixel lies within ceil(R) of its edges, plus one straight pixel so the row and column next to
		// the stretched middle equal it (bilinear sampling across a block seam then reads equal texels).
		const int32 M = FMath::CeilToInt(Radius) + 1;
		const int32 W = 2 * M + 1, H = 2 * M + 1;
		const float R = Radius;
		const FVector4f Outer(R, R, R, R);
		// CSS inner radii: the outer radius minus the border width on each axis (1 left, right and top, 2 bottom), floored at 0.
		const float Side = FMath::Max(R - 1.f, 0.f), Top = FMath::Max(R - 1.f, 0.f), Bot = FMath::Max(R - 2.f, 0.f);
		const FVector4f InnerRx(Side, Side, Side, Side);
		const FVector4f InnerRy(Top, Top, Bot, Bot);
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(W * H * 4);
		for (int32 J = 0; J < H; ++J)
		{
			for (int32 I = 0; I < W; ++I)
			{
				const double X = (double)I + 0.5, Y = (double)J + 0.5;
				const double CovOuter = RRectCoverage(X, Y, 0.0, 0.0, (double)W, (double)H, Outer, Outer);
				const double CovInner = RRectCoverage(X, Y, 1.0, 1.0, (double)W - 1.0, (double)H - 2.0, InnerRx, InnerRy);
				if (Backdrop.IsSet())
				{
					int32 D[3];
					Bytes(Backdrop.GetValue(), D);
					BlendRaster(D, BorderRgb, CovOuter);
					BlendRaster(D, FillRgb, CovInner);
					StoreOpaque(Bgra, J * W + I, D);
				}
				else
				{
					FPix P;
					Over(P, BorderRgb, CovOuter);
					Over(P, FillRgb, CovInner);
					Store(Bgra, J * W + I, P);
				}
			}
		}
		const FMargin Nine((float)M / (float)W, (float)M / (float)H, (float)M / (float)W, (float)M / (float)H);
		return Finish(Key, FIntPoint::ZeroValue, FIntPoint(W, H), Bgra, &Nine);
	}

	FBake LaserHead(float Phase, float HeadW, float Opacity)
	{
		const FString Key = FString::Printf(TEXT("laser_%.9g_%.9g_%.3f"), Phase, HeadW, Opacity);
		if (const FBake* Found = Cache().Find(Key)) { return *Found; }
		const int32 First = 0;
		const int32 End = FMath::CeilToInt(Phase + HeadW);
		if (HeadW <= 0.f || Phase < 0.f || Phase >= 1.f || End <= First)
		{
			const FBake None;
			Cache().Add(Key, None);
			return None;
		}
		// Stops (position along the head, sRGB colour, alpha): transparent black, #E3C887 70%, #FFF3D6 88%, transparent black.
		struct FStop { double Pos, R, G, B, A; };
		static const FStop Stops[4] = {
			{ 0.00, 0.0, 0.0, 0.0, 0.0 }, { 0.70, 227.0, 200.0, 135.0, 1.0 }, { 0.88, 255.0, 243.0, 214.0, 1.0 }, { 1.00, 0.0, 0.0, 0.0, 0.0 } };
		const int32 N = End - First;
		TArray<uint8> Bgra;
		Bgra.SetNumZeroed(N * 4);
		for (int32 I = 0; I < N; ++I)
		{
			const double Px = (double)(First + I);
			// The part of this pixel the head covers (the caller clips to the line's own box).
			const double Area = FMath::Max(0.0, FMath::Min(Px + 1.0, (double)Phase + (double)HeadW) - FMath::Max(Px, (double)Phase));
			const double U = FMath::Clamp((Px + 0.5 - (double)Phase) / (double)HeadW, 0.0, 1.0);
			int32 K = 0;
			while (K < 2 && U > Stops[K + 1].Pos) { ++K; }
			const double F = (U - Stops[K].Pos) / (Stops[K + 1].Pos - Stops[K].Pos);
			// CSS gradients interpolate premultiplied colours.
			const double A = Stops[K].A + (Stops[K + 1].A - Stops[K].A) * F;
			const double Pr = Stops[K].R * Stops[K].A + (Stops[K + 1].R * Stops[K + 1].A - Stops[K].R * Stops[K].A) * F;
			const double Pg = Stops[K].G * Stops[K].A + (Stops[K + 1].G * Stops[K + 1].A - Stops[K].G * Stops[K].A) * F;
			const double Pb = Stops[K].B * Stops[K].A + (Stops[K + 1].B * Stops[K + 1].A - Stops[K].B * Stops[K].A) * F;
			FPix P;
			if (A > 1e-9)
			{
				const double Alpha = A * (double)Opacity * Area;
				P.R = Pr / A * Alpha;
				P.G = Pg / A * Alpha;
				P.B = Pb / A * Alpha;
				P.A = Alpha;
			}
			Store(Bgra, I, P);
		}
		return Finish(Key, FIntPoint(First, 0), FIntPoint(N, 1), Bgra);
	}
}

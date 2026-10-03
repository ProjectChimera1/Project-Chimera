// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraOrnamentFrame.h"
#include "Ui/ChimeraBakes.h"
#include "Ui/ChimeraUi.h"
#include "ChimeraHud.h"
#include "Layout/Clipping.h"
#include "Rendering/DrawElements.h"

namespace
{
	constexpr float TileW = 16.f, TileH = 8.f;
	constexpr float RopeTop = -4.f;			// strips: top -4, 8 tall (centred on the 1 px top border just above the container)
	constexpr float SealSize = 14.f;
	constexpr float SealTop = -7.f;			// seals: top -7
	constexpr float CornerInset = 64.f;		// levers.js:130: corners level 5 -> 58, + 6
	constexpr float LaserOpacity = 0.95f;
	constexpr float LaserHeadFrac = 0.28f;	// background-size 28% 100%
	constexpr double LaserCycle = 19.0, LaserActive = 0.2, LaserSideStep = 3.8;

	/** Blink snaps a layout edge to the pixel grid half up. */
	float Snap(float V) { return FMath::FloorToFloat(V + 0.5f); }

	/** Process-lifetime PNG brush from HudData/Ornaments (loaded through ChimeraUi's logging loader, kept like the bakes). */
	const FSlateBrush* OrnamentBrush(const TCHAR* File)
	{
		static TMap<FString, TSharedPtr<FSlateBrush>>* Map = new TMap<FString, TSharedPtr<FSlateBrush>>();
		TSharedPtr<FSlateBrush>& B = Map->FindOrAdd(File);
		if (!B.IsValid())
		{
			B = ChimeraUi::LoadPngBrush(ChimeraUi::HudDataPath(FString(TEXT("Ornaments/")) + File), FName(*(FString(TEXT("ChimeraOrn_")) + File)));
			if (!B.IsValid())
			{
				UE_LOG(LogChimeraHud, Error, TEXT("ornament %s did not load"), File);
			}
		}
		return B.Get();
	}
}

void SChimeraOrnamentFrame::Construct(const FArguments& InArgs)
{
	FrameWidth = InArgs._FrameWidth;
	bSigils = InArgs._Sigils;
	LaserBaseDelay = InArgs._LaserBaseDelay;
	// Decoration only: never take a hit test (the band reaches 7 px above the panel, over the tab row).
	SetVisibility(EVisibility::HitTestInvisible);
	Tile = OrnamentBrush(TEXT("rope_tile.png"));
	TileHalf = OrnamentBrush(TEXT("rope_tile_h.png"));
	if (bSigils)
	{
		Seal[0] = OrnamentBrush(TEXT("sigil_0.png"));
		Seal[1] = OrnamentBrush(TEXT("sigil_1.png"));
		Seal[2] = OrnamentBrush(TEXT("sigil_2.png"));
		Seal[3] = OrnamentBrush(TEXT("sigil_3.png"));
	}
	if (ChimeraUi::IsHudClockFrozen())
	{
		const double Clock = ChimeraUi::HudClockSeconds();
		UE_LOG(LogChimeraHud, Display, TEXT("ornament frame w=%.1f laser t=%.3f delay=%.1f top head at %.4f%%"),
			FrameWidth, Clock, LaserBaseDelay, LaserPosition(Clock, LaserBaseDelay, false) * 100.0);
	}
}

FVector2D SChimeraOrnamentFrame::ComputeDesiredSize(float) const
{
	return FVector2D(FrameWidth + 2.f * Margin, Margin + TileH);
}

double SChimeraOrnamentFrame::CubicBezier(double X, double P1x, double P1y, double P2x, double P2y)
{
	// x(t) = 3(1-t)^2 t P1x + 3(1-t) t^2 P2x + t^3 is monotonic for 0 <= P1x, P2x <= 1: bisect for t, then evaluate y(t).
	double Lo = 0.0, Hi = 1.0;
	for (int32 I = 0; I < 60; ++I)
	{
		const double T = 0.5 * (Lo + Hi);
		const double Bx = 3.0 * (1.0 - T) * (1.0 - T) * T * P1x + 3.0 * (1.0 - T) * T * T * P2x + T * T * T;
		(Bx < X ? Lo : Hi) = T;
	}
	const double T = 0.5 * (Lo + Hi);
	return 3.0 * (1.0 - T) * (1.0 - T) * T * P1y + 3.0 * (1.0 - T) * T * T * P2y + T * T * T;
}

double SChimeraOrnamentFrame::LaserPosition(double T, double Delay, bool bReverse)
{
	const double From = bReverse ? 1.4 : -0.4, To = bReverse ? -0.4 : 1.4;
	const double Local = T - Delay;
	if (Local < 0.0)
	{
		return From;	// fill-mode both: the first keyframe holds during the delay
	}
	const double Progress = FMath::Fmod(Local, LaserCycle) / LaserCycle;
	if (Progress >= LaserActive)
	{
		return To;		// the 20%..100% keyframe: resting past the end of the line
	}
	return From + (To - From) * CubicBezier(Progress / LaserActive, 0.45, 0.05, 0.55, 0.95);
}

int32 SChimeraOrnamentFrame::PaintStrip(const FGeometry& Geo, FSlateWindowElementList& Out, int32 Layer, float X0, float X1, bool bRight) const
{
	// Painted box: the snapped edges. Tile origin: snapped left + (width - 16) x position, position 0 or 100%.
	const float C0 = Snap(X0), C1 = Snap(X1);
	const float Origin = C0 + (bRight ? (X1 - X0) - TileW : 0.f);
	const bool bHalf = (Origin - FMath::FloorToFloat(Origin)) > 0.25f;
	const FSlateBrush* Brush = bHalf ? TileHalf : Tile;
	if (!Brush || C1 <= C0)
	{
		return Layer;
	}
	// The half-phase image shows SVG x = c - 0.5 in column c, so it is drawn half a pixel left of the origin, on whole pixels.
	float Start = FMath::FloorToFloat(Origin);
	Start -= TileW * FMath::CeilToFloat((Start - C0) / TileW);
	const FVector2f BandOrigin(Margin, Margin);
	const FGeometry ClipBox = Geo.MakeChild(FVector2f(C1 - C0, TileH), FSlateLayoutTransform(BandOrigin + FVector2f(C0, RopeTop)));
	Out.PushClip(FSlateClippingZone(ClipBox));
	for (float X = Start; X < C1; X += TileW)
	{
		FSlateDrawElement::MakeBox(Out, Layer, Geo.ToPaintGeometry(FVector2f(TileW, TileH),
			FSlateLayoutTransform(BandOrigin + FVector2f(X, RopeTop))), Brush);
	}
	Out.PopClip();
	return Layer;
}

int32 SChimeraOrnamentFrame::OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
	FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const
{
	const FVector2f BandOrigin(Margin, Margin);
	const float W = FrameWidth, Half = FrameWidth * 0.5f;

	// Ropes (z-index 2 inside the frame): DOM order left, right, left, right; the second pair lands on the first pair's pixels.
	int32 Layer = LayerId;
	for (int32 Pass = 0; Pass < 2; ++Pass)
	{
		PaintStrip(AllottedGeometry, OutDrawElements, Layer, 0.f, Half, false);
		PaintStrip(AllottedGeometry, OutDrawElements, Layer, Half, W, true);
		++Layer;
	}

	// Seals (z-index 3), DOM order of r4 8.2: corner-left m0, corner-right m1, corner-left m2, corner-right m3, centre m1, centre m3,
	// left-mid m2, right-mid m0. Wrappers are 14x14 at top -7; a half-pixel left snaps half up.
	if (bSigils)
	{
		struct FSealAt { float X; int32 Variant; };
		const float Left = CornerInset, Right = W - CornerInset - SealSize, Centre = (W - SealSize) * 0.5f;
		const FSealAt Seals[8] = { { Left, 0 }, { Right, 1 }, { Left, 2 }, { Right, 3 }, { Centre, 1 }, { Centre, 3 },
			{ -SealSize * 0.5f, 2 }, { W - SealSize * 0.5f, 0 } };
		for (const FSealAt& S : Seals)
		{
			if (const FSlateBrush* B = Seal[S.Variant])
			{
				FSlateDrawElement::MakeBox(OutDrawElements, Layer, AllottedGeometry.ToPaintGeometry(FVector2f(SealSize, SealSize),
					FSlateLayoutTransform(BandOrigin + FVector2f(Snap(S.X), SealTop))), B);
			}
		}
		++Layer;
	}

	// Laser heads (z-index 3, after the seals): the top line at the container's top (y 0, delay base + 0) and the bottom line one
	// pixel above it (the 0-height container's bottom edge, delay base + 2 x 3.8 s, chiRunXr). Vertical sides have no length.
	// Each head is one bake per sub-pixel phase (ChimeraBakes::LaserHead), drawn at floor(head x) under a clip of the line's box
	// [0, W) x 1 px: W is a whole number of px, so the clip gives exactly the box-clipped coverage. The frozen frame uses the exact
	// phase; live motion is quantised to quarter pixels, so a panel never holds more than four head textures and a moving head
	// creates none after its first sweep.
	const bool bFrozen = ChimeraUi::IsHudClockFrozen();
	const double T = ChimeraUi::HudClockSeconds();
	const float HeadW = LaserHeadFrac * W;
	struct FLine { float Y; double Delay; bool bReverse; };
	const FLine Lines[2] = { { 0.f, (double)LaserBaseDelay, false }, { -1.f, (double)LaserBaseDelay + 2.0 * LaserSideStep, true } };
	for (const FLine& L : Lines)
	{
		const double Pos = LaserPosition(T, L.Delay, L.bReverse);
		float HeadX = (float)(Pos * (double)(W - HeadW));
		if (!bFrozen)
		{
			HeadX = FMath::RoundToFloat(HeadX * 4.f) * 0.25f;
		}
		const float Cell = FMath::FloorToFloat(HeadX);
		const float Phase = HeadX - Cell;	// exact in float for |HeadX| < 2^23
		const ChimeraBakes::FBake Head = ChimeraBakes::LaserHead(Phase, HeadW, LaserOpacity);
		const float X0 = Cell + (float)Head.Origin.X, X1 = X0 + (float)Head.Size.X;
		if (!Head.Brush || X1 <= 0.f || X0 >= W)
		{
			continue;	// parked off the line (e.g. the command card's top head at -40% at t = 3)
		}
		const FGeometry LineBox = AllottedGeometry.MakeChild(FVector2f(W, 1.f), FSlateLayoutTransform(BandOrigin + FVector2f(0.f, L.Y)));
		OutDrawElements.PushClip(FSlateClippingZone(LineBox));
		FSlateDrawElement::MakeBox(OutDrawElements, Layer, AllottedGeometry.ToPaintGeometry(FVector2f((float)Head.Size.X, 1.f),
			FSlateLayoutTransform(BandOrigin + FVector2f(X0, L.Y))), Head.Brush);
		OutDrawElements.PopClip();
	}
	return Layer;
}

// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"

struct FSlateBrush;

/**
 * The ornament frame of board 3.1a (r4 section 8, levers "Woven rope" at level 5): a 0-height container on a panel's top inner edge
 * (z-index 3, so it paints over the panel's content), holding
 *  - the rope: two repeat-x strips of the 16x8 tile, 8 px tall centred on the top border (top -4), each half the frame wide, the left
 *    one phased from its left edge and the right one with `background-position: 100%`; the "bottom" strips of the same frame land
 *    on the same pixels (container height 0), so every strip is drawn twice, as the board does;
 *  - the sigils (minimap panel and command card only): eight 14x14 seals at top -7 on five positions, in DOM order, so the later
 *    seal of each pair is the visible one (r4 8.2);
 *  - the laser heads (r4 8.3): of the four 1 px side lines only the horizontal ones have a length; each head is a CSS gradient
 *    28% of the side long, panned by `chiRunX` (background-position -40% to 140% in the first 20% of a 19 s cycle,
 *    cubic-bezier(.45,.05,.55,.95), fill-mode both), delays base + 3.8 s per side.
 * Pixels come from the PNG route (make_svgs.py: Chromium's raster of our rope and seal SVGs; the rope as a CSS background at both
 * pixel phases the board produces) and from ChimeraBakes::LaserHead (one bake per sub-pixel phase, clipped to the line); the time is
 * the one HUD clock (ChimeraUi::HudClockSeconds). Placement follows Blink's snapping, measured in T7:
 * a strip's painted box is [round(left), round(right)), its tile origin is round(left) + (width - 16) x position (so a half-pixel
 * panel half gets the half-phase tile), and a seal at a half-pixel left is drawn at round(left).
 * The widget's own box is the band [frame left - 7, frame top - 7] .. [frame right + 7, frame top + 8]; it is placed by the panel.
 */
class SChimeraOrnamentFrame : public SLeafWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraOrnamentFrame)
		: _FrameWidth(0.f)
		, _Sigils(true)
		, _LaserBaseDelay(0.f)
		, _Stretch(false)
	{}
		/** The container's width in px (the panel's width inside its left/right borders: 255, 1364, 299). */
		SLATE_ARGUMENT(float, FrameWidth)
		/** False on the selection panel (built with noSigils). */
		SLATE_ARGUMENT(bool, Sigils)
		/** The frame's laser delay option: 0 s minimap, 1.5 s selection, 3 s command card. */
		SLATE_ARGUMENT(float, LaserBaseDelay)
		/** True when the panel stretches with the screen (the selection panel, CSS left/right): the frame width is then the painted
		 *  width minus the two margins, rounded to whole px; FrameWidth only sets the desired size. */
		SLATE_ARGUMENT(bool, Stretch)
	SLATE_END_ARGS()

	/** Band margin around the 0-height container: the seals reach 7 px above, left and right of it. */
	static constexpr float Margin = 7.f;

	void Construct(const FArguments& InArgs);

	virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
		FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const override;
	virtual FVector2D ComputeDesiredSize(float) const override;

	/** CSS cubic-bezier(P1x, P1y, P2x, P2y) evaluated at progress X (solved for the curve parameter by bisection). */
	static double CubicBezier(double X, double P1x, double P1y, double P2x, double P2y);

	/**
	 * `chiRunX` background-position (fraction, -0.4 .. 1.4) of a laser at HUD time T with the given delay: the active phase starts
	 * at T = Delay with progress 0 (plan B 2.8 boundary rule); before it the first keyframe holds (fill-mode both); after the first
	 * 20% of each 19 s cycle the head rests at 140%. bReverse is `chiRunXr` (140% to -40%).
	 */
	static double LaserPosition(double T, double Delay, bool bReverse);

private:
	float FrameWidth = 0.f;
	bool bSigils = true;
	float LaserBaseDelay = 0.f;
	bool bStretch = false;
	const FSlateBrush* Tile = nullptr;		// rope tile, origin on a pixel edge
	const FSlateBrush* TileHalf = nullptr;	// rope tile, origin half a pixel right
	const FSlateBrush* Seal[4] = { nullptr, nullptr, nullptr, nullptr };

	/** Paints one repeat-x strip of the box [X0, X1) at y = -4 (frame-local), background-position 0 or 100%. */
	int32 PaintStrip(const FGeometry& Geo, FSlateWindowElementList& Out, int32 Layer, float X0, float X1, bool bRight) const;
};

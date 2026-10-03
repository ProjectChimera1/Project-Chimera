// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FSlateBrush;

/**
 * CPU bakes (plan B 2.4): the CSS effects Slate has no primitive for, computed from numbers of the mockup's CSS (blur, spread, radii,
 * gradient stops, sizes), never from reference pixels. Each returns a straight-alpha BGRA8 sRGB image wrapped as a Slate brush at its
 * own pixel size (drawn 1:1; Slate blends in the gamma-encoded back buffer, as Chromium does, T3 blend test) plus the offset of the
 * image's top-left from the CSS box's top-left. Brushes are cached for the process (keyed by parameters) and never freed.
 * All sRGB maths; ellipse edges use the analytic coverage of Skia's shaders (distance-based, clamp(0.5 - d)) and each CSS layer is
 * composited in Blink's paint order with its own coverage; blurs use the exact Gaussian integral at the pixel centre (CSS blur radius
 * b = Gaussian sigma b/2).
 */
namespace ChimeraBakes
{
	struct FBake
	{
		const FSlateBrush* Brush = nullptr;
		FIntPoint Origin = FIntPoint::ZeroValue;	// image top-left relative to the CSS box top-left
		FIntPoint Size = FIntPoint::ZeroValue;		// image size in px
	};

	/**
	 * Outer box-shadow of a W x H box: `OffsetX OffsetY Blur 0 rgba(Rgb, Alpha)`. Zero inside the box (CSS clips it out).
	 * The shadow alpha is quantised as Skia's blur mask is, a8 = round(coverage x Alpha x 255). With Backdrop set (the opaque colour
	 * the shadow falls on, known for the minimap plate: the panel fill), every pixel with a8 > 0 is pre-composited over it in sRGB
	 * bytes, round(Rgb a + Backdrop (1 - a)), and drawn opaque (the strategy of plan B 2.4's locked buttons); pixels with a8 = 0 stay
	 * transparent, so nothing under the bake's margin is touched. Measured on the plate's shadow ring (5,276 px over the panel fill,
	 * T7): this model leaves 372 one-level misses where the GPU blend of the straight-alpha bake leaves 2,184.
	 */
	FBake OuterShadow(int32 W, int32 H, int32 OffsetX, int32 OffsetY, float Blur, uint32 Rgb, float Alpha, TOptional<uint32> Backdrop = TOptional<uint32>());

	/** Inset box-shadow of the W x H padding box: `inset OffsetX OffsetY Blur 0 rgba(Rgb, Alpha)`. The image is the padding box. */
	FBake InsetShadow(int32 W, int32 H, int32 OffsetX, int32 OffsetY, float Blur, uint32 Rgb, float Alpha);

	struct FGradStop
	{
		float Pos;		// 0..1 along the gradient ray (fraction of the radius)
		uint32 Rgb;
		float Alpha;
	};

	/** CSS `radial-gradient(circle at CX% CY%, stops)` (farthest-corner) over a W x H box; colours interpolate premultiplied, as CSS does. */
	FBake RadialGradient(int32 W, int32 H, float CenterXPct, float CenterYPct, const TArray<FGradStop>& Stops);

	/**
	 * The selection ring (r4 5.2): an ellipse W x H with a Border px solid RingRgb border (border-radius 50%), an outer box-shadow
	 * `0 0 0 OuterSpread rgba(OuterRgb, OuterAlpha)` and an inset hairline `inset 0 0 0 1px rgba(InnerRgb, InnerAlpha)`.
	 * Origin is (-OuterSpread, -OuterSpread) from the ellipse box.
	 */
	FBake RingSet(int32 W, int32 H, int32 Border, uint32 RingRgb, int32 OuterSpread, uint32 OuterRgb, float OuterAlpha, uint32 InnerRgb, float InnerAlpha);

	/** A circle of Size px (border-radius 50%) with a 1 px Border ring over a Fill: the minimap's ringed corner nodes. */
	FBake RingedNode(int32 Size, uint32 FillRgb, uint32 BorderRgb);

	/**
	 * A Size x Size square with a 1 px border (background under it, as background-clip: border-box), anti-aliased at its true position:
	 * an odd Size lands on x.5 in both axes (CSS `translate(-50%, -50%)` of a snapped position), so the image is Size + 1 square with
	 * area-coverage fringe pixels; an even Size lands on whole pixels and is crisp (image Size).
	 */
	FBake DotSquare(int32 Size, uint32 FillRgb, uint32 BorderRgb);

	/**
	 * A W x H box's 1 px dashed border (CSS `border: 1px dashed`, transparent inside), one image the size of the box. Blink's dash
	 * arithmetic for a 1 px stroke (StrokeData: dash 3 px, nominal gap 2 px; SelectBestDashGap re-fits the gap to the side length
	 * so the dashes end flush with both corners): the gap is whichever of the two candidates (one dash fewer or one more) lies
	 * nearer the nominal gap. Each side is stroked over its full border-box length from its start corner, and a dash edge that
	 * falls inside a pixel gives that pixel the exact area coverage. Measured on a 121x41 box (r5 2.6) and on the 64x60 slot.
	 * With Backdrop set (the opaque colour under the border: the command card's fill under the empty slot), each border pixel is
	 * pre-composited over it the way Skia's raster pipeline blends an anti-aliased path (SkBlendRaster model below) and drawn opaque.
	 */
	FBake DashedBorder(int32 W, int32 H, uint32 Rgb, TOptional<uint32> Backdrop = TOptional<uint32>());

	/**
	 * Keycap face (plan B 2.4, r4 5.1): a border box with corner radius Radius in BorderRgb and, over it, the padding box inset
	 * (1, 1, 1, 2) in FillRgb with CSS's inner radii (Radius minus the border width on each axis: elliptical at the 2 px bottom
	 * border, square where that reaches 0). Each layer is anti-aliased with Skia's analytic rounded-rect coverage (straight edges
	 * crisp, corner pixels clamp(0.5 - distance) as in EllipseCoverage) and composited in that order, as Blink paints a keycap whose
	 * background is drawn under its border. Returned as a 9-slice brush (DrawAs Box): the corner blocks are drawn 1:1 and the one
	 * middle row and column stretch, so one small image serves every keycap width. With Backdrop set (the opaque colour the keycap
	 * sits on), both layers are blended over it with the SkBlendRaster model and the image is opaque; without it the layers are
	 * composited over transparent in float (straight alpha, blended by the GPU).
	 * Measured on the empty slot V's keycap (T7): corner pixels (36 px) differ from the board by 14 levels in total with the
	 * backdrop model, 110 with Slate's rounded-box shader.
	 */
	FBake KeycapFace(float Radius, uint32 BorderRgb, uint32 FillRgb, TOptional<uint32> Backdrop = TOptional<uint32>());

	/**
	 * One CSS laser head (r4 8.3): `linear-gradient(90deg, transparent, #E3C887 70%, #FFF3D6 88%, transparent)` sized HeadW px, drawn
	 * at Opacity, no-repeat, starting Phase px (0 <= Phase < 1) right of the image's left edge. Colours interpolate premultiplied
	 * (CSS), are sampled at each pixel centre, and a pixel the head covers only in part takes that area fraction. The image is
	 * ceil(Phase + HeadW) x 1 px with Origin (0, 0); the caller draws it at floor(head x) under a clip of the line's box [0, BoxW),
	 * which gives the same pixels as clipping the coverage to the box because the box edges are whole pixels.
	 * Deviation from plan B 2.4 (MakeGradient with 16 pre-sampled stops), T7: a gradient element is sampled at its vertices, so the
	 * fractional end pixels of a head at a sub-pixel position are not area-weighted as Chromium's are; the bake gives them exactly.
	 * Keyed by (Phase, HeadW, Opacity) only, so a head costs one texture per sub-pixel phase: the frozen frame uses its exact
	 * phase (one bake per line), live motion quantises the head to quarter pixels (at most four bakes per panel width, ever).
	 */
	FBake LaserHead(float Phase, float HeadW, float Opacity);

	/** Gaps of the dash fit for a side of the given length (exposed for the log line and tests). */
	float DashGap(float SideLength, float DashLength, float GapLength);
}

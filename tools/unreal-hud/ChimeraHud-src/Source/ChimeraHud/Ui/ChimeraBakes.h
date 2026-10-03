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

	/** Outer box-shadow of a W x H box: `OffsetX OffsetY Blur 0 rgba(Rgb, Alpha)`. Zero inside the box (CSS clips it out). */
	FBake OuterShadow(int32 W, int32 H, int32 OffsetX, int32 OffsetY, float Blur, uint32 Rgb, float Alpha);

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
}

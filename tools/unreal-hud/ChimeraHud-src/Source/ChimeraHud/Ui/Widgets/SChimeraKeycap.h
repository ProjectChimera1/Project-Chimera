// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"

/**
 * Keycap (r4 5.1: "used everywhere"): mono 600 10px/14px label, padding 0 4, 1 px #5C5446 sides and top, a 2 px bottom, radius 3
 * (2 on the command card). Drawn as plan B 2.4's two layers, an outer box in the border colour and the inner box inset
 * (1, 1, 1, 2) in the face colour, with CSS's inner radii (outer radius minus the border width on each axis, so the bottom
 * corners are elliptical), baked into one 9-slice image with Skia's analytic corner coverage (T7, ChimeraBakes::KeycapFace).
 */
class SChimeraKeycap : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraKeycap)
		: _Fill(0x20242B)
		, _Radius(3.f)
		, _Height(18.f)
		, _MinWidth(0.f)
	{}
		SLATE_ARGUMENT(FString, Label)
		/** Face colour (sRGB): #20242B on the menu, #14161A on the toast, #0B0C0E on the command card. */
		SLATE_ARGUMENT(uint32, Fill)
		SLATE_ARGUMENT(float, Radius)
		/** Border-box height in px (18 on the menu and toast, 16 on the command card). */
		SLATE_ARGUMENT(float, Height)
		/** Border-box minimum width in px (16 on the command card: the label is centred). 0 = fit the label. */
		SLATE_ARGUMENT(float, MinWidth)
		/** Border colour (sRGB); #5C5446 unless a locked button passes its pre-composited one (plan B 2.4). */
		SLATE_ARGUMENT(TOptional<uint32>, BorderColor)
		/** Label colour (sRGB); the keycap style's colour unless overridden. */
		SLATE_ARGUMENT(TOptional<uint32>, LabelColor)
		/** The opaque colour the keycap sits on (sRGB), when known: its corner pixels are then pre-composited over it (Skia blend model). */
		SLATE_ARGUMENT(TOptional<uint32>, Backdrop)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"

/**
 * Keycap (r4 5.1: "used everywhere"): mono 600 10px/14px label, padding 0 4, 1 px #5C5446 sides and top, a 2 px bottom, radius 3
 * (2 on the command card). Drawn as plan B 2.4 prescribes: an outer fill-only rounded box in the border colour and an inner
 * fill-only rounded box inset (1, 1, 1, 2), both crisp on their straight edges (fill smoothstep spread .5, F11).
 * The inner radii follow CSS (outer radius minus the border width on each axis; the bottom corners' 2 px border makes them
 * elliptical in CSS, approximated here by the mean of the two axes).
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
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

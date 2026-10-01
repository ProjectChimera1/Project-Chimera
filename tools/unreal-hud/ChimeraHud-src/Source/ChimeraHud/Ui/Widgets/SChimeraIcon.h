// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"

struct FSlateBrush;

/**
 * One line icon of the mockup's icons.js set (r4 section 7): drawn white and tinted (svg route: the vector cache rasterises the
 * SVG with nanosvg at the on-screen size; png route: Chromium's raster of the same SVG). The quad is pixel-snapped like Blink
 * snaps an inline <svg> root's border box, so a 16 px icon at x=121.81 lands on column 122. The brush comes from ChimeraUi::IconBrush.
 */
class SChimeraIcon : public SLeafWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraIcon)
		: _Size(16)
		, _Color(0xC9A86A)
	{}
		/** icons.js name, e.g. "resource". */
		SLATE_ARGUMENT(FString, Icon)
		/** On-screen size in px (12, 14, 16 or 24 on board 3.1a). */
		SLATE_ARGUMENT(int32, Size)
		/** Tint as sRGB 0xRRGGBB (gold by default). */
		SLATE_ARGUMENT(uint32, Color)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual FVector2D ComputeDesiredSize(float LayoutScaleMultiplier) const override;
	virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
		FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const override;

private:
	const FSlateBrush* Brush = nullptr;
	int32 Size = 16;
	FLinearColor Tint = FLinearColor::White;
};

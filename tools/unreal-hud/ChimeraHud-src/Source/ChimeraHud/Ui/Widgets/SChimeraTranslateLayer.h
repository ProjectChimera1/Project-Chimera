// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"

/**
 * Publishes the absolute x of a group that CSS places with a fractional transform (board 3.1a's clock group:
 * left:50%; transform:translateX(-50%), which puts it at 960 - 146.92/2 = 886.53).
 *
 * Blink snaps every box inside a transformed layer to the layer's own pixel grid and then applies the layer's fractional
 * translation when it rasterises, with anti-aliasing. So a 1 px border inside the group lands at 886.53 + 53 = 939.53 and covers
 * column 939 by 0.47 and column 940 by 0.53. Slate snaps every quad to whole pixels instead. Widgets that must reproduce the
 * split coverage (SChimeraSubpixelBorder) read OriginX in their OnPaint, which runs after this widget's OnPaint has set it.
 * Layout and the children's geometry are unchanged: this widget only records where it was painted.
 */
class SChimeraTranslateLayer : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraTranslateLayer) {}
		SLATE_DEFAULT_SLOT(FArguments, Content)
		/** Receives the layer's absolute x (px) on every paint. Shared with the children that need it. */
		SLATE_ARGUMENT(TSharedPtr<float>, OriginX)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
		FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const override;

private:
	TSharedPtr<float> OriginX;
};

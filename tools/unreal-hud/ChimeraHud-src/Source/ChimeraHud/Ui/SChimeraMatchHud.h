// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

struct FSlateBrush;

/**
 * Root widget of the Match HUD. T3 content: the backdrop layer (flat colour, 1:1 PNG, or none), the optional
 * blend test pattern, and the one-time geometry log that proves 1 Slate unit = 1 px. Panels are added by later tasks.
 */
class SChimeraMatchHud : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraMatchHud) {}
		/** "#RRGGBB", an absolute PNG path, or "none". */
		SLATE_ARGUMENT(FString, Backdrop)
		/** "blend" draws the four alpha boxes of the blend test; empty draws nothing. */
		SLATE_ARGUMENT(FString, TestPattern)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
		FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const override;

private:
	TArray<TSharedPtr<FSlateBrush>> Brushes;
	mutable bool bLoggedGeometry = false;
};

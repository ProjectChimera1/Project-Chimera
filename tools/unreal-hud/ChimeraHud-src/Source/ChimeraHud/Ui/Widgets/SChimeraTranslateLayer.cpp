// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Widgets/SChimeraTranslateLayer.h"

void SChimeraTranslateLayer::Construct(const FArguments& InArgs)
{
	OriginX = InArgs._OriginX;
	SetCanTick(false);
	ChildSlot
	[
		InArgs._Content.Widget
	];
}

int32 SChimeraTranslateLayer::OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
	FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const
{
	if (OriginX.IsValid())
	{
		// Set before the children paint (SCompoundWidget::OnPaint arranges and paints them below).
		*OriginX = FVector2f(AllottedGeometry.GetAbsolutePosition()).X;
	}
	return SCompoundWidget::OnPaint(Args, AllottedGeometry, MyCullingRect, OutDrawElements, LayerId, InWidgetStyle, bParentEnabled);
}

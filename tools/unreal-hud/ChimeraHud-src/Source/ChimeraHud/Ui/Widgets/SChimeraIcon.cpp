// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Widgets/SChimeraIcon.h"
#include "Ui/ChimeraUi.h"
#include "Rendering/DrawElements.h"

void SChimeraIcon::Construct(const FArguments& InArgs)
{
	Size = InArgs._Size;
	Tint = ChimeraUi::Hex(InArgs._Color);
	Brush = ChimeraUi::IconBrush(InArgs._Icon, Size);
	SetCanTick(false);
}

FVector2D SChimeraIcon::ComputeDesiredSize(float) const
{
	return FVector2D((double)Size, (double)Size);
}

int32 SChimeraIcon::OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
	FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const
{
	if (Brush)
	{
		// Exactly Size x Size local units from the allotted top-left; the box element's vertices are pixel-snapped (round half up).
		FSlateDrawElement::MakeBox(OutDrawElements, LayerId, AllottedGeometry.ToPaintGeometry(FVector2f((float)Size, (float)Size), FSlateLayoutTransform()),
			Brush, ESlateDrawEffect::None, Tint * InWidgetStyle.GetColorAndOpacityTint());
	}
	return LayerId;
}

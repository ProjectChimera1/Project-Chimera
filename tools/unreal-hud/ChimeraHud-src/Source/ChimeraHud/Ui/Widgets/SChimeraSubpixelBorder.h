// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"

/**
 * A 1 px solid border box with a transparent fill, drawn the way Blink rasterises it inside a layer with a fractional x
 * translation (see SChimeraTranslateLayer).
 *
 * The box edges are first snapped to the layer's pixel grid (round(x - f) + f, f = the layer's fractional offset
 * origin - round(origin)), then every pixel column gets the exact area coverage of the border ring: the outer rectangle's
 * coverage minus the inner (1 px inset) rectangle's. Each column run of equal coverage is one box in the border colour at that
 * alpha; Slate blends it in the gamma-encoded back buffer, as Chromium does (T3 blend test). Rows are not translated (the CSS
 * transform is translateX only), so the top and bottom borders stay on whole rows. Without a layer (f = 0) this is a plain
 * snapped 1 px border.
 */
class SChimeraSubpixelBorder : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraSubpixelBorder)
		: _BorderColor(0x3A3F48)
		, _Padding(0.f)
	{}
		SLATE_DEFAULT_SLOT(FArguments, Content)
		/** Border colour, sRGB 0xRRGGBB. */
		SLATE_ARGUMENT(uint32, BorderColor)
		/** CSS padding inside the 1 px border. */
		SLATE_ARGUMENT(FMargin, Padding)
		/** Absolute x of the enclosing SChimeraTranslateLayer (null: no fractional translation). */
		SLATE_ARGUMENT(TSharedPtr<float>, LayerOriginX)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
		FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const override;

private:
	uint32 BorderColor = 0x3A3F48;
	TSharedPtr<float> LayerOriginX;
	mutable bool bLoggedPaint = false;
};

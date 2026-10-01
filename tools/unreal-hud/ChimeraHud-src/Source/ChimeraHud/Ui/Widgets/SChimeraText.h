// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Fonts/SlateFontInfo.h"
#include "Fonts/ShapedTextFwd.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"

/**
 * Single-line text with Chromium-like horizontal metrics (plan B 2.3, r5 5.1 extended).
 *
 * Slate rounds every shaped advance to whole pixels (SlateTextShaper.cpp:797), so a run of N glyphs drifts by up to N/2 px and a
 * flex row of chips drifts by 3 px (plan B F8). This widget shapes the string with HarfBuzz twice: at the draw scale (the glyphs
 * that are drawn) and at FontScale 16 (advances quantised to 1/16 px, GPOS kerning included when LetterSpacing != 0). Each glyph
 * is drawn at round(origin + exact pen x) (error <= 0.5 px, non-cumulative) and the widget reports the exact advance sum as its
 * desired width, so the layout around it lands where Blink's does.
 *
 * Vertically the widget is its CSS line box (LineHeight tall); the baseline is drawn at round(top + BaselineY), with BaselineY from
 * ChimeraUi::CssBaseline. Built only by ChimeraUi::Text().
 */
class SChimeraText : public SLeafWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraText)
		: _Color(FLinearColor::White)
		, _LineHeight(0.f)
		, _BaselineY(0.f)
	{}
		SLATE_ARGUMENT(FString, Text)
		SLATE_ARGUMENT(FSlateFontInfo, Font)
		SLATE_ARGUMENT(FLinearColor, Color)
		/** Height of the CSS line box in px. */
		SLATE_ARGUMENT(float, LineHeight)
		/** Baseline offset from the top of the line box in px. */
		SLATE_ARGUMENT(float, BaselineY)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual FVector2D ComputeDesiredSize(float LayoutScaleMultiplier) const override;
	virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
		FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const override;

	/** Exact advance sum in px (what the widget reports as its width). */
	float GetExactWidth() const { return ExactWidth; }

	/** Pen x of every glyph of the draw-scale sequence in px from the run start (size = glyph count + 1). */
	const TArray<float>& GetPenX() const { return PenX; }

private:
	/** Shapes at the given draw scale (cached) and builds one sub-sequence per drawn glyph. */
	void EnsureShaped(float Scale) const;

	FString Str;
	FSlateFontInfo FontInfo;
	FLinearColor Color = FLinearColor::White;
	float LineHeight = 0.f;
	float BaselineY = 0.f;

	/** Exact pen positions (px) at layout scale 1, from the FontScale 16 shaping. */
	TArray<float> PenX;
	float ExactWidth = 0.f;

	struct FGlyphDraw
	{
		FShapedGlyphSequencePtr Seq;	// the glyph alone, shaped in context
		int32 PenIndex = 0;				// index into PenX
	};
	mutable float ShapedScale = -1.f;
	mutable TArray<FGlyphDraw> Glyphs;
	mutable FShapedGlyphSequencePtr Whole;	// the whole run at the draw scale
	mutable float Ascent = 0.f;			// MaxTextHeight + TextBaseline at the draw scale: top-of-element to baseline
	mutable bool bWarnedMismatch = false;
	mutable bool bLoggedPaint = false;
};

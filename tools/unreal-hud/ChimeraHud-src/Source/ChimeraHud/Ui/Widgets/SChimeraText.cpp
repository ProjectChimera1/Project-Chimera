// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Widgets/SChimeraText.h"
#include "ChimeraHud.h"
#include "Fonts/FontCache.h"
#include "Framework/Application/SlateApplication.h"
#include "Rendering/DrawElements.h"
#include "Rendering/SlateRenderer.h"

namespace
{
	/** FontScale for the exact-advance shaping: HarfBuzz advances are rounded to whole pixels of that scale (1/16 px here). */
	constexpr float MeasureScale = 16.f;

	FShapedGlyphSequenceRef ShapeLtr(const FString& Str, const FSlateFontInfo& Font, float Scale)
	{
		const TSharedRef<FSlateFontCache> Cache = FSlateApplication::Get().GetRenderer()->GetFontCache();
		// FullShaping explicitly (as the cvar Slate.DefaultTextShapingMethod=2 does for STextBlock): HarfBuzz applies GPOS kerning
		// when the font's LetterSpacing != 0 (SlateTextShaper.cpp:723-736).
		return Cache->ShapeUnidirectionalText(Str, Font, Scale, TextBiDi::ETextDirection::LeftToRight, ETextShapingMethod::FullShaping);
	}
}

void SChimeraText::Construct(const FArguments& InArgs)
{
	Str = InArgs._Text;
	FontInfo = InArgs._Font;
	Color = InArgs._Color;
	LineHeight = InArgs._LineHeight;
	BaselineY = InArgs._BaselineY;
	SetCanTick(false);

	PenX.Reset();
	PenX.Add(0.f);
	ExactWidth = 0.f;
	if (!Str.IsEmpty())
	{
		const FShapedGlyphSequenceRef Fine = ShapeLtr(Str, FontInfo, MeasureScale);
		int32 Sum = 0;
		for (const FShapedGlyphEntry& G : Fine->GetGlyphsToRender())
		{
			Sum += G.XAdvance;
			PenX.Add((float)Sum / MeasureScale);
		}
		ExactWidth = (float)Sum / MeasureScale;
	}
}

FVector2D SChimeraText::ComputeDesiredSize(float) const
{
	return FVector2D(ExactWidth, LineHeight);
}

void SChimeraText::EnsureShaped(float Scale) const
{
	// Re-shape when the scale changes or when the font cache was flushed under us: a flush frees the font faces the cached
	// sequence points at (IsDirty, FontCache.cpp:304-315) and its glyphs would no longer render. The first flush comes right
	// after the HUD's first paint ("Flushing composite font cache on Game thread"); STextBlock's shaped-text cache does the same.
	if (Scale == ShapedScale && !(Whole.IsValid() && Whole->IsDirty()))
	{
		return;
	}
	ShapedScale = Scale;
	Glyphs.Reset();
	Ascent = 0.f;
	if (Str.IsEmpty())
	{
		return;
	}
	const FShapedGlyphSequenceRef Seq = ShapeLtr(Str, FontInfo, Scale);
	Whole = Seq;
	Ascent = (float)Seq->GetMaxTextHeight() + (float)Seq->GetTextBaseline();	// baseline below the element top (ElementBatcher.cpp:3486)
	const TArray<FShapedGlyphEntry>& Entries = Seq->GetGlyphsToRender();
	if (Entries.Num() + 1 != PenX.Num() && !bWarnedMismatch)
	{
		// The two shapings normally produce the same glyph run; if not, fall back to the draw-scale advances for the tail.
		bWarnedMismatch = true;
		UE_LOG(LogChimeraHud, Warning, TEXT("SChimeraText \"%s\": %d glyphs at scale %.2f vs %d at x%.0f"), *Str, Entries.Num(), Scale, PenX.Num() - 1, MeasureScale);
	}
	for (int32 i = 0; i < Entries.Num(); ++i)
	{
		const FShapedGlyphEntry& G = Entries[i];
		if (G.NumCharactersInGlyph == 0)
		{
			continue;	// drawn with the cluster's first glyph
		}
		bool bAnyVisible = false;
		for (int32 j = i; j < Entries.Num() && (j == i || Entries[j].NumCharactersInGlyph == 0); ++j)
		{
			bAnyVisible |= Entries[j].bIsVisible;
		}
		if (!bAnyVisible)
		{
			continue;	// whitespace: only its advance matters, and that is in PenX
		}
		FGlyphDraw D;
		D.Seq = Seq->GetSubSequence(G.SourceIndex, G.SourceIndex + G.NumCharactersInGlyph);
		D.PenIndex = FMath::Min(i, PenX.Num() - 1);
		if (D.Seq.IsValid() && D.Seq->GetGlyphsToRender().Num() > 0)
		{
			Glyphs.Add(D);
		}
	}
}

int32 SChimeraText::OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
	FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const
{
	const float Scale = AllottedGeometry.Scale;
	EnsureShaped(Scale);
	if (!bLoggedPaint)
	{
		bLoggedPaint = true;
		UE_LOG(LogChimeraHud, Display, TEXT("SChimeraText paint \"%s\" glyphs=%d draws=%d scale=%.3f origin=(%.3f,%.3f) size=(%.3f,%.3f) baselineY=%.2f ascent=%.1f width=%.4f"),
			*Str, PenX.Num() - 1, Glyphs.Num(), Scale, AllottedGeometry.GetAbsolutePosition().X, AllottedGeometry.GetAbsolutePosition().Y, AllottedGeometry.GetLocalSize().X, AllottedGeometry.GetLocalSize().Y, BaselineY, Ascent, ExactWidth);
	}
	if (Glyphs.Num() == 0)
	{
		return LayerId;
	}
	const FVector2f Origin = FVector2f(AllottedGeometry.GetAbsolutePosition());
	// Baseline on a whole pixel (Skia rounds the baseline of horizontal text the same way); glyph tops follow from it.
	const float BaseAbs = FMath::RoundToFloat(Origin.Y + BaselineY * Scale);
	const float TopLocal = (BaseAbs - Ascent - Origin.Y) / Scale;
	const FLinearColor Tint = Color * InWidgetStyle.GetColorAndOpacityTint();
	const ESlateDrawEffect Effects = ShouldBeEnabled(bParentEnabled) ? ESlateDrawEffect::None : ESlateDrawEffect::DisabledEffect;
	const FVector2f LocalSize = FVector2f(AllottedGeometry.GetLocalSize());
	for (const FGlyphDraw& D : Glyphs)
	{
		// Glyph origin at round(origin + exact pen x): an integer absolute x, so the batcher's vertex rounding keeps it (RenderingCommon.h:364-367).
		const float PenAbs = FMath::RoundToFloat(Origin.X + PenX[D.PenIndex] * Scale);
		const FVector2f Offset((PenAbs - Origin.X) / Scale, TopLocal);
		FSlateDrawElement::MakeShapedText(OutDrawElements, LayerId, AllottedGeometry.ToPaintGeometry(LocalSize, FSlateLayoutTransform(Offset)),
			D.Seq.ToSharedRef(), Effects, Tint, FLinearColor::Transparent);
	}
	return LayerId;
}

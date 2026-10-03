// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Widgets/SChimeraSubpixelBorder.h"
#include "ChimeraHud.h"
#include "Ui/ChimeraUi.h"
#include "Rendering/DrawElements.h"

void SChimeraSubpixelBorder::Construct(const FArguments& InArgs)
{
	BorderColor = InArgs._BorderColor;
	LayerOriginX = InArgs._LayerOriginX;
	SetCanTick(false);
	ChildSlot
	.Padding(FMargin(1.f) + InArgs._Padding)
	[
		InArgs._Content.Widget
	];
}

int32 SChimeraSubpixelBorder::OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
	FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const
{
	const float Scale = AllottedGeometry.Scale;
	const FVector2f Abs = FVector2f(AllottedGeometry.GetAbsolutePosition());
	const FVector2f AbsSize = FVector2f(AllottedGeometry.GetLocalSize()) * Scale;

	// The layer's fractional offset f in (-0.5, 0.5]: Blink snaps the box inside the layer, then translates it by f.
	float F = 0.f;
	if (LayerOriginX.IsValid())
	{
		F = *LayerOriginX - FMath::RoundToFloat(*LayerOriginX);
	}
	const float L = FMath::RoundToFloat(Abs.X - F) + F;
	const float R = FMath::RoundToFloat(Abs.X + AbsSize.X - F) + F;
	const int32 T = FMath::RoundToInt(Abs.Y);
	const int32 B = FMath::RoundToInt(Abs.Y + AbsSize.Y);
	if (!bLoggedPaint)
	{
		bLoggedPaint = true;
		UE_LOG(LogChimeraHud, Display, TEXT("SChimeraSubpixelBorder paint layer_f=%.3f box x=[%.3f,%.3f) y=[%d,%d)"), F, L, R, T, B);
	}

	if (R - L >= 2.f && B - T >= 2)
	{
		auto Overlap = [](float A0, float A1, float B0, float B1) { return FMath::Max(0.f, FMath::Min(A1, B1) - FMath::Max(A0, B0)); };
		// Coverage of column c by the border ring: rows T and B-1 see the whole outer span; the rows between see outer minus inner.
		auto Alpha = [&](int32 C, bool bEdgeRow)
		{
			const float Outer = Overlap((float)C, (float)C + 1.f, L, R);
			return bEdgeRow ? Outer : Outer - Overlap((float)C, (float)C + 1.f, L + 1.f, R - 1.f);
		};
		const FSlateBrush* White = ChimeraUi::Solid(0xFFFFFF);
		const FLinearColor Tint = InWidgetStyle.GetColorAndOpacityTint();
		const ESlateDrawEffect Effects = ShouldBeEnabled(bParentEnabled) ? ESlateDrawEffect::None : ESlateDrawEffect::DisabledEffect;
		auto Emit = [&](int32 X0, int32 X1, int32 Y0, int32 Y1, float A)
		{
			// Absolute pixel rectangle to local space (whole-pixel absolute corners survive the batcher's vertex rounding).
			const FVector2f Offset = (FVector2f((float)X0, (float)Y0) - Abs) / Scale;
			const FVector2f Size = FVector2f((float)(X1 - X0), (float)(Y1 - Y0)) / Scale;
			FSlateDrawElement::MakeBox(OutDrawElements, LayerId, AllottedGeometry.ToPaintGeometry(Size, FSlateLayoutTransform(Offset)), White,
				Effects, ChimeraUi::Hex(BorderColor, A) * Tint);
		};
		const int32 C0 = FMath::FloorToInt(L);
		const int32 C1 = FMath::CeilToInt(R);
		auto Rows = [&](int32 Y0, int32 Y1, bool bEdgeRow)
		{
			int32 C = C0;
			while (C < C1)
			{
				const float A = Alpha(C, bEdgeRow);
				int32 E = C + 1;
				while (E < C1 && FMath::IsNearlyEqual(Alpha(E, bEdgeRow), A, 1e-4f))
				{
					++E;
				}
				if (A > 1e-4f)
				{
					Emit(C, E, Y0, Y1, A);
				}
				C = E;
			}
		};
		Rows(T, T + 1, true);
		Rows(B - 1, B, true);
		if (B - 1 > T + 1)
		{
			Rows(T + 1, B - 1, false);
		}
	}
	return SCompoundWidget::OnPaint(Args, AllottedGeometry, MyCullingRect, OutDrawElements, LayerId + 1, InWidgetStyle, bParentEnabled);
}

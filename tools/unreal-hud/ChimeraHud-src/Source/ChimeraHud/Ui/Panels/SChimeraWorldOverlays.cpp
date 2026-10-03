// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraWorldOverlays.h"
#include "Ui/ChimeraBakes.h"
#include "Ui/ChimeraLayout.h"
#include "ChimeraHud.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"

using namespace ChimeraUi;

void SChimeraWorldOverlays::Construct(const FArguments& InArgs)
{
	const TSharedRef<SConstraintCanvas> Canvas = SNew(SConstraintCanvas);
	int32 Enabled = 1;
	FParse::Value(FCommandLine::Get(), TEXT("-HudWorldOverlays="), Enabled);
	if (Enabled != 0)
	{
		for (const FChimeraWorldRing& R : InArgs._State.Rings)
		{
			// Ring: an ellipse W x round(W x .42), centred on (X, Y) (the CSS box is translated by -50%, -50%).
			const int32 W = FMath::RoundToInt(R.Width);
			const int32 H = FMath::RoundToInt(R.Width * 0.42f);
			const float Left = R.X - (float)W * 0.5f;
			const float Top = R.Y - (float)H * 0.5f;
			const ChimeraBakes::FBake Ring = ChimeraBakes::RingSet(W, H, 3, R.Rgb, 2, Token::Void, 0.75f, Token::Void, 0.6f);
			AddBake(Canvas, Left, Top, Ring);

			// Health bar: bt = -(ring.h / 2 + tall) from the ring centre; frame 30 wide (bw = max(28, round(w x .8))), 7 tall,
			// `#0B0C0E` fill and border, an outer 1 px ring rgba(236,230,216,.25), the verdigris fill inside the 1 px border.
			const float BarW = FMath::Max(28.f, FMath::RoundToFloat(R.Width * 0.8f));
			const float BarH = 7.f;
			const float BarLeft = R.X - BarW * 0.5f;
			const float BarTop = R.Y - ((float)H * 0.5f + R.BarOffset);
			AddRect(Canvas, BarLeft - 1.f, BarTop - 1.f, BarW + 2.f, BarH + 2.f, Token::Text, 0.25f);
			AddRect(Canvas, BarLeft, BarTop, BarW, BarH, Token::Void);
			AddRect(Canvas, BarLeft + 1.f, BarTop + 1.f, FMath::RoundToFloat((BarW - 2.f) * R.Hp), BarH - 2.f, R.Rgb == 0x4FB39A ? (uint32)Token::Verdigris : R.Rgb);
		}
	}
	ChildSlot[Canvas];
}

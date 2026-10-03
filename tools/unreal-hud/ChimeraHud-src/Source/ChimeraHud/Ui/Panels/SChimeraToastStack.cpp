// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraToastStack.h"
#include "Ui/ChimeraUi.h"
#include "Ui/Widgets/SChimeraIcon.h"
#include "Ui/Widgets/SChimeraKeycap.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"

using namespace ChimeraUi;

namespace
{
	constexpr float ToastW = 400.f;
	constexpr float ToastH = 38.f;
	constexpr float PadX = 12.f;
	constexpr float PadY = 9.f;
	constexpr float Gap = 10.f;
	constexpr float HoldEndS = 11.f * 0.70f;		// hudTimer: width 100% -> 0 linearly over 70% of the 11 s cycle
}

void SChimeraToastStack::Construct(const FArguments& InArgs)
{
	const FChimeraHudState& S = InArgs._State;

	// The timer bar is the padding box wide (400 - 2 border) at t = 0 and drains to 0 at 7.7 s. t is the one HUD clock (plan B 2.8,
	// ChimeraUi::HudClockSeconds: -HudFreezeTime pins it), read at construct: the bar is laid out once (live draining is not built).
	const float T = (float)HudClockSeconds();
	const float BarFrac = FMath::Clamp(1.f - T / HoldEndS, 0.f, 1.f);
	// Blink snaps the bar's box to whole pixels: 13 + 242.92 = 255.92 rounds to 256.
	const float BarW = FMath::FloorToFloat(1.f + (ToastW - 2.f) * BarFrac + 0.5f) - 1.f;

	TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox);
	Row->AddSlot().AutoWidth().VAlign(VAlign_Center)[SNew(SChimeraIcon).Icon(S.ToastIcon).Size(16).Color(Token::Gold)];
	Row->AddSlot().FillWidth(1.f).VAlign(VAlign_Center).Padding(FMargin(Gap, 0.f, 0.f, 0.f))[Text(EChimeraText::Toast, S.ToastText)];
	Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(FMargin(Gap, 0.f, 0.f, 0.f))
		[SNew(SChimeraKeycap).Label(S.ToastKey).Fill(Token::Surface0).Radius(3.f).Height(18.f)];

	// One border line: a 1 px strip of the border colour (W or H 0 = fill that axis).
	auto Line = [](float W, float Ht) -> TSharedRef<SWidget>
	{
		return SNew(SBox).WidthOverride(W > 0.f ? FOptionalSize(W) : FOptionalSize()).HeightOverride(Ht > 0.f ? FOptionalSize(Ht) : FOptionalSize())
			[
				SNew(SImage).Image(Solid(Token::Border))
			];
	};

	ChildSlot
	[
		SNew(SBox).WidthOverride(ToastW).HeightOverride(ToastH)
		[
			SNew(SOverlay)
			+ SOverlay::Slot()[SNew(SImage).Image(Solid(Token::Surface0, 0.94f))]
			+ SOverlay::Slot().HAlign(HAlign_Fill).VAlign(VAlign_Top)[Line(0.f, 1.f)]
			+ SOverlay::Slot().HAlign(HAlign_Fill).VAlign(VAlign_Bottom)[Line(0.f, 1.f)]
			+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Fill)[Line(1.f, 0.f)]
			+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Fill)[Line(1.f, 0.f)]
			+ SOverlay::Slot().Padding(FMargin(1.f + PadX, 1.f + PadY))[Row]
			+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Bottom).Padding(FMargin(1.f, 0.f, 0.f, 1.f))
			[
				SNew(SBox).WidthOverride(BarW).HeightOverride(2.f)[SNew(SImage).Image(Solid(Token::Gold))]
			]
		]
	];
}

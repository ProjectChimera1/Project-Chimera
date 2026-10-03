// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraTopStrip.h"
#include "Ui/ChimeraUi.h"
#include "Ui/Widgets/SChimeraIcon.h"
#include "Ui/Widgets/SChimeraKeycap.h"
#include "Ui/Widgets/SChimeraSubpixelBorder.h"
#include "Ui/Widgets/SChimeraTranslateLayer.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"

using namespace ChimeraUi;

namespace
{
	constexpr float StripH = 40.f;		// border box; the content box is 39 (1 px bottom border)
	constexpr float ChipH = 28.f;
	constexpr float RowGap = 4.f;		// flex gap of the strip
	constexpr float ChipGap = 7.f;		// flex gap inside chips and buttons
	constexpr float ChipPadX = 10.f;
	constexpr float ClockGap = 10.f;	// flex gap of the clock group
}

TSharedRef<SWidget> SChimeraTopStrip::Chip(uint32 Fill, uint32 BorderColor, const TArray<TSharedRef<SWidget>>& Children)
{
	TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox);
	for (int32 i = 0; i < Children.Num(); ++i)
	{
		Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(FMargin(i == 0 ? 0.f : ChipGap, 0.f, 0.f, 0.f))[Children[i]];
	}
	return SNew(SBox).HeightOverride(ChipH)
		[
			SNew(SBorder).BorderImage(Solid(BorderColor)).Padding(1.f)
			[
				SNew(SBorder).BorderImage(Solid(Fill)).Padding(FMargin(ChipPadX, 0.f)).VAlign(VAlign_Center)
				[
					Row
				]
			]
		];
}

void SChimeraTopStrip::Construct(const FArguments& InArgs)
{
	const FChimeraHudState& S = InArgs._State;

	// Flex row: chips, spacer, alert log, menu (every item separated by the 4 px gap, the spacer included).
	TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox);
	bool bFirst = true;
	auto Add = [&Row, &bFirst](const TSharedRef<SWidget>& W, bool bFill = false)
	{
		const FMargin Pad(bFirst ? 0.f : RowGap, 0.f, 0.f, 0.f);
		if (bFill)
		{
			Row->AddSlot().FillWidth(1.f).VAlign(VAlign_Center).Padding(Pad)[W];
		}
		else
		{
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(Pad)[W];
		}
		bFirst = false;
	};

	for (const FChimeraResourceChip& C : S.Chips)
	{
		TArray<TSharedRef<SWidget>> Kids;
		Kids.Add(SNew(SChimeraIcon).Icon(C.Icon).Size(16).Color(Token::Gold));
		Kids.Add(Text(EChimeraText::ResourceValue, C.Value));
		// The supply chip's rate node is empty but still takes its 7 px gap (r4 5.1: 106 = 1+10+16+7+54+7+0+10+1).
		Kids.Add(C.Rate.IsEmpty() ? StaticCastSharedRef<SWidget>(SNew(SBox).WidthOverride(0.f).HeightOverride(0.f))
			: Text(EChimeraText::ResourceRate, C.Rate));
		Add(Chip(Token::Surface0, Token::Border, Kids));
	}
	Add(SNew(SBox), true);	// spacer
	{
		const uint32 AlertColor = S.bAlertUrgent ? 0xE2735F : Token::TextMuted;
		TArray<TSharedRef<SWidget>> Kids;
		Kids.Add(SNew(SChimeraIcon).Icon(TEXT("alert")).Size(16).Color(S.bAlertUrgent ? 0xE2735F : Token::TextMuted));
		Kids.Add(Text(EChimeraText::AlertCount, S.AlertCount, AlertColor));
		Add(Chip(Token::Surface0, S.bAlertUrgent ? 0xC8503C : Token::Border, Kids));
	}
	{
		TArray<TSharedRef<SWidget>> Kids;
		Kids.Add(SNew(SChimeraIcon).Icon(TEXT("grid")).Size(16).Color(Token::Gold));
		Kids.Add(Text(EChimeraText::Label12, TEXT("Menu"), Token::Text));
		Kids.Add(SNew(SChimeraKeycap).Label(TEXT("F10")).Fill(Token::Surface2).Radius(3.f).Height(18.f));
		Add(Chip(Token::Surface0, Token::Border, Kids));
	}

	// Clock group: the match clock and the speed pill (1 px border, padding 5/8, transparent over the strip). CSS centres the group
	// with left:50%; transform:translateX(-50%), so it sits at a fractional x (886.53) and Blink anti-aliases the pill's side
	// borders across two columns; SChimeraTranslateLayer + SChimeraSubpixelBorder reproduce that coverage from the live layout.
	const TSharedRef<float> ClockOriginX = MakeShared<float>(0.f);
	TSharedRef<SWidget> Clock = SNew(SChimeraTranslateLayer).OriginX(ClockOriginX)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				Text(EChimeraText::Clock, S.Clock)
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(FMargin(ClockGap, 0.f, 0.f, 0.f))
			[
				SNew(SChimeraSubpixelBorder).BorderColor(Token::Border).Padding(FMargin(8.f, 5.f)).LayerOriginX(ClockOriginX)
				[
					Text(EChimeraText::Label12, S.Speed, Token::TextMuted)
				]
			]
		];

	ChildSlot
	[
		SNew(SBox).HeightOverride(StripH)
		[
			SNew(SOverlay)
			+ SOverlay::Slot()
			[
				SNew(SBorder).BorderImage(Solid(Token::Border)).Padding(FMargin(0.f, 0.f, 0.f, 1.f))
				[
					SNew(SBorder).BorderImage(Solid(Token::Surface1)).Padding(FMargin(8.f, 0.f))
					[
						Row
					]
				]
			]
			+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Fill)
			[
				Clock
			]
		]
	];
}

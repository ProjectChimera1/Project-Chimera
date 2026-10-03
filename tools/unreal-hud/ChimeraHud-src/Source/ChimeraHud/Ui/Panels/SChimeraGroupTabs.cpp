// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraGroupTabs.h"
#include "Ui/ChimeraUi.h"
#include "Ui/Widgets/SChimeraIcon.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"

using namespace ChimeraUi;

void SChimeraGroupTabs::Construct(const FArguments& InArgs)
{
	const FChimeraHudState& S = InArgs._State;
	constexpr float TabH = 30.f;
	constexpr float PadX = 9.f;
	constexpr float Gap = 6.f;

	// Content box: 29 px (the 1 px top border), items centred in it as the flex row does.
	ChildSlot
	[
		SNew(SBox).HeightOverride(TabH)
		[
			SNew(SBorder).BorderImage(Solid(Token::GoldBright)).Padding(FMargin(1.f, 1.f, 1.f, 0.f))
			[
				SNew(SBorder).BorderImage(Solid(Token::Surface4)).Padding(FMargin(PadX, 0.f)).VAlign(VAlign_Center)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Text(EChimeraText::TabNumber, S.TabNumber)]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(FMargin(Gap, 0.f, 0.f, 0.f))
						[SNew(SChimeraIcon).Icon(S.TabIcon).Size(14).Color(Token::GoldBright)]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(FMargin(Gap, 0.f, 0.f, 0.f))[Text(EChimeraText::TabCount, S.TabCount)]
				]
			]
		]
	];
}

// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Widgets/SChimeraKeycap.h"
#include "Ui/ChimeraUi.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"

void SChimeraKeycap::Construct(const FArguments& InArgs)
{
	using namespace ChimeraUi;

	// Content box: height minus the 1 px top and 2 px bottom border; the 14 px line box sits at its top (block layout, r4 5.1).
	const float ContentH = FMath::Max(InArgs._Height - 3.f, 0.f);
	const float InnerMinW = FMath::Max(InArgs._MinWidth - 2.f, 0.f);

	ChildSlot
	[
		SNew(SBox).HeightOverride(InArgs._Height).MinDesiredWidth(InArgs._MinWidth > 0.f ? FOptionalSize(InArgs._MinWidth) : FOptionalSize())
		[
			// One 9-slice face (border box + padding box, Skia corner coverage); the content sits in the padding box.
			SNew(SBorder).BorderImage(KeycapBrush(InArgs._Radius, InArgs._BorderColor.Get(Token::KeycapBorder), InArgs._Fill, InArgs._Backdrop))
			.Padding(FMargin(1.f, 1.f, 1.f, 2.f))
			[
				SNew(SBox).Padding(FMargin(4.f, 0.f, 4.f, 0.f))
				.HAlign(HAlign_Center).VAlign(VAlign_Top)
				[
					SNew(SBox).HeightOverride(ContentH).MinDesiredWidth(InnerMinW > 8.f ? FOptionalSize(InnerMinW - 8.f) : FOptionalSize()).HAlign(HAlign_Center).VAlign(VAlign_Top)
					[
						Text(EChimeraText::Keycap, InArgs._Label, InArgs._LabelColor)
					]
				]
			]
		]
	];
}

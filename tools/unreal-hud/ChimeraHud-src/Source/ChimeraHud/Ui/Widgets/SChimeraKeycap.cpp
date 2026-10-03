// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Widgets/SChimeraKeycap.h"
#include "Ui/ChimeraUi.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"

void SChimeraKeycap::Construct(const FArguments& InArgs)
{
	using namespace ChimeraUi;
	const float R = InArgs._Radius;
	const float Top = FMath::Max(R - 1.f, 0.f);
	const float Bottom = FMath::Max(((R - 1.f) + (R - 2.f)) * 0.5f, 0.f);
	const FVector4f OuterRadii(R, R, R, R);
	const FVector4f InnerRadii(Top, Top, Bottom, Bottom);	// (TL, TR, BR, BL)

	// Content box: height minus the 1 px top and 2 px bottom border; the 14 px line box sits at its top (block layout, r4 5.1).
	const float ContentH = FMath::Max(InArgs._Height - 3.f, 0.f);
	const float InnerMinW = FMath::Max(InArgs._MinWidth - 2.f, 0.f);

	ChildSlot
	[
		SNew(SBox).HeightOverride(InArgs._Height).MinDesiredWidth(InArgs._MinWidth > 0.f ? FOptionalSize(InArgs._MinWidth) : FOptionalSize())
		[
			SNew(SBorder).BorderImage(KeycapBrush(InArgs._BorderColor.Get(Token::KeycapBorder), OuterRadii)).Padding(FMargin(1.f, 1.f, 1.f, 2.f))
			[
				SNew(SBorder).BorderImage(KeycapBrush(InArgs._Fill, InnerRadii)).Padding(FMargin(4.f, 0.f, 4.f, 0.f))
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

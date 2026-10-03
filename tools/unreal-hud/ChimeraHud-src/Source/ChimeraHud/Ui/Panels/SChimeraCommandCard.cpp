// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraCommandCard.h"
#include "Ui/ChimeraBakes.h"
#include "Ui/ChimeraLayout.h"
#include "Ui/Widgets/SChimeraIcon.h"
#include "Ui/Widgets/SChimeraKeycap.h"
#include "Widgets/Layout/SBox.h"

using namespace ChimeraUi;

namespace
{
	constexpr float PanelW = 300.f;
	constexpr float PanelH = 216.f;
	constexpr float GridX = 13.f, GridY = 13.f;		// padding 12 + the 1 px left / top border
	constexpr float SlotW = 64.f, SlotH = 60.f;
	constexpr float StepX = 70.f, StepY = 66.f;		// 64 + gap 6, 60 + gap 6

	/** CSS `opacity .55` of a whole opaque button over the panel: c' = .55 c + .45 panel, per sRGB channel (r4 5.7: #14161A -> #181A1F). */
	uint32 OverPanel(uint32 Rgb)
	{
		const uint32 Panel = Token::Surface1;
		uint32 Out = 0;
		for (int32 Shift = 16; Shift >= 0; Shift -= 8)
		{
			const float C = (float)((Rgb >> Shift) & 0xFF), P = (float)((Panel >> Shift) & 0xFF);
			Out |= (uint32)FMath::Clamp(FMath::FloorToInt(0.55f * C + 0.45f * P + 0.5f), 0, 255) << Shift;
		}
		return Out;
	}

	/** One slot, 64x60 in its own absolute canvas. */
	TSharedRef<SWidget> MakeSlot(const FChimeraCommandSlot& Slot)
	{
		const bool bLocked = Slot.State == EChimeraSlotState::Locked;
		const bool bActive = Slot.State == EChimeraSlotState::Active;
		const bool bEmpty = Slot.State == EChimeraSlotState::Empty;
		// r4 5.7 state table; the locked column is the pre-composited one.
		auto Tone = [bLocked](uint32 Rgb) { return bLocked ? OverPanel(Rgb) : Rgb; };
		const uint32 BorderRgb = bActive ? Token::Verdigris : bLocked ? Token::Border : Token::BorderWarm;
		const uint32 FillRgb = bActive ? Token::SuccessFill : Token::Surface0;
		const uint32 IconRgb = bActive ? Token::Verdigris : Token::Gold;

		const TSharedRef<SConstraintCanvas> C = SNew(SConstraintCanvas);
		if (bEmpty)
		{
			AddBake(C, 0.f, 0.f, ChimeraBakes::DashedBorder((int32)SlotW, (int32)SlotH, Token::Surface4));
		}
		else
		{
			AddRect(C, 0.f, 0.f, SlotW, SlotH, Tone(BorderRgb));
			AddRect(C, 1.f, 1.f, SlotW - 2.f, SlotH - 2.f, Tone(FillRgb));
		}
		AddAbs(C, 4.f, 4.f, 16.f, 16.f,
			SNew(SChimeraKeycap).Label(Slot.Key).Fill(Tone(Token::Void)).Radius(2.f).Height(16.f).MinWidth(16.f)
				.BorderColor(Tone(Token::KeycapBorder)).LabelColor(Tone(Token::Text)));
		if (!Slot.Icon.IsEmpty())
		{
			// Flex column, centred, gap 4: icon 24 + (4 + cost line 9) when there is a cost: 10.5 + 1 = 11.5 or 17 + 1 = 18.
			const float IconY = Slot.Cost.IsEmpty() ? 18.f : 11.5f;
			AddAbs(C, 20.f, IconY, 24.f, 24.f, SNew(SChimeraIcon).Icon(Slot.Icon).Size(24).Color(Tone(IconRgb)));
		}
		if (!Slot.Cost.IsEmpty())
		{
			AddAbs(C, 1.f, 39.5f, 62.f, 9.f,
				SNew(SBox).WidthOverride(62.f).HeightOverride(9.f).HAlign(HAlign_Center)
				[
					Text(EChimeraText::CostLabel, Slot.Cost, Tone(Token::TextMuted))
				]);
		}
		if (bLocked)
		{
			AddAbs(C, 47.f, 5.f, 12.f, 12.f, SNew(SChimeraIcon).Icon(TEXT("lock")).Size(12).Color(Tone(Token::TextMuted)));
		}
		return SNew(SBox).WidthOverride(SlotW).HeightOverride(SlotH)[C];
	}
}

void SChimeraCommandCard::Construct(const FArguments& InArgs)
{
	const FChimeraHudState& S = InArgs._State;
	const TSharedRef<SConstraintCanvas> C = SNew(SConstraintCanvas);

	AddRect(C, 0.f, 0.f, PanelW, PanelH, Token::Surface1);
	AddRect(C, 0.f, 0.f, PanelW, 1.f, Token::GoldDark);
	AddRect(C, 0.f, 1.f, 1.f, PanelH - 1.f, Token::Border);

	for (int32 I = 0; I < S.Slots.Num() && I < 12; ++I)
	{
		AddAbs(C, GridX + (float)(I % 4) * StepX, GridY + (float)(I / 4) * StepY, SlotW, SlotH, MakeSlot(S.Slots[I]));
	}

	ChildSlot[SNew(SBox).WidthOverride(PanelW).HeightOverride(PanelH)[C]];
}

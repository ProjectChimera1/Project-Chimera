// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraSelectionPanel.h"
#include "Ui/ChimeraBakes.h"
#include "Ui/Panels/SChimeraOrnamentFrame.h"
#include "Ui/ChimeraLayout.h"
#include "ChimeraHud.h"
#include "Widgets/Layout/SBox.h"

using namespace ChimeraUi;

namespace
{
	// Panel-local numbers: the panel is at screen (256, 904); r4 5.6 positions minus that origin.
	constexpr float PanelW = 1364.f;
	constexpr float PanelH = 176.f;
	constexpr float VatX = 274.f - 256.f, VatY = 919.f - 904.f, VatW = 124.f, VatH = 147.f;
	constexpr float InfoX = 418.f - 256.f, InfoW = 340.f;
	// Line boxes (r4 5.6 table): name 22 tall at 943.91, role 15.6 at 972.91, HP text 12 at 995.5, stat line 15.6 at 1025.5.
	constexpr float NameY = 943.91f - 904.f;
	constexpr float RoleY = 972.91f - 904.f;
	constexpr float HpTextY = 995.5f - 904.f;
	constexpr float StatY = 1025.5f - 904.f;
	constexpr float BarY = 1011.5f - 904.f;		// the 340x7 frame; Blink snaps 1011.5 to 1012 and 1018.5 to 1019

	float Snap(float V) { return FMath::FloorToFloat(V + 0.5f); }
}

void SChimeraSelectionPanel::Construct(const FArguments& InArgs)
{
	const FChimeraHudState& S = InArgs._State;
	const TSharedRef<SConstraintCanvas> C = SNew(SConstraintCanvas);

	// CSS left:256px; right:300px: the fill, the top border and the ornament frame stretch with the screen (1364 px at 1920 wide);
	// the vat and the info column stay at their left positions (flex row from the left).
	AddStretchX(C, 0.f, 0.f, 0.f, PanelH, SNew(SImage).Image(Solid(Token::Surface1)));
	AddStretchX(C, 0.f, 0.f, 0.f, 1.f, SNew(SImage).Image(Solid(Token::GoldDark)));

	// Portrait vat: 1 px border, #14161A fill, radial-gradient(circle at 50% 70%, #2E4A44 0, #1A1D22 62%) over the 122x145 padding box.
	AddRect(C, VatX, VatY, VatW, VatH, Token::GoldDark);
	AddRect(C, VatX + 1.f, VatY + 1.f, VatW - 2.f, VatH - 2.f, Token::Surface0);
	AddBake(C, VatX + 1.f, VatY + 1.f, ChimeraBakes::RadialGradient(122, 145, 50.f, 70.f, { { 0.f, 0x2E4A44, 1.f }, { 0.62f, 0x1A1D22, 1.f } }));
	{
		// The 48x78 figure placeholder (the stand-in for the future 3D render target): bottom-aligned, 12 px above the vat's padding box.
		const TSharedPtr<FSlateBrush> Figure = LoadPngBrush(HudDataPath(TEXT("Placeholders/portrait_figure.png")), TEXT("ChimeraHudPortraitFigure"));
		if (Figure.IsValid())
		{
			new TSharedPtr<FSlateBrush>(Figure);	// lives as long as the process, like the cached bakes
			AddAbs(C, 312.f - 256.f, 975.f - 904.f, 48.f, 78.f, SNew(SImage).Image(Figure.Get()));
		}
		else
		{
			UE_LOG(LogChimeraHud, Error, TEXT("portrait figure did not load"));
		}
	}

	// Info column.
	AddAbs(C, InfoX, NameY, InfoW, 22.f, Text(EChimeraText::UnitName, S.UnitName));
	AddAbs(C, InfoX, RoleY, InfoW, 15.6f, Text(EChimeraText::UnitRole, S.UnitRole));
	AddAbs(C, InfoX, HpTextY, InfoW, 12.f, Text(EChimeraText::HpText, S.UnitHp));
	{
		const float Y = Snap(BarY);
		AddRect(C, InfoX, Y, InfoW, 7.f, Token::Border);
		AddRect(C, InfoX + 1.f, Y + 1.f, InfoW - 2.f, 5.f, Token::Void);
		const float FillW = Snap((InfoW - 2.f) * FMath::Clamp(S.UnitHpFraction, 0.f, 1.f));
		if (FillW > 0.f)
		{
			AddRect(C, InfoX + 1.f, Y + 1.f, FillW, 5.f, Token::Verdigris);
		}
	}
	// One line, overflow hidden: the 331.2 px stat line fits the 340 px column, so the ellipsis never triggers on this board.
	AddAbs(C, InfoX, StatY, InfoW, 15.6f, Text(EChimeraText::StatLine, S.UnitStats));

	// Ornament frame (z-index 3): the full 1364 px top inner edge, built with noSigils, laser delay 1.5 s.
	{
		const float M = SChimeraOrnamentFrame::Margin;
		AddStretchX(C, -M, 1.f - M, -M, M + 8.f,
			SNew(SChimeraOrnamentFrame).FrameWidth(PanelW).Stretch(true).Sigils(false).LaserBaseDelay(1.5f));
	}

	ChildSlot[SNew(SBox).HeightOverride(PanelH)[C]];
}

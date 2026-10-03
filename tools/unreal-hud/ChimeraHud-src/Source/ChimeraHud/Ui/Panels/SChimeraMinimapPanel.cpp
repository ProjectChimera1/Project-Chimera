// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/Panels/SChimeraMinimapPanel.h"
#include "Ui/ChimeraBakes.h"
#include "Ui/Panels/SChimeraOrnamentFrame.h"
#include "Ui/ChimeraLayout.h"
#include "Ui/Widgets/SChimeraIcon.h"
#include "ChimeraHud.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"

using namespace ChimeraUi;

namespace
{
	// The panel is at screen (0, 864); every number below is panel-local (screen y - 864) from r4 5.4.
	constexpr float PanelW = 256.f;
	constexpr float PanelH = 216.f;
	constexpr float ContentTop = 1.f;			// the 1 px top border
	constexpr float PlateX = 44.f, PlateY = ContentTop + 8.f, PlateSize = 200.f;
	constexpr float MapX = 52.f, MapY = ContentTop + 16.f, MapSize = 184.f;
	constexpr float MapInX = MapX + 1.f, MapInY = MapY + 1.f, MapIn = 182.f;	// padding box inside the 1 px border

	/** CSS rounds a layout position to the pixel grid: half up. */
	float Snap(float V) { return FMath::FloorToFloat(V + 0.5f); }

	/** A 28x28 map button: 1 px #3A3F48 border, #14161A fill, the 16 px gold icon centred. */
	TSharedRef<SWidget> MapButton(const TCHAR* Icon)
	{
		return SNew(SBorder).BorderImage(Solid(Token::Border)).Padding(1.f)
			[
				SNew(SBorder).BorderImage(Solid(Token::Surface0)).HAlign(HAlign_Center).VAlign(VAlign_Center).Padding(0.f)
				[
					SNew(SChimeraIcon).Icon(Icon).Size(16).Color(Token::Gold)
				]
			];
	}
}

void SChimeraMinimapPanel::Construct(const FArguments& InArgs)
{
	const FChimeraHudState& S = InArgs._State;
	const TSharedRef<SConstraintCanvas> C = SNew(SConstraintCanvas);

	// Panel: fill, top border (gold dark), right border.
	AddRect(C, 0.f, 0.f, PanelW, PanelH, Token::Surface1);
	AddRect(C, 0.f, 0.f, PanelW, 1.f, Token::GoldDark);
	AddRect(C, PanelW - 1.f, 1.f, 1.f, PanelH - 1.f, Token::Border);

	// Plate (paint order 21-26): drop shadow, border + fill, bevels, inner line, nodes.
	// Every shadow pixel with any alpha lies on the panel fill (the buttons, borders and ropes are beyond its reach), so the bake
	// is pre-composited over #1C1F25 (ChimeraBakes::OuterShadow).
	AddBake(C, PlateX, PlateY, ChimeraBakes::OuterShadow(200, 200, 0, 3, 6.f, 0x000000, 0.4f, Token::Surface1));
	AddAbs(C, PlateX, PlateY, PlateSize, PlateSize,
		SNew(SBorder).BorderImage(Solid(Token::GoldDark)).Padding(1.f)
		[
			SNew(SImage).Image(Solid(Token::Surface3))
		]);
	{
		// inset -1 -1 0 rgba(11,12,14,.5) (bottom row, right column) is listed after inset 1 1 0 rgba(227,200,135,.25): the top-left
		// highlight paints over the dark bevel where they meet, so the dark lines go down first.
		const float Px = PlateX + 1.f, Py = PlateY + 1.f, Pw = PlateSize - 2.f;
		AddRect(C, Px, Py + Pw - 1.f, Pw, 1.f, Token::Void, 0.5f);
		AddRect(C, Px + Pw - 1.f, Py, 1.f, Pw, Token::Void, 0.5f);
		AddRect(C, Px, Py, Pw, 1.f, Token::GoldBright, 0.25f);
		AddRect(C, Px, Py, 1.f, Pw, Token::GoldBright, 0.25f);
	}
	AddHollowRect(C, PlateX + 4.f, PlateY + 4.f, 192.f, 192.f, Token::BorderWarm);
	{
		const ChimeraBakes::FBake Node = ChimeraBakes::RingedNode(6, Token::Surface0, Token::Gold);
		const float L = PlateX - 2.f, R = PlateX + PlateSize - 4.f, T = PlateY - 2.f, B = PlateY + PlateSize - 4.f;
		AddBake(C, L, T, Node);
		AddBake(C, R, T, Node);
		AddBake(C, L, B, Node);
		AddBake(C, R, B, Node);
	}

	// Map surface (paint order 27-42): border, photo, inset shadow, dim, fog, dots, camera rectangle.
	AddRect(C, MapX, MapY, MapSize, MapSize, Token::Void);
	{
		const TSharedPtr<FSlateBrush> Photo = LoadPngBrush(HudDataPath(TEXT("Placeholders/minimap_photo.png")), TEXT("ChimeraHudMinimapPhoto"));
		if (Photo.IsValid())
		{
			new TSharedPtr<FSlateBrush>(Photo);	// the brush lives as long as the process, like the cached bakes
			AddAbs(C, MapInX, MapInY, MapIn, MapIn, SNew(SImage).Image(Photo.Get()));
		}
		else
		{
			UE_LOG(LogChimeraHud, Error, TEXT("minimap photo did not load"));
		}
	}
	AddBake(C, MapInX, MapInY, ChimeraBakes::InsetShadow(182, 182, 0, 2, 6.f, 0x000000, 0.6f));
	AddRect(C, MapInX, MapInY, MapIn, MapIn, Token::Surface0, 0.35f);
	AddBake(C, MapInX, MapInY, ChimeraBakes::RadialGradient(182, 182, 30.f, 68.f,
		{ { 0.30f, 0x000000, 0.f }, { 0.52f, 0x08090B, 0.66f } }));
	for (const FChimeraMapDot& D : S.MapDots)
	{
		// left/top are percent of the 182 px padding box; translate(-50%, -50%) of the border box. Blink snaps the layout position
		// (89.4 -> 89) and the transform adds -Size/2, so an odd size lands on x.5 (see DotSquare: its image starts at floor).
		const float Cx = Snap(MapInX + D.PctX * 0.01f * MapIn);
		const float Cy = Snap(MapInY + D.PctY * 0.01f * MapIn);
		const ChimeraBakes::FBake Dot = ChimeraBakes::DotSquare(D.Size, D.Rgb, Token::Void);
		const float Half = (D.Size & 1) ? (float)(D.Size / 2) + 1.f : (float)D.Size * 0.5f;
		AddBake(C, Cx - Half, Cy - Half, Dot);
	}
	{
		// camL = (cam.x * 184 - 27).toFixed(0), camT = (cam.y * 184 - 17).toFixed(0), relative to the padding box.
		const float CamL = Snap(S.CamX * 184.f - 27.f), CamT = Snap(S.CamY * 184.f - 17.f);
		AddHollowRect(C, MapInX + CamL, MapInY + CamT, 54.f, 34.f, Token::Text);
	}

	// Buttons (paint order 44-52, after the map).
	AddAbs(C, 8.f, ContentTop + 16.f, 28.f, 28.f, MapButton(TEXT("eye")));
	AddAbs(C, 8.f, ContentTop + 48.f, 28.f, 28.f, MapButton(TEXT("flag")));
	AddAbs(C, 8.f, ContentTop + 80.f, 28.f, 28.f, MapButton(TEXT("grid")));

	// Ornament frame (z-index 3, over everything above): container on the top inner edge, 255 wide (inside the right border).
	{
		const float M = SChimeraOrnamentFrame::Margin;
		AddAbs(C, -M, ContentTop - M, PanelW - 1.f + 2.f * M, M + 8.f,
			SNew(SChimeraOrnamentFrame).FrameWidth(PanelW - 1.f).Sigils(true).LaserBaseDelay(0.f));
	}

	ChildSlot[SNew(SBox).WidthOverride(PanelW).HeightOverride(PanelH)[C]];
}

// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/SChimeraMatchHud.h"
#include "Ui/ChimeraUi.h"
#include "ChimeraHud.h"
#include "Brushes/SlateColorBrush.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/SCanvas.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Layout/SConstraintCanvas.h"
#include "Ui/ChimeraHudState.h"
#include "Ui/Panels/SChimeraCommandCard.h"
#include "Ui/Panels/SChimeraGroupTabs.h"
#include "Ui/Panels/SChimeraMinimapPanel.h"
#include "Ui/Panels/SChimeraSelectionPanel.h"
#include "Ui/Panels/SChimeraToastStack.h"
#include "Ui/Panels/SChimeraTopStrip.h"
#include "Ui/Panels/SChimeraWorldOverlays.h"

void SChimeraMatchHud::Construct(const FArguments& InArgs)
{
	TSharedRef<SOverlay> Overlay = SNew(SOverlay);

	// Layer 0: the backdrop, drawn 1:1 under the HUD.
	const FString& Backdrop = InArgs._Backdrop;
	uint32 Rgb = 0;
	if (Backdrop.Equals(TEXT("none"), ESearchCase::IgnoreCase))
	{
		// live 3D world shows through
	}
	else if (ChimeraUi::ParseHex(Backdrop, Rgb))
	{
		TSharedPtr<FSlateBrush> Flat = MakeShared<FSlateColorBrush>(ChimeraUi::Hex(Rgb));
		Brushes.Add(Flat);
		Overlay->AddSlot()[SNew(SImage).Image(Flat.Get())];
	}
	else
	{
		TSharedPtr<FSlateBrush> Photo = ChimeraUi::LoadPngBrush(Backdrop, FName(*FString::Printf(TEXT("ChimeraHudBackdrop_%s"), *FPaths::GetBaseFilename(Backdrop))));
		if (Photo.IsValid())
		{
			Brushes.Add(Photo);
			Overlay->AddSlot()[SNew(SImage).Image(Photo.Get())];
		}
		else
		{
			UE_LOG(LogChimeraHud, Error, TEXT("backdrop %s did not load"), *Backdrop);
		}
	}

	// Blend test pattern: 50% #ECE6D8, 25% #0B0C0E, 25% #ECE6D8, 50% #0B0C0E at the positions hud_compare.py --blend expects.
	if (InArgs._TestPattern.Equals(TEXT("blend"), ESearchCase::IgnoreCase))
	{
		struct FBox { float X; uint32 Rgb; float A; };
		static const FBox Boxes[] = { {100, 0xECE6D8, 0.50f}, {400, 0x0B0C0E, 0.25f}, {700, 0xECE6D8, 0.25f}, {1000, 0x0B0C0E, 0.50f} };
		TSharedRef<SCanvas> Canvas = SNew(SCanvas);
		for (const FBox& B : Boxes)
		{
			TSharedPtr<FSlateBrush> Brush = MakeShared<FSlateColorBrush>(ChimeraUi::Hex(B.Rgb, B.A));
			Brushes.Add(Brush);
			Canvas->AddSlot().Position(FVector2D(B.X, 100)).Size(FVector2D(200, 200))[SNew(SImage).Image(Brush.Get())];
		}
		Overlay->AddSlot()[Canvas];
	}

	// HUD panels (plan B 2.6), screen-anchored on a constraint canvas; T4a adds the top strip (first cut).
	// -HudPanels=0 (and any test pattern) draws the backdrop alone, so T3's 1:1 pipeline proof stays reproducible.
	int32 Panels = 1;
	FParse::Value(FCommandLine::Get(), TEXT("-HudPanels="), Panels);
	if (Panels == 0 || !InArgs._TestPattern.IsEmpty())
	{
		UE_LOG(LogChimeraHud, Display, TEXT("panels off"));
		ChildSlot[Overlay];
		return;
	}
	ChimeraUi::LogRouteAndKernCheck();
	const FChimeraHudState State = MakeBoard31aState();
	// Z order bottom to top (r4 5.8): world overlays, toast, minimap panel, tab row, selection panel, command card, top strip.
	TSharedRef<SConstraintCanvas> Canvas = SNew(SConstraintCanvas);
	Canvas->AddSlot()
		.Anchors(FAnchors(0.f, 0.f, 1.f, 1.f))		// the world overlays fill the screen
		.Offset(FMargin(0.f))
		[
			SNew(SChimeraWorldOverlays).State(State)
		];
	Canvas->AddSlot()
		.Anchors(FAnchors(0.f, 1.f, 0.f, 1.f))		// toast: left 12, bottom 228
		.Offset(FMargin(12.f, -228.f, 400.f, 38.f))
		.Alignment(FVector2D(0.f, 1.f))
		[
			SNew(SChimeraToastStack).State(State)
		];
	Canvas->AddSlot()
		.Anchors(FAnchors(0.f, 1.f, 0.f, 1.f))		// minimap panel: bottom-left, 256x216
		.Offset(FMargin(0.f, 0.f, 256.f, 216.f))
		.Alignment(FVector2D(0.f, 1.f))
		[
			SNew(SChimeraMinimapPanel).State(State)
		];
	Canvas->AddSlot()
		.Anchors(FAnchors(0.f, 1.f, 0.f, 1.f))		// tab row: x 268, resting on the selection panel's top edge (176 above the bottom)
		.Offset(FMargin(268.f, -176.f, 0.f, 0.f))
		.Alignment(FVector2D(0.f, 1.f))
		.AutoSize(true)
		[
			SNew(SChimeraGroupTabs).State(State)
		];
	Canvas->AddSlot()
		.Anchors(FAnchors(0.f, 1.f, 0.f, 1.f))		// selection panel: (256, 904), 1364x176
		.Offset(FMargin(256.f, 0.f, 1364.f, 176.f))
		.Alignment(FVector2D(0.f, 1.f))
		[
			SNew(SChimeraSelectionPanel).State(State)
		];
	Canvas->AddSlot()
		.Anchors(FAnchors(1.f, 1.f, 1.f, 1.f))		// command card: bottom-right, 300x216
		.Offset(FMargin(0.f, 0.f, 300.f, 216.f))
		.Alignment(FVector2D(1.f, 1.f))
		[
			SNew(SChimeraCommandCard).State(State)
		];
	Canvas->AddSlot()
		.Anchors(FAnchors(0.f, 0.f, 1.f, 0.f))		// stretch in X, point in Y
		.Offset(FMargin(0.f, 0.f, 0.f, 40.f))		// left, top, right margin, height
		.Alignment(FVector2D(0.f, 0.f))
		[
			SNew(SChimeraTopStrip).State(State)
		];
	Overlay->AddSlot()[Canvas];

	ChildSlot[Overlay];
}

int32 SChimeraMatchHud::OnPaint(const FPaintArgs& Args, const FGeometry& AllottedGeometry, const FSlateRect& MyCullingRect,
	FSlateWindowElementList& OutDrawElements, int32 LayerId, const FWidgetStyle& InWidgetStyle, bool bParentEnabled) const
{
	if (!bLoggedGeometry)
	{
		bLoggedGeometry = true;
		const FVector2f Local = AllottedGeometry.GetLocalSize();
		FIntPoint Viewport(0, 0);
		if (GEngine && GEngine->GameViewport && GEngine->GameViewport->Viewport)
		{
			Viewport = GEngine->GameViewport->Viewport->GetSizeXY();
		}
		UE_LOG(LogChimeraHud, Display, TEXT("geometry scale=%.3f local=%dx%d viewport=%dx%d"),
			AllottedGeometry.Scale, FMath::RoundToInt(Local.X), FMath::RoundToInt(Local.Y), Viewport.X, Viewport.Y);
	}
	return SCompoundWidget::OnPaint(Args, AllottedGeometry, MyCullingRect, OutDrawElements, LayerId, InWidgetStyle, bParentEnabled);
}

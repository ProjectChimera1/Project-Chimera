// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Ui/ChimeraHudState.h"

/**
 * The selection panel of board 3.1a (r4 5.6, ids 231-282), 1364x176 at (256, 904): fill #1C1F25 with a 1 px #8A6E3C top border.
 * Left the 124x147 portrait vat (1 px #8A6E3C border, radial-gradient bake over #14161A, the 48x78 figure placeholder); right of it
 * the 340 px info column: name (Cinzel), role, "220 / 220" with the 7 px HP bar, and the stat line. Every element is placed at
 * the mockup's layout position (flex column, gap 7, centred; r4 5.6 line boxes), with Blink's pixel snapping applied to the bar
 * boxes. The ornament frame (SChimeraOrnamentFrame: rope, no sigils on this panel, laser) is the last child.
 */
class SChimeraSelectionPanel : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraSelectionPanel) {}
		SLATE_ARGUMENT(FChimeraHudState, State)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

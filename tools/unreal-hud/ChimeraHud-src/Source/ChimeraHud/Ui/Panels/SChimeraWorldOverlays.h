// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Ui/ChimeraHudState.h"

/**
 * World overlays of board 3.1a (r4 5.2, ids 4-7): the selected unit's ring (a baked ellipse set: 3 px border, 2 px outer shadow
 * ring, 1 px inset hairline) and its 30x7 health bar. Screen space for this check only; `-HudWorldOverlays=0` hides them.
 * Fills the screen; the bottom layer of the root canvas.
 */
class SChimeraWorldOverlays : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraWorldOverlays) {}
		SLATE_ARGUMENT(FChimeraHudState, State)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

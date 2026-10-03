// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Ui/ChimeraHudState.h"

/**
 * The minimap panel of board 3.1a (r4 5.4, ids 81-218), 256x216 at the screen's bottom-left: fill #1C1F25, a 1 px #8A6E3C top
 * border and a 1 px #3A3F48 right border; three 28x28 buttons (eye, flag, grid); the raised 200x200 plate (#262A31, 1 px
 * #8A6E3C, drop-shadow bake, two 1 px bevel lines, a 192x192 inner line, four ringed corner-node bakes); and the 184x184 map
 * surface drawn in CSS paint order: 1 px #0B0C0E border, the photo placeholder, the inset-shadow bake, the dim box, the fog radial
 * bake, the unit dots (half-pixel bakes for odd sizes) and the 54x34 camera rectangle. The ornament frame (SChimeraOrnamentFrame) is the last child.
 * All children are absolutely placed in panel coordinates, as the mockup does.
 */
class SChimeraMinimapPanel : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraMinimapPanel) {}
		SLATE_ARGUMENT(FChimeraHudState, State)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

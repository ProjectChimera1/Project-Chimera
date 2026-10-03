// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Ui/ChimeraHudState.h"

/**
 * The command card of board 3.1a (r4 5.7, ids 283-483), 300x216 at (1620, 864): fill #1C1F25, 1 px #8A6E3C top border, 1 px
 * #3A3F48 left border, padding 12. A 4x3 grid of 64x60 slots (gap 6) at (13, 13); each slot is a bordered box with a 16x16
 * keycap at (4, 4), a 24 px icon and an optional 9 px cost label. States (r4 5.7): normal; locked (CSS opacity .55 on the whole
 * button: every colour is pre-composited over #1C1F25 and drawn opaque, so no render opacity is used, plan B 2.4); active (green);
 * empty (a transparent box with the dashed-border bake). The ornament frame (rope, sigils, laser) is T7's.
 */
class SChimeraCommandCard : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraCommandCard) {}
		SLATE_ARGUMENT(FChimeraHudState, State)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

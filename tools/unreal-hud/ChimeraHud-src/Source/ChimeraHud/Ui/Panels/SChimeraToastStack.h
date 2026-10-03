// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Ui/ChimeraHudState.h"

/**
 * The alert toast of board 3.1a (r4 5.3, ids 69-80), 400x38: fill #14161A at .94, 1 px #3A3F48 border, padding 9/12, gap 10;
 * info icon 16 gold, the message (Inter 500 13, flex), a "Space" keycap, and the 2 px gold timer bar along the bottom edge.
 * The border is four lines over the translucent fill (an opaque border box under it would show through). One toast; the fade and
 * timer motion are not animated here (the gated frame is the t = 3.0 s state: opacity 1, bar 242.92 px).
 */
class SChimeraToastStack : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraToastStack) {}
		SLATE_ARGUMENT(FChimeraHudState, State)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

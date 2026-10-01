// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Ui/ChimeraHudState.h"

/**
 * Top strip of board 3.1a (r4 5.1, ids 8-68), first cut (T4a). 1920x40 #1C1F25 with a 1 px #3A3F48 bottom border; a flex row
 * (padding 0 8, gap 4, items centred in the 39 px content box) of four resource chips, a spacer, the alert-log button and the
 * menu button; the clock group (clock + speed pill, gap 10) centred on x=960 over the full 40 px. Built as CSS lays it out, with
 * fractional positions left to the layout (chips at y=5.5 snap to row 6 as Chromium's do).
 */
class SChimeraTopStrip : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraTopStrip) {}
		SLATE_ARGUMENT(FChimeraHudState, State)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

private:
	/** Chip / button chrome: 28 tall, 1 px border, padding 0 10, children centred with a 7 px gap. */
	static TSharedRef<SWidget> Chip(uint32 Fill, uint32 BorderColor, const TArray<TSharedRef<SWidget>>& Children);
};

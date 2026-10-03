// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/DeclarativeSyntaxSupport.h"
#include "Ui/ChimeraHudState.h"

/**
 * The control-group tab row of board 3.1a (r4 5.5, ids 219-230): one selected tab, 30 px tall, fill #2A2F38, 1 px #E3C887 on
 * top, left and right (no bottom border: it sits on the selection panel's top edge), padding 0 9, gap 6: the group number
 * (Mono 600 11 gold bright), the `owner` icon 14 and the unit count (Mono 600 12). Its width is the exact content width (59.81 px).
 */
class SChimeraGroupTabs : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SChimeraGroupTabs) {}
		SLATE_ARGUMENT(FChimeraHudState, State)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
};

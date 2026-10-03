// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Ui/ChimeraUi.h"
#include "Ui/ChimeraBakes.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Layout/SConstraintCanvas.h"

/** Absolute placement helpers shared by the panels: a top-left anchored slot is CSS `position:absolute; left; top; width; height`. */
namespace ChimeraUi
{
	/** Adds `Child` at (X, Y) with a W x H box (top-left anchored, in the canvas' local px). */
	inline void AddAbs(const TSharedRef<SConstraintCanvas>& Canvas, float X, float Y, float W, float H, const TSharedRef<SWidget>& Child)
	{
		Canvas->AddSlot().Anchors(FAnchors(0.f, 0.f, 0.f, 0.f)).Offset(FMargin(X, Y, W, H)).Alignment(FVector2D(0.f, 0.f))[Child];
	}

	/**
	 * Adds `Child` stretched across the canvas' width: CSS `position:absolute; left: Left; right: RightInset; top: Y; height: H`.
	 * A negative inset reaches past that edge. Used where the mockup gives a panel left and right instead of a width, so the panel
	 * follows the screen when the UI scale changes (T8's -HudUiScale shot); at 1920x1080 and scale 1 it lands on the same pixels.
	 */
	inline void AddStretchX(const TSharedRef<SConstraintCanvas>& Canvas, float Left, float Y, float RightInset, float H, const TSharedRef<SWidget>& Child)
	{
		Canvas->AddSlot().Anchors(FAnchors(0.f, 0.f, 1.f, 0.f)).Offset(FMargin(Left, Y, RightInset, H)).Alignment(FVector2D(0.f, 0.f))[Child];
	}

	/** A solid W x H rectangle at (X, Y). */
	inline void AddRect(const TSharedRef<SConstraintCanvas>& Canvas, float X, float Y, float W, float H, uint32 Rgb, float Alpha = 1.f)
	{
		AddAbs(Canvas, X, Y, W, H, SNew(SImage).Image(Solid(Rgb, Alpha)));
	}

	/** A hollow 1 px rectangle (CSS `border: 1px solid`, transparent inside) at (X, Y), W x H border box. */
	inline void AddHollowRect(const TSharedRef<SConstraintCanvas>& Canvas, float X, float Y, float W, float H, uint32 Rgb, float Alpha = 1.f)
	{
		AddRect(Canvas, X, Y, W, 1.f, Rgb, Alpha);
		AddRect(Canvas, X, Y + H - 1.f, W, 1.f, Rgb, Alpha);
		AddRect(Canvas, X, Y + 1.f, 1.f, H - 2.f, Rgb, Alpha);
		AddRect(Canvas, X + W - 1.f, Y + 1.f, 1.f, H - 2.f, Rgb, Alpha);
	}

	/** A baked image (drawn at its own pixel size) whose CSS box top-left is (X, Y); the bake's origin offset is applied here. */
	inline void AddBake(const TSharedRef<SConstraintCanvas>& Canvas, float X, float Y, const ChimeraBakes::FBake& Bake)
	{
		AddAbs(Canvas, X + (float)Bake.Origin.X, Y + (float)Bake.Origin.Y, (float)Bake.Size.X, (float)Bake.Size.Y, SNew(SImage).Image(Bake.Brush));
	}
}

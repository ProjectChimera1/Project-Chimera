// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

struct FSlateDynamicImageBrush;

/** Shared HUD helpers: colour conversion, and the file loaders every image read goes through (they log path + sha256 for the allow-list check). */
namespace ChimeraUi
{
	/** sRGB bytes to a linear colour. Never use FSlateColorBrush(FColor): it reinterprets bytes as linear. */
	FLinearColor Hex(uint32 Rgb, float Alpha = 1.0f);

	/** Parses "#RRGGBB" (or "RRGGBB"); returns false when malformed. */
	bool ParseHex(const FString& Text, uint32& OutRgb);

	/** Reads a file and logs "LogChimeraHud: loaded <path> <sha256>". Returns false (and logs an Error) when it cannot be read. */
	bool LoadFileBytes(const FString& Path, TArray<uint8>& OutBytes);

	/** Decodes a PNG into a BGRA8 sRGB Slate brush at its own pixel size. Null (with an Error log) on failure. */
	TSharedPtr<FSlateDynamicImageBrush> LoadPngBrush(const FString& Path, const FName& BrushName);
}

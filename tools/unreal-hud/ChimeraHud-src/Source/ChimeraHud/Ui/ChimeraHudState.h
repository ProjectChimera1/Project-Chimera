// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * The plain state the Match HUD reads (plan B section 0: one struct, filled with the 3.1a fixture, the HUD writes nothing).
 * Strings are authored as the mockup renders them (single spaces around the middle dots, r4 5.6). Later the sim fills it.
 */
struct FChimeraResourceChip
{
	FString Icon;		// icons.js name
	FString Value;		// "340"
	FString Rate;		// "+18"; empty keeps the empty rate slot (and its 7 px gap) as the supply chip does (r4 5.1)
	FString Tooltip;
};

/** Command-card slot state (r4 5.7). */
enum class EChimeraSlotState : uint8 { Normal, Locked, Active, Empty };

struct FChimeraCommandSlot
{
	FString Key;		// "Q"
	FString Name;		// "Foundry"
	FString Icon;		// icons.js name; empty for the empty slot
	FString Cost;		// "150 g"; empty when the order has no cost
	EChimeraSlotState State = EChimeraSlotState::Normal;
	FString LockedReason;
};

struct FChimeraHudState
{
	// Top strip (r4 5.1)
	TArray<FChimeraResourceChip> Chips;
	FString Clock;
	FString Speed;
	FString AlertCount;
	bool bAlertUrgent = false;

	// Toast (r4 5.3)
	FString ToastIcon;
	FString ToastText;
	FString ToastKey;

	// Control-group tab (r4 5.5)
	FString TabNumber;
	FString TabIcon;
	FString TabCount;

	// Selection panel (r4 5.6)
	FString UnitName;
	FString UnitRole;
	FString UnitHp;
	float UnitHpFraction = 1.f;
	FString UnitStats;

	// Command card (r4 5.7), row-major Q W E R / A S D F / Z X C V
	TArray<FChimeraCommandSlot> Slots;
};

/** Board 3.1a "HUD · early game" (Match.dc.html:610 res(), :698 overlay data, CARDS.worker :573). */
inline FChimeraHudState MakeBoard31aState()
{
	FChimeraHudState S;
	S.Chips = {
		{ TEXT("resource"), TEXT("340"), TEXT("+18"), TEXT("Gold · +18 every 10 s") },
		{ TEXT("terrain"), TEXT("60"), TEXT("+6"), TEXT("Iron · +6 every 10 s") },
		{ TEXT("array"), TEXT("20"), TEXT("+2"), TEXT("Aether · regenerates") },
		{ TEXT("users"), TEXT("9 / 20"), TEXT(""), TEXT("Supply used / max") },
	};
	S.Clock = TEXT("2:14");
	S.Speed = TEXT("Normal · ×1.0");
	S.AlertCount = TEXT("3");

	S.ToastIcon = TEXT("info");
	S.ToastText = TEXT("Acolyte idle at the west wells");
	S.ToastKey = TEXT("Space");

	S.TabNumber = TEXT("3");
	S.TabIcon = TEXT("owner");
	S.TabCount = TEXT("8");

	S.UnitName = TEXT("Covenant Acolyte");
	S.UnitRole = TEXT("Worker · carrying 10 gold");
	S.UnitHp = TEXT("220 / 220");
	S.UnitHpFraction = 1.f;
	S.UnitStats = TEXT("Armour 1 · Damage 6–8 · Speed 2.8 · Carry 10 g");

	using E = EChimeraSlotState;
	S.Slots = {
		{ TEXT("Q"), TEXT("Foundry"), TEXT("place"), TEXT("150 g"), E::Normal, TEXT("") },
		{ TEXT("W"), TEXT("Refinery"), TEXT("resource"), TEXT("100 g"), E::Normal, TEXT("") },
		{ TEXT("E"), TEXT("Watchtower"), TEXT("prop"), TEXT("75 g"), E::Locked, TEXT("Needs a Foundry") },
		{ TEXT("R"), TEXT("Alembic Works"), TEXT("array"), TEXT("220 g"), E::Locked, TEXT("Needs a Foundry and a Smithy") },
		{ TEXT("A"), TEXT("Attack"), TEXT("swords"), TEXT(""), E::Normal, TEXT("") },
		{ TEXT("S"), TEXT("Stop"), TEXT("stop"), TEXT(""), E::Normal, TEXT("") },
		{ TEXT("D"), TEXT("Hold"), TEXT("pathing"), TEXT(""), E::Normal, TEXT("") },
		{ TEXT("F"), TEXT("Patrol"), TEXT("flag"), TEXT(""), E::Normal, TEXT("") },
		{ TEXT("Z"), TEXT("Gather"), TEXT("resource"), TEXT(""), E::Active, TEXT("") },
		{ TEXT("X"), TEXT("Return cargo"), TEXT("undo"), TEXT(""), E::Normal, TEXT("") },
		{ TEXT("C"), TEXT("Repair"), TEXT("rotate"), TEXT(""), E::Normal, TEXT("") },
		{ TEXT("V"), TEXT("Empty slot"), TEXT(""), TEXT(""), E::Empty, TEXT("") },
	};
	return S;
}

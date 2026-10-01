// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.4 inputs, C8: the mouse controller of ChimeraTerrain. Cursor shown; LMB = a stroke along the cursor's pick, 1-5 = mode,
// Shift+1..4 = paint layer, [ ] = brush size -/+5 m, - = strength -5, = strength +5, Ctrl+Z / Ctrl+Y = undo / redo (all of these keys are
// IGNORED while LMB is held, stroke open or not; Godot swallowed them mid-stroke, DW-144). Strokes tick at 30 Hz from an accumulator fed
// by unclamped wall time that catches up at most 15 ticks per frame (a longer hitch drops ticks, counted as ticks_dropped); tick positions
// interpolate along the segment between frame picks.
// Every finished stroke is logged to the terrain actor (results.json mouse_strokes[]) with source=os|slate, centre, pick error and the
// height hash before and after; every key action is logged to mouse_keys[] (the director writes both).
// Input is ARMED only when no -ChimeraTerrainScript is loaded (interactive use) or while a script's await_mouse op runs, so a stray click
// or key during any other scripted run cannot edit the terrain or move its hashes.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/PlayerController.h"
#include "Data/TerrainBrush.h"
#include "Dom/JsonObject.h"
#include "ChimeraTerrainPlayerController.generated.h"

class ATerrainActor;

UCLASS()
class CHIMERATERRAIN_API AChimeraTerrainPlayerController : public APlayerController
{
	GENERATED_BODY()

public:
	AChimeraTerrainPlayerController();

	virtual void BeginPlay() override;
	virtual void PlayerTick(float DeltaTime) override;

	/** Arms or disarms mouse strokes and brush keys (the director's await_mouse op arms them for its duration). */
	void SetInputArmed(bool bInArmed);
	bool IsInputArmed() const { return bArmed; }
	/** True while a mouse stroke is open (the director's await_mouse hitch hook reads it). */
	bool IsMouseStrokeOpen() const { return bStroke; }
	int32 GetStrokeTicksApplied() const { return TicksApplied; }
	/** Applied key actions after the given number of finished strokes (await_mouse's tail wait). */
	int32 CountKeyActionsAfterStroke(int32 StrokeCount) const;
	/** results.json mouse_keys[] and mouse_controller{}. */
	const TArray<TSharedPtr<FJsonValue>>& GetKeyRecords() const { return KeyRecords; }
	TSharedRef<FJsonObject> ControllerSummaryJson() const;

private:
	ChimeraTerrain::FTerrainBrushParams Brush;

	// arming
	bool bArmed = false;
	int32 InputWhileDisarmed = 0;

	// stroke in progress
	bool bStroke = false;
	FVector2D LastPickM = FVector2D::ZeroVector;
	FVector2D StartPickM = FVector2D::ZeroVector;
	FVector2D StartPx = FVector2D::ZeroVector;
	double Accumulator = 0.0;
	/** Wall time of the previous stroke tick evaluation (FPlatformTime): the accumulator's input is unclamped, unlike the world delta. */
	double LastAccumSeconds = 0.0;
	int32 TicksApplied = 0;
	int32 TicksDropped = 0;
	int32 StrokeFrames = 0;
	int32 KeysIgnored = 0;
	double StrokeStartSeconds = 0.0;
	double MaxFrameGapS = 0.0;
	int64 TicksBeforeStroke = 0;
	FString StrokeSource;
	double StrokeCursorErrPx = 0.0;
	bool bStrokeSynth = false;
	ChimeraTerrain::ETerrainBrushMode StrokeMode = ChimeraTerrain::ETerrainBrushMode::Raise;
	float StrokeD = 0.0f;
	float StrokeS = 0.0f;
	int32 StrokeLayer = 0;
	FString PhaseBeforeStroke;
	uint32 StartHeightFnv = 0;

	FVector2D LastTickCentreSum = FVector2D::ZeroVector;
	FVector StartHit3 = FVector::ZeroVector;
	FVector2D LastPx = FVector2D::ZeroVector;
	bool bBrushInit = false;
	bool bLmbWasDown = false;
	TArray<FKey> PendingKeys;
	bool bSynthCtrl = false;
	bool bSynthShift = false;

	int32 StrokesDone = 0;
	int32 UndoKeys = 0;
	int32 RedoKeys = 0;
	int32 KeysIgnoredTotal = 0;
	TArray<TSharedPtr<FJsonValue>> KeyRecords;

	/** Slate fallback (-ChimeraTerrainSynthMouse): drives the same strokes and keys through FSlateApplication::Process*Event. */
	bool bSynth = false;
	struct FSynthKey
	{
		FKey Key;
		bool bCtrl = false;
		bool bShift = false;
	};
	struct FSynth
	{
		int32 State = 0;           // 0 wait for targets, 1 keys, 2 move to start, 3 press, 4 drag, 5 release, 6 settle after release, 7 tail keys, 8 done
		int32 Stroke = 0;
		int32 Step = 0;
		double StateSeconds = 0.0;
		TArray<TSharedPtr<FJsonObject>> Targets;
		FVector2D Cursor = FVector2D::ZeroVector;     // viewport pixels
		TSet<FKey> Pressed;
		bool bCtrlZFired = false;
		int32 CtrlZStep = 0;
		TArray<FSynthKey> Tail;
	};
	FSynth Synth;

	/** Targets of the await_mouse op (mouse_targets.json), loaded lazily; Num() == 0 while the file is not there yet. */
	TArray<TSharedPtr<FJsonObject>> Targets;
	void LoadTargets();

	ATerrainActor* FindTerrain() const;
	/** Pick under the mouse: terrain-space metres. False when the mouse is outside the viewport or the ray misses the terrain. */
	bool PickMouse(FVector2D& OutM, FVector& OutHit3, FVector2D& OutPx) const;
	void HandleKeys(ATerrainActor& Terrain);
	void RecordKey(const ATerrainActor& Terrain, const FString& Key, const FString& Action, bool bApplied);
	void BeginMouseStroke(ATerrainActor& Terrain, const FVector& Hit, const FVector2D& Px);
	void TickMouseStroke(ATerrainActor& Terrain, const FVector2D& PickM, bool bPicked);
	void EndMouseStroke(ATerrainActor& Terrain, const FVector2D& EndPx, const FVector2D& EndPickM);
	/** Where the platform cursor is relative to the viewport (pixels); false when there is no window. */
	bool PlatformCursorInViewport(FVector2D& Out) const;
	bool ViewportOrigin(FVector2D& OutScreenOrigin, FVector2D& OutSize) const;

	void StepSynth(float DeltaTime);
	void SynthMove(const FVector2D& Px);
	void SynthButton(bool bDown);
	void SynthKey(const FKey& Key, bool bDown);
};

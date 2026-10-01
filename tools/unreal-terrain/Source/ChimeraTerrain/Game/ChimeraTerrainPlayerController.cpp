// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Game/ChimeraTerrainPlayerController.h"

#include "ChimeraTerrain.h"
#include "Data/TerrainIO.h"
#include "Data/TerrainPick.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "Engine/LocalPlayer.h"
#include "Engine/World.h"
#include "Framework/Application/SlateApplication.h"
#include "Game/ChimeraTerrainGameMode.h"
#include "Game/TerrainActor.h"
#include "InputCoreTypes.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Widgets/SWindow.h"

using namespace ChimeraTerrain;

namespace
{
	/** Plan C 3.4: 30 Hz stroke tick, at most 15 ticks of catch-up per frame (0.5 s); a longer hitch drops ticks. */
	constexpr double MouseTickHz = 30.0;
	constexpr int32 MouseMaxCatchUp = 15;
	/** Platform cursor and viewport mouse position must agree within this many pixels for a stroke to count as OS input. */
	constexpr double OsCursorTolPx = 3.0;
	/**
	 * Synthetic drag seconds. Plan C C8 fixes the stroke at 60 ticks: the press applies one, the 30 Hz accumulator the rest while LMB is
	 * held, so the button is held for about 59.5 / 30 s (drag plus the release frame).
	 */
	constexpr double SynthDragSeconds = 1.98;

	TSharedPtr<FJsonValue> V3(const FVector& V)
	{
		TArray<TSharedPtr<FJsonValue>> A;
		A.Add(MakeShared<FJsonValueNumber>(V.X));
		A.Add(MakeShared<FJsonValueNumber>(V.Y));
		A.Add(MakeShared<FJsonValueNumber>(V.Z));
		return MakeShared<FJsonValueArray>(A);
	}

	TSharedPtr<FJsonValue> V2(double X, double Y)
	{
		TArray<TSharedPtr<FJsonValue>> A;
		A.Add(MakeShared<FJsonValueNumber>(X));
		A.Add(MakeShared<FJsonValueNumber>(Y));
		return MakeShared<FJsonValueArray>(A);
	}

	bool ReadArray(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, TArray<double>& Out)
	{
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (!Obj.IsValid() || !Obj->TryGetArrayField(Field, Arr))
		{
			return false;
		}
		Out.Reset();
		for (const TSharedPtr<FJsonValue>& V : *Arr)
		{
			Out.Add(V->AsNumber());
		}
		return true;
	}

	const TCHAR* ModeName(ETerrainBrushMode M)
	{
		static const TCHAR* const Names[] = { TEXT("raise"), TEXT("lower"), TEXT("smooth"), TEXT("flatten"), TEXT("paint") };
		return Names[FMath::Clamp(static_cast<int32>(M), 0, 4)];
	}
}

AChimeraTerrainPlayerController::AChimeraTerrainPlayerController()
{
	bShowMouseCursor = true;
	DefaultMouseCursor = EMouseCursor::Default;
	PrimaryActorTick.bCanEverTick = true;
}

ATerrainActor* AChimeraTerrainPlayerController::FindTerrain() const
{
	const AChimeraTerrainGameMode* GM = GetWorld() ? GetWorld()->GetAuthGameMode<AChimeraTerrainGameMode>() : nullptr;
	return GM ? GM->GetTerrain() : nullptr;
}

void AChimeraTerrainPlayerController::BeginPlay()
{
	Super::BeginPlay();
	// Cursor shown, never hidden or locked by a mouse down (the project default is CapturePermanently, which hides it).
	bShowMouseCursor = true;
	FInputModeGameAndUI Mode;
	Mode.SetLockMouseToViewportBehavior(EMouseLockMode::DoNotLock);
	Mode.SetHideCursorDuringCapture(false);
	SetInputMode(Mode);
	bool bScripted = false;
	if (const AChimeraTerrainGameMode* GM = GetWorld() ? GetWorld()->GetAuthGameMode<AChimeraTerrainGameMode>() : nullptr)
	{
		bSynth = GM->GetOptions().bSynthMouse;
		bScripted = !GM->GetOptions().ScriptPath.IsEmpty();
	}
	// Interactive use: armed from the start. Scripted runs: armed only inside await_mouse (the director calls SetInputArmed).
	bArmed = !bScripted;
	UE_LOG(LogChimeraTerrain, Display, TEXT("mouse controller: cursor shown, input mode GameAndUI, source=%s, input %s"),
		bSynth ? TEXT("slate (synthetic)") : TEXT("os"), bArmed ? TEXT("armed (no script)") : TEXT("disarmed until await_mouse (scripted run)"));
}

void AChimeraTerrainPlayerController::SetInputArmed(bool bInArmed)
{
	if (bArmed == bInArmed)
	{
		return;
	}
	bArmed = bInArmed;
	UE_LOG(LogChimeraTerrain, Display, TEXT("mouse controller: input %s"), bArmed ? TEXT("armed") : TEXT("disarmed"));
}

int32 AChimeraTerrainPlayerController::CountKeyActionsAfterStroke(int32 StrokeCount) const
{
	int32 N = 0;
	for (const TSharedPtr<FJsonValue>& V : KeyRecords)
	{
		const TSharedPtr<FJsonObject> O = V->AsObject();
		if (O.IsValid() && O->GetBoolField(TEXT("applied")) && static_cast<int32>(O->GetNumberField(TEXT("after_strokes"))) >= StrokeCount)
		{
			++N;
		}
	}
	return N;
}

TSharedRef<FJsonObject> AChimeraTerrainPlayerController::ControllerSummaryJson() const
{
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetBoolField(TEXT("synthetic"), bSynth);
	J->SetBoolField(TEXT("armed_at_end"), bArmed);
	J->SetNumberField(TEXT("strokes"), StrokesDone);
	J->SetNumberField(TEXT("undo_keys"), UndoKeys);
	J->SetNumberField(TEXT("redo_keys"), RedoKeys);
	J->SetNumberField(TEXT("keys_ignored_while_lmb"), KeysIgnoredTotal);
	J->SetNumberField(TEXT("input_while_disarmed"), InputWhileDisarmed);
	return J;
}

void AChimeraTerrainPlayerController::LoadTargets()
{
	const AChimeraTerrainGameMode* GM = GetWorld() ? GetWorld()->GetAuthGameMode<AChimeraTerrainGameMode>() : nullptr;
	if (!GM || GM->GetOptions().OutDir.IsEmpty())
	{
		return;
	}
	const FString Path = FPaths::Combine(GM->GetOptions().OutDir, TEXT("mouse_targets.json"));
	FString Text;
	if (!FFileHelper::LoadFileToString(Text, *Path))
	{
		return;
	}
	TSharedPtr<FJsonObject> Root;
	if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Text), Root) || !Root.IsValid())
	{
		return;
	}
	const TArray<TSharedPtr<FJsonValue>>* Strokes = nullptr;
	if (!Root->TryGetArrayField(TEXT("strokes"), Strokes))
	{
		return;
	}
	Targets.Reset();
	for (const TSharedPtr<FJsonValue>& S : *Strokes)
	{
		Targets.Add(S->AsObject());
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("mouse controller: %d targets loaded from %s"), Targets.Num(), *Path);
}

bool AChimeraTerrainPlayerController::ViewportOrigin(FVector2D& OutScreenOrigin, FVector2D& OutSize) const
{
	if (!GEngine || !GEngine->GameViewport)
	{
		return false;
	}
	const TSharedPtr<SWindow> Window = GEngine->GameViewport->GetWindow();
	if (!Window.IsValid())
	{
		return false;
	}
	const FSlateRect Client = Window->GetClientRectInScreen();
	OutScreenOrigin = FVector2D(Client.Left, Client.Top);
	OutSize = FVector2D(Client.Right - Client.Left, Client.Bottom - Client.Top);
	return true;
}

bool AChimeraTerrainPlayerController::PlatformCursorInViewport(FVector2D& Out) const
{
	FVector2D Origin;
	FVector2D Size;
	if (!ViewportOrigin(Origin, Size) || !FSlateApplication::IsInitialized())
	{
		return false;
	}
	const FVector2D Cursor = FVector2D(FSlateApplication::Get().GetCursorPos());
	Out = Cursor - Origin;
	return true;
}

bool AChimeraTerrainPlayerController::PickMouse(FVector2D& OutM, FVector& OutHit3, FVector2D& OutPx) const
{
	ATerrainActor* Terrain = FindTerrain();
	double Mx = 0.0;
	double My = 0.0;
	if (!Terrain || !GetMousePosition(Mx, My))
	{
		return false;
	}
	OutPx = FVector2D(Mx, My);
	FVector Origin;
	FVector Dir;
	if (!DeprojectMousePositionToWorld(Origin, Dir))
	{
		return false;
	}
	FTerrainHit Hit;
	// Unreal cm to terrain metres; the pick is the analytic ray march over the CPU heights (plan C 3.6), no physics.
	if (!TerrainPick::RayCast(Terrain->GetHeightfield(), Origin / 100.0, Dir.GetSafeNormal(), 5000.0, Hit) || !Hit.bHit)
	{
		return false;
	}
	OutHit3 = Hit.Position;
	OutM = FVector2D(Hit.Position.X, Hit.Position.Y);
	return true;
}

void AChimeraTerrainPlayerController::RecordKey(const ATerrainActor& Terrain, const FString& Key, const FString& Action, bool bApplied)
{
	TSharedRef<FJsonObject> K = MakeShared<FJsonObject>();
	K->SetStringField(TEXT("key"), Key);
	K->SetStringField(TEXT("action"), Action);
	K->SetBoolField(TEXT("applied"), bApplied);
	K->SetNumberField(TEXT("after_strokes"), StrokesDone);
	K->SetStringField(TEXT("mode"), ModeName(Brush.Mode));
	K->SetNumberField(TEXT("d"), Brush.DiameterM);
	K->SetNumberField(TEXT("s"), Brush.Strength);
	K->SetNumberField(TEXT("layer"), Brush.PaintLayer);
	K->SetStringField(TEXT("height_fnv"), TerrainIO::HashToString(Terrain.HeightFnv()));
	K->SetStringField(TEXT("splat_fnv"), TerrainIO::HashToString(Terrain.SplatFnv()));
	KeyRecords.Add(MakeShared<FJsonValueObject>(K));
}

void AChimeraTerrainPlayerController::HandleKeys(ATerrainActor& Terrain)
{
	static const FKey DigitKeys[5] = { EKeys::One, EKeys::Two, EKeys::Three, EKeys::Four, EKeys::Five };
	static const TCHAR* const DigitNames[5] = { TEXT("1"), TEXT("2"), TEXT("3"), TEXT("4"), TEXT("5") };
	const bool bShift = IsInputKeyDown(EKeys::LeftShift) || IsInputKeyDown(EKeys::RightShift);
	const bool bCtrl = IsInputKeyDown(EKeys::LeftControl) || IsInputKeyDown(EKeys::RightControl);
	// Plan C 3.4: ignored while LMB is held. The button state, not the stroke flag: a press that missed the terrain (sky, off the map)
	// opens no stroke but still holds LMB, and must not let Ctrl+Z or the brush keys through (DW-144).
	const bool bHeld = bStroke || IsInputKeyDown(EKeys::LeftMouseButton);
	auto Pressed = [&](const FKey& K, const FString& Name)
	{
		if (!WasInputKeyJustPressed(K))
		{
			return false;
		}
		if (bHeld)
		{
			++KeysIgnored;
			++KeysIgnoredTotal;
			UE_LOG(LogChimeraTerrain, Display, TEXT("mouse key %s ignored: LMB is held"), *Name);
			RecordKey(Terrain, Name, TEXT("ignored_lmb_held"), false);
			return false;
		}
		return true;
	};
	auto BrushChanged = [&](const FString& Name, const TCHAR* Action)
	{
		Brush.Clamp();
		Terrain.SetBrushParams(Brush);
		UE_LOG(LogChimeraTerrain, Display, TEXT("mouse brush (%s): mode=%s d=%.0f s=%.0f layer=%d"), *Name, ModeName(Brush.Mode), Brush.DiameterM, Brush.Strength, Brush.PaintLayer);
		RecordKey(Terrain, Name, Action, true);
	};

	for (int32 I = 0; I < 5; ++I)
	{
		const FString Name = bShift ? FString::Printf(TEXT("shift+%s"), DigitNames[I]) : FString(DigitNames[I]);
		if (Pressed(DigitKeys[I], Name))
		{
			if (bShift)
			{
				if (I < 4)
				{
					Brush.Mode = ETerrainBrushMode::Paint;
					Brush.PaintLayer = I;
					BrushChanged(Name, TEXT("paint_layer"));
				}
			}
			else
			{
				Brush.Mode = static_cast<ETerrainBrushMode>(I);
				BrushChanged(Name, TEXT("mode"));
			}
		}
	}
	if (Pressed(EKeys::LeftBracket, TEXT("["))) { Brush.DiameterM -= 5.0f; BrushChanged(TEXT("["), TEXT("size")); }
	if (Pressed(EKeys::RightBracket, TEXT("]"))) { Brush.DiameterM += 5.0f; BrushChanged(TEXT("]"), TEXT("size")); }
	if (Pressed(EKeys::Hyphen, TEXT("-"))) { Brush.Strength -= 5.0f; BrushChanged(TEXT("-"), TEXT("strength")); }
	if (Pressed(EKeys::Equals, TEXT("="))) { Brush.Strength += 5.0f; BrushChanged(TEXT("="), TEXT("strength")); }
	if (bCtrl && Pressed(EKeys::Z, TEXT("ctrl+z")))
	{
		const bool bOk = Terrain.UndoLast();
		++UndoKeys;
		UE_LOG(LogChimeraTerrain, Display, TEXT("mouse key Ctrl+Z: undo %s (height_fnv=0x%08x)"), bOk ? TEXT("applied") : TEXT("had nothing"), Terrain.HeightFnv());
		RecordKey(Terrain, TEXT("ctrl+z"), bOk ? TEXT("undo") : TEXT("undo_nothing"), bOk);
	}
	if (bCtrl && Pressed(EKeys::Y, TEXT("ctrl+y")))
	{
		const bool bOk = Terrain.RedoLast();
		++RedoKeys;
		UE_LOG(LogChimeraTerrain, Display, TEXT("mouse key Ctrl+Y: redo %s (height_fnv=0x%08x)"), bOk ? TEXT("applied") : TEXT("had nothing"), Terrain.HeightFnv());
		RecordKey(Terrain, TEXT("ctrl+y"), bOk ? TEXT("redo") : TEXT("redo_nothing"), bOk);
	}
}

void AChimeraTerrainPlayerController::BeginMouseStroke(ATerrainActor& Terrain, const FVector& Hit, const FVector2D& Px)
{
	bStroke = true;
	StartPickM = FVector2D(Hit.X, Hit.Y);
	LastPickM = StartPickM;
	StartPx = Px;
	Accumulator = 0.0;
	TicksApplied = 0;
	TicksDropped = 0;
	StrokeFrames = 0;
	KeysIgnored = 0;
	MaxFrameGapS = 0.0;
	StrokeStartSeconds = FPlatformTime::Seconds();
	LastAccumSeconds = StrokeStartSeconds;
	TicksBeforeStroke = Terrain.GetTicksApplied();
	StrokeMode = Brush.Mode;
	StrokeD = Brush.DiameterM;
	StrokeS = Brush.Strength;
	StrokeLayer = Brush.PaintLayer;
	bStrokeSynth = bSynth;
	StrokeCursorErrPx = -1.0;
	FVector2D Plat;
	if (PlatformCursorInViewport(Plat))
	{
		StrokeCursorErrPx = FVector2D::Distance(Plat, Px);
	}
	StrokeSource = (!bStrokeSynth && StrokeCursorErrPx >= 0.0 && StrokeCursorErrPx <= OsCursorTolPx) ? TEXT("os") : TEXT("slate");
	StartHeightFnv = Terrain.HeightFnv();
	PhaseBeforeStroke = Terrain.GetPhase();
	Terrain.SetPhase(TEXT("mouse"));
	Terrain.SetBrushParams(Brush);
	Terrain.BeginStroke(Brush, StartPickM);
	// The press itself applies the first tick at the pick, so a click edits.
	Terrain.ApplyTick(StartPickM);
	++TicksApplied;
	LastTickCentreSum = StartPickM;
	UE_LOG(LogChimeraTerrain, Display, TEXT("mouse stroke %d begins: source=%s mode=%s d=%.0f s=%.0f at px=(%.1f,%.1f) pick=(%.3f,%.3f,%.3f) m platform_cursor_err_px=%.2f"),
		StrokesDone, *StrokeSource, ModeName(StrokeMode), StrokeD, StrokeS, Px.X, Px.Y, Hit.X, Hit.Y, Hit.Z, StrokeCursorErrPx);
	StartHit3 = Hit;
}

void AChimeraTerrainPlayerController::TickMouseStroke(ATerrainActor& Terrain, const FVector2D& PickM, bool bPicked)
{
	++StrokeFrames;
	const FVector2D Cur = bPicked ? PickM : LastPickM;
	// Wall time, not the world delta: the engine clamps the world delta to MaxUndilatedFrameTime (0.4 s, BaseGame.ini), which would
	// cap a hitch at 12 ticks and hide the rest; the plan's catch-up cap (15) and ticks_dropped need the real elapsed time.
	const double Now = FPlatformTime::Seconds();
	const double Dt = FMath::Max(0.0, Now - LastAccumSeconds);
	LastAccumSeconds = Now;
	MaxFrameGapS = FMath::Max(MaxFrameGapS, Dt);
	Accumulator += Dt * MouseTickHz;
	int32 N = FMath::FloorToInt(Accumulator);
	Accumulator -= N;
	if (N > MouseMaxCatchUp)
	{
		TicksDropped += N - MouseMaxCatchUp;
		UE_LOG(LogChimeraTerrain, Display, TEXT("mouse stroke %d: frame gap %.0f ms owed %d ticks, applied %d, dropped %d"), StrokesDone, Dt * 1000.0, N, MouseMaxCatchUp, N - MouseMaxCatchUp);
		N = MouseMaxCatchUp;
	}
	// Tick positions interpolate along the segment between the previous and this frame's pick (plan C 3.4).
	for (int32 I = 1; I <= N; ++I)
	{
		const FVector2D C = FMath::Lerp(LastPickM, Cur, static_cast<double>(I) / N);
		Terrain.ApplyTick(C);
		LastTickCentreSum += C;
		++TicksApplied;
	}
	LastPickM = Cur;
}

void AChimeraTerrainPlayerController::EndMouseStroke(ATerrainActor& Terrain, const FVector2D& EndPx, const FVector2D& EndPickM)
{
	const bool bPushed = Terrain.EndStroke();
	bStroke = false;
	Terrain.SetPhase(PhaseBeforeStroke);
	const int32 Index = StrokesDone++;
	TSharedRef<FJsonObject> R = MakeShared<FJsonObject>();
	R->SetNumberField(TEXT("index"), Index);
	R->SetStringField(TEXT("source"), StrokeSource);
	R->SetStringField(TEXT("mode"), ModeName(StrokeMode));
	R->SetNumberField(TEXT("d"), StrokeD);
	R->SetNumberField(TEXT("s"), StrokeS);
	R->SetNumberField(TEXT("layer"), StrokeLayer);
	R->SetNumberField(TEXT("ticks_applied"), TicksApplied);
	R->SetNumberField(TEXT("ticks_dropped"), TicksDropped);
	R->SetNumberField(TEXT("frames"), StrokeFrames);
	R->SetNumberField(TEXT("max_frame_gap_ms"), MaxFrameGapS * 1000.0);
	R->SetNumberField(TEXT("duration_s"), FPlatformTime::Seconds() - StrokeStartSeconds);
	R->SetBoolField(TEXT("undo_pushed"), bPushed);
	R->SetNumberField(TEXT("keys_ignored_while_lmb"), KeysIgnored);
	R->SetNumberField(TEXT("platform_cursor_err_px"), StrokeCursorErrPx);
	R->SetStringField(TEXT("start_height_fnv"), TerrainIO::HashToString(StartHeightFnv));
	R->SetStringField(TEXT("end_height_fnv"), TerrainIO::HashToString(Terrain.HeightFnv()));
	R->SetField(TEXT("start_px"), V2(StartPx.X, StartPx.Y));
	R->SetField(TEXT("end_px"), V2(EndPx.X, EndPx.Y));
	R->SetField(TEXT("start_world_m"), V3(StartHit3));
	R->SetField(TEXT("end_pick_m"), V2(EndPickM.X, EndPickM.Y));
	const FVector2D Centre = TicksApplied > 0 ? LastTickCentreSum / TicksApplied : StartPickM;
	R->SetField(TEXT("centre_m"), V2(Centre.X, Centre.Y));
	R->SetNumberField(TEXT("tick_samples_first"), static_cast<double>(TicksBeforeStroke));

	// Pick error against the logged target of this stroke. The start pick is the comparable one: this stroke has not yet edited the
	// surface under the press (an EARLIER stroke may have, which shifts the hit by about that edit's height; the targets are computed
	// on the surface before any mouse stroke). By the end the stroke itself has raised or lowered the surface under the cursor, so the
	// pick follows the edited surface (it drifts toward the camera on a raise and away on a lower); the end is checked in pixels, cursor
	// against the logged pixel, where 1 m at 80 m is about 9 px (plan C 5 P10).
	if (Targets.Num() == 0)
	{
		LoadTargets();
	}
	if (Targets.IsValidIndex(Index) && Targets[Index].IsValid())
	{
		TArray<double> FromW, ToW, FromPx, ToPx;
		if (ReadArray(Targets[Index], TEXT("from_world_m"), FromW) && ReadArray(Targets[Index], TEXT("to_world_m"), ToW)
			&& ReadArray(Targets[Index], TEXT("from_px"), FromPx) && ReadArray(Targets[Index], TEXT("to_px"), ToPx)
			&& FromW.Num() >= 3 && ToW.Num() >= 3 && FromPx.Num() >= 2 && ToPx.Num() >= 2)
		{
			const double ErrM = FVector::Distance(StartHit3, FVector(FromW[0], FromW[1], FromW[2]));
			const double ErrStartPx = FVector2D::Distance(StartPx, FVector2D(FromPx[0], FromPx[1]));
			const double ErrEndPx = FVector2D::Distance(EndPx, FVector2D(ToPx[0], ToPx[1]));
			const double EndDriftM = FVector2D::Distance(EndPickM, FVector2D(ToW[0], ToW[1]));
			R->SetNumberField(TEXT("target_index"), Index);
			R->SetNumberField(TEXT("pick_err_m"), ErrM);
			R->SetNumberField(TEXT("pick_err_start_px"), ErrStartPx);
			R->SetNumberField(TEXT("pick_err_end_px"), ErrEndPx);
			R->SetNumberField(TEXT("end_pick_drift_xy_m"), EndDriftM);
			UE_LOG(LogChimeraTerrain, Display, TEXT("mouse stroke %d pick error: %.3f m at the start (%.2f px), %.2f px at the end (end pick %.2f m off the planned end on the edited surface)"),
				Index, ErrM, ErrStartPx, ErrEndPx, EndDriftM);
		}
	}
	Terrain.AddMouseStrokeRecord(R);
	UE_LOG(LogChimeraTerrain, Display, TEXT("mouse stroke %d done: source=%s mode=%s d=%.0f s=%.0f centre=(%.2f,%.2f) m ticks_applied=%d ticks_dropped=%d frames=%d keys_ignored=%d undo_pushed=%d"),
		Index, *StrokeSource, ModeName(StrokeMode), StrokeD, StrokeS, Centre.X, Centre.Y, TicksApplied, TicksDropped, StrokeFrames, KeysIgnored, bPushed ? 1 : 0);
}

void AChimeraTerrainPlayerController::PlayerTick(float DeltaTime)
{
	Super::PlayerTick(DeltaTime);
	ATerrainActor* Terrain = FindTerrain();
	if (!Terrain)
	{
		return;
	}
	if (!bBrushInit)
	{
		Brush = Terrain->GetBrushParams();
		bBrushInit = true;
	}
	const bool bDown = IsInputKeyDown(EKeys::LeftMouseButton);
	if (!bArmed)
	{
		// Scripted run outside await_mouse: no stroke, no key. A stroke still open when the director disarmed is closed normally.
		if (bStroke)
		{
			EndMouseStroke(*Terrain, LastPx, LastPickM);
		}
		if (bDown && !bLmbWasDown)
		{
			++InputWhileDisarmed;
			UE_LOG(LogChimeraTerrain, Display, TEXT("mouse controller: LMB press ignored, input disarmed (scripted run outside await_mouse)"));
		}
		bLmbWasDown = bDown;
		return;
	}
	if (bSynth)
	{
		StepSynth(DeltaTime);
	}
	HandleKeys(*Terrain);

	FVector2D PickM;
	FVector Hit3;
	FVector2D Px;
	const bool bPicked = PickMouse(PickM, Hit3, Px);
	if (bDown && !bStroke)
	{
		if (bPicked && !bLmbWasDown)
		{
			BeginMouseStroke(*Terrain, Hit3, Px);
		}
	}
	else if (bDown && bStroke)
	{
		TickMouseStroke(*Terrain, PickM, bPicked);
	}
	else if (!bDown && bStroke)
	{
		EndMouseStroke(*Terrain, bPicked ? Px : LastPx, bPicked ? PickM : LastPickM);
	}
	if (bPicked)
	{
		LastPx = Px;
	}
	bLmbWasDown = bDown;
}

// ---- Slate fallback (-ChimeraTerrainSynthMouse). Events are routed through FSlateApplication::Process*Event
// (SlateApplication.h:1292-1310), the same entry the platform layer calls for real input, so everything below the Slate application
// is the real path; only the OS is skipped, hence the strokes log source=slate.

void AChimeraTerrainPlayerController::SynthMove(const FVector2D& Px)
{
	FVector2D Origin;
	FVector2D Size;
	if (!FSlateApplication::IsInitialized() || !ViewportOrigin(Origin, Size))
	{
		return;
	}
	const FVector2D Screen = Origin + Px;
	const FVector2D Last = Origin + Synth.Cursor;
	FPointerEvent E(0, Screen, Last, Synth.Pressed, FKey(), 0.0f, FSlateApplication::Get().GetModifierKeys());
	FSlateApplication::Get().ProcessMouseMoveEvent(E);
	Synth.Cursor = Px;
}

void AChimeraTerrainPlayerController::SynthButton(bool bDown)
{
	FVector2D Origin;
	FVector2D Size;
	if (!FSlateApplication::IsInitialized() || !ViewportOrigin(Origin, Size))
	{
		return;
	}
	const FVector2D Screen = Origin + Synth.Cursor;
	if (bDown)
	{
		Synth.Pressed.Add(EKeys::LeftMouseButton);
	}
	else
	{
		Synth.Pressed.Remove(EKeys::LeftMouseButton);
	}
	FPointerEvent E(0, Screen, Screen, Synth.Pressed, EKeys::LeftMouseButton, 0.0f, FSlateApplication::Get().GetModifierKeys());
	TSharedPtr<FGenericWindow> NativeWindow;
	if (GEngine && GEngine->GameViewport && GEngine->GameViewport->GetWindow().IsValid())
	{
		NativeWindow = GEngine->GameViewport->GetWindow()->GetNativeWindow();
	}
	if (bDown)
	{
		FSlateApplication::Get().ProcessMouseButtonDownEvent(NativeWindow, E);
	}
	else
	{
		FSlateApplication::Get().ProcessMouseButtonUpEvent(E);
	}
}

void AChimeraTerrainPlayerController::SynthKey(const FKey& Key, bool bDown)
{
	if (!FSlateApplication::IsInitialized())
	{
		return;
	}
	if (Key == EKeys::LeftControl)
	{
		bSynthCtrl = bDown;
	}
	if (Key == EKeys::LeftShift)
	{
		bSynthShift = bDown;
	}
	const FModifierKeysState Mods(bSynthShift, false, bSynthCtrl, false, false, false, false, false, false);
	const FKeyEvent E(Key, Mods, 0, false, 0, 0);
	if (bDown)
	{
		FSlateApplication::Get().ProcessKeyDownEvent(E);
	}
	else
	{
		FSlateApplication::Get().ProcessKeyUpEvent(E);
	}
}

void AChimeraTerrainPlayerController::StepSynth(float DeltaTime)
{
	Synth.StateSeconds += DeltaTime;
	++Synth.Step;
	auto Goto = [&](int32 State)
	{
		Synth.State = State;
		Synth.StateSeconds = 0.0;
		Synth.Step = 0;
	};
	switch (Synth.State)
	{
	case 0:
		if (Synth.StateSeconds > 1.0)
		{
			Synth.StateSeconds = 0.0;
			LoadTargets();
			if (Targets.Num() > 0)
			{
				Synth.Targets = Targets;
				// The same brush the OS injector sets with keys: d 30 (] twice), s 30 (= four times).
				PendingKeys = { EKeys::RightBracket, EKeys::RightBracket, EKeys::Equals, EKeys::Equals, EKeys::Equals, EKeys::Equals };
				// The same tail as the injector, after the last stroke: undo, redo, the size/strength keys back and forth, modes 3-5,
				// then Shift+2 (paint layer 1) so the HUD shot shows a paint brush.
				Synth.Tail = {
					{ EKeys::Z, true, false }, { EKeys::Y, true, false },
					{ EKeys::LeftBracket, false, false }, { EKeys::Hyphen, false, false }, { EKeys::RightBracket, false, false }, { EKeys::Equals, false, false },
					{ EKeys::Three, false, false }, { EKeys::Four, false, false }, { EKeys::Five, false, false },
					{ EKeys::Two, false, true } };
				Goto(1);
			}
		}
		break;
	case 1:
		// One key per 4 frames: down on the first frame, up on the third.
		if (PendingKeys.Num() == 0)
		{
			Goto(2);
		}
		else if (Synth.Step == 1)
		{
			SynthKey(PendingKeys[0], true);
		}
		else if (Synth.Step == 3)
		{
			SynthKey(PendingKeys[0], false);
			PendingKeys.RemoveAt(0);
			Synth.Step = 0;
		}
		break;
	case 2:
	{
		TArray<double> FromPx;
		if (!Synth.Targets.IsValidIndex(Synth.Stroke) || !ReadArray(Synth.Targets[Synth.Stroke], TEXT("from_px"), FromPx) || FromPx.Num() < 2)
		{
			Goto(7);
			break;
		}
		if (Synth.Step == 1)
		{
			if (Synth.Stroke == 1)
			{
				PendingKeys = { EKeys::Two };
			}
			else if (Synth.Stroke == 2)
			{
				PendingKeys = { EKeys::One };
			}
			SynthMove(FVector2D(FromPx[0], FromPx[1]));
		}
		if (PendingKeys.Num() > 0 && Synth.Step % 4 == 2)
		{
			SynthKey(PendingKeys[0], true);
		}
		else if (PendingKeys.Num() > 0 && Synth.Step % 4 == 0)
		{
			SynthKey(PendingKeys[0], false);
			PendingKeys.RemoveAt(0);
		}
		if (PendingKeys.Num() == 0 && Synth.StateSeconds > 0.4)
		{
			Goto(3);
		}
		break;
	}
	case 3:
		SynthButton(true);
		Synth.bCtrlZFired = false;
		Synth.CtrlZStep = 0;
		Goto(4);
		break;
	case 4:
	{
		TArray<double> FromPx, ToPx;
		ReadArray(Synth.Targets[Synth.Stroke], TEXT("from_px"), FromPx);
		ReadArray(Synth.Targets[Synth.Stroke], TEXT("to_px"), ToPx);
		const double T = FMath::Clamp(Synth.StateSeconds / SynthDragSeconds, 0.0, 1.0);
		SynthMove(FMath::Lerp(FVector2D(FromPx[0], FromPx[1]), FVector2D(ToPx[0], ToPx[1]), T));
		if (Synth.Stroke == 2)
		{
			// Ctrl+Z at the middle of the third stroke: must be ignored while LMB is held.
			if (T >= 0.5 && !Synth.bCtrlZFired)
			{
				SynthKey(EKeys::LeftControl, true);
				SynthKey(EKeys::Z, true);
				Synth.bCtrlZFired = true;
				Synth.CtrlZStep = Synth.Step;
			}
			else if (Synth.bCtrlZFired && Synth.Step == Synth.CtrlZStep + 3)
			{
				SynthKey(EKeys::Z, false);
				SynthKey(EKeys::LeftControl, false);
			}
		}
		if (T >= 1.0 && Synth.Step > Synth.CtrlZStep + 4)
		{
			Goto(5);
		}
		break;
	}
	case 5:
		SynthButton(false);
		Goto(6);
		break;
	case 6:
		// Hold the cursor at the drag end for a few frames so the controller sees the release there (it ends the stroke on the frame
		// it reads LMB up, and logs end_px from that frame's cursor). Re-sent every frame: when the release drops the mouse capture,
		// Slate synthesizes a mouse move from the PLATFORM cursor (which the fallback never moves), and that would otherwise become the
		// viewport's mouse position on the release frame.
		SynthMove(Synth.Cursor);
		if (Synth.Step >= 3)
		{
			++Synth.Stroke;
			Goto(Synth.Stroke >= Synth.Targets.Num() ? 7 : 2);
		}
		break;
	case 7:
	{
		// Tail keys, 8 frames each: modifiers down (1), key down (2), key up (4), modifiers up (5).
		if (Synth.Tail.Num() == 0)
		{
			Goto(8);
			break;
		}
		const FSynthKey K = Synth.Tail[0];
		if (Synth.Step == 1)
		{
			if (K.bCtrl) { SynthKey(EKeys::LeftControl, true); }
			if (K.bShift) { SynthKey(EKeys::LeftShift, true); }
		}
		else if (Synth.Step == 2)
		{
			SynthKey(K.Key, true);
		}
		else if (Synth.Step == 4)
		{
			SynthKey(K.Key, false);
		}
		else if (Synth.Step == 5)
		{
			if (K.bCtrl) { SynthKey(EKeys::LeftControl, false); }
			if (K.bShift) { SynthKey(EKeys::LeftShift, false); }
		}
		else if (Synth.Step >= 8)
		{
			Synth.Tail.RemoveAt(0);
			Synth.Step = 0;
		}
		break;
	}
	default:
		break;
	}
}

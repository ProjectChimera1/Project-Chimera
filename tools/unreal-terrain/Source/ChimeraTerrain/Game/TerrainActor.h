// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.1 Game/: the terrain in the world. Owns the CPU heightfield, undo history, the chunk renderer and the splat texture;
// every edit (script or, from C8, mouse) goes through BeginStroke / ApplyTick / EndStroke here.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Data/TerrainHeightfield.h"
#include "Data/TerrainBrush.h"
#include "Data/TerrainUndo.h"
#include "Render/TerrainChunkRenderer.h"
#include "Render/TerrainSplatTexture.h"
#include "TerrainActor.generated.h"

class UMaterialInterface;
class UMaterialInstanceDynamic;
class FJsonObject;

/** Timings of one stroke tick, milliseconds (plan C 3.8 metrics: apply, normals, upload-submit, splat). */
struct FTerrainTickTiming
{
	/** Brush maths on the CPU arrays (including the undo snapshots). */
	double ApplyMs = 0.0;
	/** Normals and tangents of the touched chunks. */
	double NormalsMs = 0.0;
	/** Position writes, stream copies and the submit to the render side. */
	double UploadMs = 0.0;
	double SplatMs = 0.0;
	double TotalMs() const { return ApplyMs + NormalsMs + UploadMs + SplatMs; }
};

/** One logged stroke tick (script or mouse): who and what it was, and what it cost. Written to ticks.csv by the director. */
struct FTerrainTickSample
{
	int64 Index = 0;
	uint64 Frame = 0;
	int32 PhaseIndex = 0;
	uint8 Mode = 0;
	float DiameterM = 0.0f;
	float Strength = 0.0f;
	FTerrainTickTiming Timing;
	int32 VerticesChanged = 0;
	int32 TexelsChanged = 0;
	int32 UploadedVertices = 0;
};

UCLASS()
class CHIMERATERRAIN_API ATerrainActor : public AActor
{
	GENERATED_BODY()

public:
	ATerrainActor();

	/** Build the flat heightfield (half extent, chunk size), the splat texture, the default material and the renderer. */
	bool InitTerrain(int32 HalfExtentM, int32 ChunkQuads, ChimeraTerrain::ETerrainDrawType DrawType);

	/**
	 * Ground material for InitTerrain (plan C 3.5, C7); call before InitTerrain. bGrey = C3's lit grey instead of M_ChimeraGround;
	 * GroundParams = "Name=Value,Name=Value" scalar overrides applied to the ground MID (an unknown name is logged as an error).
	 */
	void SetMaterialOptions(bool bGrey, const FString& GroundParams) { bGreyMaterial = bGrey; GroundParamsSpec = GroundParams; }

	/** Brush ring of the ground material (plan C 3.5: BrushX/BrushY/BrushRadius on the MID). Disabled = radius 0 (compare mode). */
	void SetBrushRingEnabled(bool bEnabled);
	/** Ring centre (terrain metres) and radius (m); shown only while enabled. ApplyTick calls it with the tick centre and the brush radius. */
	void SetBrushRing(const FVector2D& CenterM, float RadiusM);
	bool IsBrushRingEnabled() const { return bRingEnabled; }

	/** Material in use: path, whether it is the ground material, the MID scalar values (results.json "material"). */
	TSharedRef<FJsonObject> DescribeMaterial() const;
	/** Non-empty when the ground material is missing or a -ChimeraTerrainGround item named no scalar parameter. */
	const FString& GetMaterialError() const { return MaterialError; }

	/** Collision settings for the renderer InitTerrain creates (plan C 3.6); call before InitTerrain. */
	void SetCollisionOptions(const ChimeraTerrain::FTerrainCollisionOptions& InOptions) { CollisionOptions = InOptions; }

	/**
	 * Vertex rects whose physics collision was rewritten since the last call (stroke end, mid-stroke, undo, redo, load; not the initial
	 * build), oldest first. verify_collision casts its vertex rays inside them (plan C 3.8).
	 */
	TArray<ChimeraTerrain::FTerrainRect> ConsumeCollisionRects();

	/** Open a stroke (flatten target fixed at the height under StartM; plan C 3.4). */
	void BeginStroke(const ChimeraTerrain::FTerrainBrushParams& Params, const FVector2D& StartM);
	/** One stroke tick centred on terrain-space metres (= Unreal cm / 100). Uploads the changed rect at once. */
	ChimeraTerrain::FTerrainTickResult ApplyTick(const FVector2D& CenterM, FTerrainTickTiming* OutTiming = nullptr);
	/** Close the stroke: exact bounds of the touched chunks, undo entry. Returns true when an entry was pushed. */
	bool EndStroke();
	bool IsStrokeOpen() const { return bStrokeOpen; }

	bool UndoLast();
	bool RedoLast();

	/** terrain.json + height.r32 + splat.rgba8 into Dir (plan C 3.2). False with a reason in OutError. */
	bool SaveTo(const FString& Dir, FString& OutError);
	/** Replace the terrain with the files in Dir (same half extent required), rebuild every chunk, re-upload the splat, clear undo. */
	bool LoadFrom(const FString& Dir, FString& OutError);

	/** Label stored with every tick from now on (e.g. "sculpt", "walk"). */
	void SetPhase(const FString& Name);
	const FString& GetPhase() const { return PhaseNames[CurrentPhase]; }
	const TArray<FString>& GetPhaseNames() const { return PhaseNames; }
	const TArray<FTerrainTickSample>& GetTickSamples() const { return TickSamples; }

	/** Mouse strokes (C8): the controller records one JSON object per finished stroke (source os|slate, centre, pick error). */
	void AddMouseStrokeRecord(const TSharedRef<FJsonObject>& Record);
	const TArray<TSharedRef<FJsonObject>>& GetMouseStrokeRecords() const { return MouseStrokeRecords; }

	void SetTerrainVisible(bool bVisible);
	bool IsTerrainVisible() const { return bVisibleNow; }

	/** True while mesh updates or splat uploads are still in flight. */
	bool HasPendingWork() const;

	uint32 HeightFnv() const { return HF.HeightFnv(); }
	uint32 SplatFnv() const { return HF.SplatFnv(); }

	const ChimeraTerrain::FTerrainHeightfield& GetHeightfield() const { return HF; }
	ChimeraTerrain::ITerrainChunkRenderer* GetRenderer() const { return Renderer.Get(); }
	const ChimeraTerrain::FTerrainSplatTexture& GetSplat() const { return Splat; }
	ChimeraTerrain::ETerrainDrawType GetDrawType() const { return DrawType; }

	/** HUD state. */
	const ChimeraTerrain::FTerrainBrushParams& GetBrushParams() const { return Params; }
	double GetLastTickMs() const { return LastTickMs; }

	/** Ticks applied over the actor's life. */
	int64 GetTicksApplied() const { return TicksApplied; }

	/** Bytes held by the undo and redo history (plan C 3.7: capped at 512 MiB / 1000 entries). The soak reports it beside UsedPhysical. */
	int64 GetUndoBytes() const { return Undo.TotalBytes(); }
	/** Drops the whole undo/redo history (the soak's reported residue sample only; no editor path calls it). */
	void ClearUndoHistory() { Undo.Clear(); }

	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

private:
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;

	UPROPERTY(Transient)
	TObjectPtr<UMaterialInstanceDynamic> GroundMid;

	ChimeraTerrain::FTerrainHeightfield HF;
	ChimeraTerrain::FTerrainUndo Undo;
	ChimeraTerrain::FTerrainSplatTexture Splat;
	TUniquePtr<ChimeraTerrain::ITerrainChunkRenderer> Renderer;
	ChimeraTerrain::ETerrainDrawType DrawType = ChimeraTerrain::ETerrainDrawType::Dynamic;
	ChimeraTerrain::FTerrainBrushParams Params;

	bool bStrokeOpen = false;
	bool bVisibleNow = true;
	ChimeraTerrain::FTerrainRect StrokeHeightRect;
	ChimeraTerrain::FTerrainRect StrokeSplatRect;
	/** Heights changed since the last mid-stroke collision update (chimera.terrain.CollisionDuringStroke > 0). */
	ChimeraTerrain::FTerrainRect MidStrokeCollisionRect;
	double LastMidStrokeCollisionSeconds = 0.0;
	ChimeraTerrain::FTerrainCollisionOptions CollisionOptions;
	TArray<ChimeraTerrain::FTerrainRect> CollisionRects;
	double LastTickMs = 0.0;
	int64 TicksApplied = 0;
	TArray<FString> PhaseNames = { TEXT("default") };
	int32 CurrentPhase = 0;
	TArray<FTerrainTickSample> TickSamples;
	TArray<TSharedRef<FJsonObject>> MouseStrokeRecords;

	void ApplyDelta(const ChimeraTerrain::FTerrainEditDelta& Delta, ChimeraTerrain::ETerrainCollisionReason Reason);
	void SubmitCollision(const ChimeraTerrain::FTerrainRect& Rect, ChimeraTerrain::ETerrainCollisionReason Reason);
	UMaterialInterface* MakeDefaultMaterial();
	/** M_ChimeraGround as a MID (splat texture, HalfExtentM, overrides); the grey material when bGreyMaterial is set or it is missing. */
	UMaterialInterface* MakeGroundMaterial();
	void PushBrushRing();

	bool bGreyMaterial = false;
	bool bGroundMaterial = false;
	FString GroundParamsSpec;
	FString MaterialPath;
	FString MaterialError;
	TArray<TPair<FName, float>> GroundOverrides;
	bool bRingEnabled = true;
	FVector2D RingCenterM = FVector2D::ZeroVector;
	float RingRadiusM = 0.0f;
};

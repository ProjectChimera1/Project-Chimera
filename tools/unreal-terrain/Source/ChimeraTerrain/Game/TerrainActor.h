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
	double LastTickMs = 0.0;
	int64 TicksApplied = 0;
	TArray<FString> PhaseNames = { TEXT("default") };
	int32 CurrentPhase = 0;
	TArray<FTerrainTickSample> TickSamples;
	TArray<TSharedRef<FJsonObject>> MouseStrokeRecords;

	void ApplyDelta(const ChimeraTerrain::FTerrainEditDelta& Delta);
	UMaterialInterface* MakeDefaultMaterial();
};

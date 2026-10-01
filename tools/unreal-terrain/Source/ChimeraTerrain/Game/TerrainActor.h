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

/** Timings of one stroke tick, milliseconds (plan C 3.8 metrics). */
struct FTerrainTickTiming
{
	double ApplyMs = 0.0;
	double UploadMs = 0.0;
	double SplatMs = 0.0;
	double TotalMs() const { return ApplyMs + UploadMs + SplatMs; }
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

	void ApplyDelta(const ChimeraTerrain::FTerrainEditDelta& Delta);
	UMaterialInterface* MakeDefaultMaterial();
};

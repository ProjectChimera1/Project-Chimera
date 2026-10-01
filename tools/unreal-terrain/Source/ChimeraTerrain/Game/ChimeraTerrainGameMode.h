// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.1: GlobalDefaultGameMode of ChimeraTerrain (DefaultEngine.ini). Spawns the lighting, the terrain and, with
// -ChimeraTerrainScript=<json>, the script director, on /Engine/Maps/Entry (no .umap of ours).
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "Render/TerrainChunkRenderer.h"
#include "ChimeraTerrainGameMode.generated.h"

class ATerrainActor;
class ATerrainLighting;
class ATerrainScriptDirector;

/** Command-line options (plan C 3.8). String values use FParse::Value(..., false) (EXECUTION 2.2). */
struct FChimeraTerrainOptions
{
	int32 HalfExtentM = 160;
	int32 ChunkQuads = 64;
	ChimeraTerrain::ETerrainDrawType DrawType = ChimeraTerrain::ETerrainDrawType::Dynamic;
	FString ScriptPath;
	FString OutDir;
	bool bCompareAtStart = false;
	float CompareEV100 = 2.0f;
	/** settle op timeout (plan C 3.8: 600 s); run_terrain raises it for warm-ups (-TimeoutMin > 15) where shaders may compile for up to an hour. */
	double SettleTimeoutS = 600.0;

	static FChimeraTerrainOptions FromCommandLine(const TCHAR* CommandLine);
};

UCLASS()
class CHIMERATERRAIN_API AChimeraTerrainGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	AChimeraTerrainGameMode();

	virtual void StartPlay() override;
	/** Entry has no PlayerStart: the pawn starts at the rts80 pose instead of logging "Player start not found". */
	virtual void RestartPlayer(AController* NewPlayer) override;

	ATerrainActor* GetTerrain() const { return Terrain; }
	ATerrainLighting* GetLighting() const { return Lighting; }
	const FChimeraTerrainOptions& GetOptions() const { return Options; }

private:
	UPROPERTY(Transient)
	TObjectPtr<ATerrainActor> Terrain;
	UPROPERTY(Transient)
	TObjectPtr<ATerrainLighting> Lighting;
	UPROPERTY(Transient)
	TObjectPtr<ATerrainScriptDirector> Director;

	FChimeraTerrainOptions Options;
};

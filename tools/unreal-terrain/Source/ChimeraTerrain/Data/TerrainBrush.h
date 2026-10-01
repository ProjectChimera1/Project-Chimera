// Chimera terrain trial, plan C 3.4: brushes with Terrain3D 1.0.1 semantics (known Godot flaws fixed). Pure C++, no UObjects.
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	class FTerrainUndo;

	/** Key order 1..5 in the editor. */
	enum class ETerrainBrushMode : uint8
	{
		Raise,
		Lower,
		Smooth,
		Flatten,
		Paint
	};

	struct FTerrainBrushParams
	{
		static constexpr float MinDiameterM = 5.0f;
		static constexpr float MaxDiameterM = 100.0f;
		static constexpr float MinStrength = 1.0f;
		static constexpr float MaxStrength = 100.0f;

		ETerrainBrushMode Mode = ETerrainBrushMode::Raise;
		/** Brush diameter in metres, 5..100. */
		float DiameterM = 20.0f;
		/** Strength 1..100; one stroke tick applies k = Strength * 0.01. */
		float Strength = 10.0f;
		/** Paint layer 0..3. */
		int32 PaintLayer = 0;
		/** Flatten target height in metres, fixed per stroke by the caller (the height under the cursor at stroke start). */
		float FlattenTarget = 0.0f;

		float Radius() const { return DiameterM * 0.5f; }
		float K() const { return Strength * 0.01f; }
		/** Clamp diameter, strength and layer into their legal ranges. */
		void Clamp();
	};

	struct FTerrainTickResult
	{
		/** Bounding rect of the vertices whose height changed (empty when none did). */
		FTerrainRect HeightRect;
		/** Bounding rect of the splat texels whose bytes changed. */
		FTerrainRect SplatRect;
		int32 VerticesChanged = 0;
		int32 TexelsChanged = 0;

		bool Changed() const { return VerticesChanged > 0 || TexelsChanged > 0; }
	};

	/**
	 * One stroke tick (30 Hz in the game). Footprint: vertices (texels for paint) at distance r < R from the centre, falloff a = 1 - r/R.
	 * Script strokes call this exactly `ticks` times regardless of frame time, so results do not depend on fps (plan C 3.4).
	 */
	class FTerrainBrush
	{
	public:
		/**
		 * Apply one tick centred on terrain-space (CenterX, CenterY) metres. When Undo is given, the chunks the footprint can touch are
		 * snapshotted (FTerrainUndo::Touch) BEFORE any write; the caller owns Begin/EndStroke.
		 */
		static FTerrainTickResult ApplyTick(FTerrainHeightfield& HF, const FTerrainBrushParams& Params, float CenterX, float CenterY, FTerrainUndo* Undo = nullptr);

		/** Flatten target for a stroke starting under (CenterX, CenterY): the triangulated surface height there. */
		static float FlattenTargetAt(const FTerrainHeightfield& HF, float CenterX, float CenterY);

		/**
		 * Paint one RGBA8 texel: weights[Layer] += Delta (capped so the layer reaches at most 255); the same total is taken from the other
		 * layers in proportion, floored; leftover units go by largest fractional remainder, ties to the lower layer id. The four weights keep
		 * summing to exactly 255. Returns the delta actually applied (0 when nothing changed).
		 */
		static int32 PaintTexel(uint8* Texel, int32 Layer, int32 Delta);
	};
}

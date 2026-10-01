// Chimera terrain trial, plan C 3.2-3.3: the engine-neutral heightfield (pure C++, no UObjects).
// One global grid of float32 heights (1 m vertex spacing, centred on the origin) plus an RGBA8 splat map (0.5 m texels).
// "Terrain space" is metres with +X = column direction, +Y = row direction, +Z = up; it equals Unreal world cm / 100
// (SimToUe maps sim (x, z, h) to (100x, 100z, 100h) cm, see TerrainSimExport.h).
#pragma once

#include "CoreMinimal.h"

namespace ChimeraTerrain
{
	/** Paint layers, same ids as Godot (0 Grass, 1 Dirt, 2 Rock, 3 Snow); channel = id (R, G, B, A). */
	constexpr int32 SplatLayerCount = 4;
	/** Splat texels per metre (0.5 m texels). */
	constexpr int32 SplatTexelsPerMeter = 2;
	/** Heights are clamped to +-128 m (trial choice, plan C 3.2). */
	constexpr float MaxHeightM = 128.0f;

	constexpr uint32 FnvOffsetBasis = 2166136261u;
	constexpr uint32 FnvPrime = 16777619u;

	/** FNV-1a 32 over a byte span, continuing from Hash. */
	uint32 Fnv1aBytes(uint32 Hash, const uint8* Data, int64 Num);
	/** FNV-1a 32 over the 4 little-endian bytes of Value, continuing from Hash. */
	uint32 Fnv1aU32LE(uint32 Hash, uint32 Value);

	/** Half-open integer rectangle [X0, X1) x [Y0, Y1). Empty when X1 <= X0 or Y1 <= Y0. */
	struct FTerrainRect
	{
		int32 X0 = 0;
		int32 Y0 = 0;
		int32 X1 = 0;
		int32 Y1 = 0;

		FTerrainRect() = default;
		FTerrainRect(int32 InX0, int32 InY0, int32 InX1, int32 InY1) : X0(InX0), Y0(InY0), X1(InX1), Y1(InY1) {}

		bool IsEmpty() const { return X1 <= X0 || Y1 <= Y0; }
		int32 Width() const { return IsEmpty() ? 0 : X1 - X0; }
		int32 Height() const { return IsEmpty() ? 0 : Y1 - Y0; }
		int64 Area() const { return static_cast<int64>(Width()) * Height(); }
		bool Contains(int32 X, int32 Y) const { return X >= X0 && X < X1 && Y >= Y0 && Y < Y1; }

		/** Grow to contain the cell (X, Y). */
		void Include(int32 X, int32 Y);
		/** Grow to contain Other (no-op when Other is empty). */
		void Union(const FTerrainRect& Other);
		/** This rectangle grown by N cells on every side (not clamped). */
		FTerrainRect Expanded(int32 N) const { return FTerrainRect(X0 - N, Y0 - N, X1 + N, Y1 + N); }
		/** Intersection (empty when disjoint). */
		FTerrainRect Intersect(const FTerrainRect& Other) const;

		bool operator==(const FTerrainRect& Other) const
		{
			return (IsEmpty() && Other.IsEmpty()) || (X0 == Other.X0 && Y0 == Other.Y0 && X1 == Other.X1 && Y1 == Other.Y1);
		}
	};

	/**
	 * The heightfield and splat map. Vertex (X, Y) lies at terrain-space metres (X - E, Y - E) with E = half extent
	 * (trial: E = 160 -> 321 x 321 vertices, 320 x 320 quads, 640 x 640 splat texels).
	 *
	 * Chunks are ChunkQuads quads square (64 default). A chunk RENDERS (C+1)^2 vertices (borders duplicated from the global array, so seams
	 * are bit-equal) but OWNS a disjoint block [cx*C, (cx+1)*C) (the last chunk also owns the closing edge) so that undo snapshots never overlap.
	 * Splat texel (TX, TY) belongs to the chunk owning vertex (TX/2, TY/2).
	 */
	class FTerrainHeightfield
	{
	public:
		/** Allocate a flat field (height 0, every splat texel pure layer 0). InHalfExtentM must be >= 1; chunk size >= 1. */
		void Init(int32 InHalfExtentM, int32 InChunkQuads);
		/** Back to flat and all grass, same dimensions. */
		void Reset();

		bool IsInitialized() const { return Heights.Num() > 0; }

		int32 HalfExtentM() const { return HalfExtent; }
		/** Vertices per side (2E + 1). */
		int32 Width() const { return VertsPerSide; }
		/** Quads per side (2E). */
		int32 Quads() const { return VertsPerSide - 1; }
		/** Splat texels per side (4E). */
		int32 SplatSize() const { return SplatTexels; }
		int32 ChunkQuads() const { return ChunkSize; }
		int32 ChunksPerSide() const { return NumChunksSide; }
		int32 NumChunks() const { return NumChunksSide * NumChunksSide; }

		// ---- vertices -------------------------------------------------------------------------------------------
		float GetHeight(int32 X, int32 Y) const { return Heights[Y * VertsPerSide + X]; }
		/** Index-clamped read (X, Y outside the grid read the nearest edge vertex). */
		float GetHeightClamped(int32 X, int32 Y) const;
		/** Write a height, clamped to +-MaxHeightM. NaN is stored as 0. */
		void SetHeight(int32 X, int32 Y, float H);

		/** Terrain-space metre of a vertex index (same formula on both axes). */
		float VertexToWorld(int32 V) const { return static_cast<float>(V - HalfExtent); }
		/** Terrain-space metre of the centre of splat texel T (0.5 m texels). */
		float TexelCenterToWorld(int32 T) const { return (static_cast<float>(T) + 0.5f) * 0.5f - static_cast<float>(HalfExtent); }

		/**
		 * Normal at a vertex from global central differences, indices clamped to the grid so the map border uses one-sided differences:
		 * n = normalize(-dh/dx, -dh/dy, 1). Exact for a tilted plane everywhere.
		 */
		FVector3f GetNormal(int32 X, int32 Y) const;
		/** Tangent normalize(1, 0, dh/dx) with the same differences. */
		FVector3f GetTangent(int32 X, int32 Y) const;

		/**
		 * Height of the TRIANGULATED surface at terrain-space (WorldX, WorldY) metres, clamped to the map. Cells split along the
		 * bottom-left to top-right diagonal, matching the mesh index order (BL,TL,TR) and (BL,TR,BR) of the RMC FastUpdate example.
		 * This is what the pick returns and what the GPU mesh draws; it is NOT the sim's nearest-cell lookup (plan C 3.6).
		 */
		float SampleSurface(double WorldX, double WorldY) const;

		// ---- splat ----------------------------------------------------------------------------------------------
		const uint8* SplatTexel(int32 TX, int32 TY) const { return &Splat[(TY * SplatTexels + TX) * SplatLayerCount]; }
		uint8* SplatTexel(int32 TX, int32 TY) { return &Splat[(TY * SplatTexels + TX) * SplatLayerCount]; }

		// ---- chunks ---------------------------------------------------------------------------------------------
		int32 ChunkId(int32 CX, int32 CY) const { return CY * NumChunksSide + CX; }
		/** The block of vertices this chunk owns (disjoint across chunks, together they tile the grid). */
		FTerrainRect ChunkOwnedRect(int32 Id) const;
		/** The vertices the chunk mesh holds: (C+1)^2, clipped to the grid. */
		FTerrainRect ChunkRenderRect(int32 Id) const;
		/** Splat texels owned by the chunk: 2x its owned vertex block, clipped to the splat map. */
		FTerrainRect ChunkOwnedSplatRect(int32 Id) const;
		/** Vertex rect to the splat texel rect covering it ([2*X0, 2*X1) clipped to the map). */
		FTerrainRect VertexRectToSplatRect(const FTerrainRect& VertexRect) const;
		int32 OwnerChunkOfVertex(int32 X, int32 Y) const;
		/** Ids of chunks that OWN a vertex inside the rect (the rect is clipped to the grid). Ascending. */
		void ChunksOwningRect(const FTerrainRect& VertexRect, TArray<int32>& Out) const;
		/** Ids of chunks that own a splat texel inside the texel rect. Ascending. */
		void ChunksOwningSplatRect(const FTerrainRect& TexelRect, TArray<int32>& Out) const;
		/** Ids of chunks whose RENDER rect overlaps the vertex rect (a border vertex is rendered by up to four chunks). Ascending. */
		void ChunksRenderOverlappingRect(const FTerrainRect& VertexRect, TArray<int32>& Out) const;
		/** Min and max vertex height over the chunk's render rect. */
		void GetChunkZRange(int32 Id, float& OutMin, float& OutMax) const;

		// ---- hashes ---------------------------------------------------------------------------------------------
		/** FNV-1a 32 over the float bits (little-endian) of every height, row-major. */
		uint32 HeightFnv() const;
		/** FNV-1a 32 over the splat bytes. */
		uint32 SplatFnv() const;

		/** Row-major (Y * Width + X) heights. */
		TArray<float> Heights;
		/** Row-major RGBA8, 4 bytes per texel, channel = layer id; every texel sums to 255. */
		TArray<uint8> Splat;

	private:
		int32 HalfExtent = 0;
		int32 VertsPerSide = 0;
		int32 SplatTexels = 0;
		int32 ChunkSize = 64;
		int32 NumChunksSide = 0;
	};
}

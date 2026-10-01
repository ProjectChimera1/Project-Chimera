// Chimera terrain trial, plan C 3.7: copy-on-write undo and redo, one entry per stroke (pure C++, no UObjects).
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	/** What an undo or redo changed: the chunks restored and the union of their owned vertex and splat rects (for re-upload). */
	struct FTerrainEditDelta
	{
		TArray<int32> Chunks;
		FTerrainRect HeightRect;
		FTerrainRect SplatRect;

		bool Any() const { return Chunks.Num() > 0; }
	};

	/**
	 * Per chunk per stroke: the first touch snapshots the chunk's OWNED block of heights and splat texels (owned blocks are disjoint, so
	 * restoring one chunk never overwrites another's border vertex). EndStroke keeps only chunks whose bytes really changed and pushes
	 * {chunks, before, after}; a stroke that changed no byte pushes nothing (DW-143). Caps: 512 MiB or 1000 entries, oldest dropped
	 * (EditorHistory.cs:25-28). A new push clears the redo stack.
	 */
	class FTerrainUndo
	{
	public:
		FTerrainUndo(int64 InMaxBytes = 512ll * 1024 * 1024, int32 InMaxEntries = 1000) : MaxBytes(InMaxBytes), MaxEntries(InMaxEntries) {}

		/** Start collecting a stroke (discards any stroke left open). */
		void BeginStroke();
		bool IsStrokeOpen() const { return bStrokeOpen; }

		/**
		 * Snapshot, before they are written, every chunk owning a vertex of HeightRect or a splat texel of SplatRect (either may be empty).
		 * Chunks already snapshotted in this stroke are skipped. Starts a stroke implicitly if none is open.
		 */
		void Touch(const FTerrainHeightfield& HF, const FTerrainRect& HeightRect, const FTerrainRect& SplatRect);

		/** Close the stroke. Returns true when an entry was pushed (some byte changed). */
		bool EndStroke(const FTerrainHeightfield& HF);

		/** Restore the newest entry's before-state. False when there is nothing to undo or a stroke is open. */
		bool Undo(FTerrainHeightfield& HF, FTerrainEditDelta& OutDelta);
		/** Re-apply the newest undone entry's after-state. False when there is nothing to redo or a stroke is open. */
		bool Redo(FTerrainHeightfield& HF, FTerrainEditDelta& OutDelta);

		bool CanUndo() const { return UndoStack.Num() > 0; }
		bool CanRedo() const { return RedoStack.Num() > 0; }
		int32 NumUndo() const { return UndoStack.Num(); }
		int32 NumRedo() const { return RedoStack.Num(); }
		/** Bytes held by the undo and redo stacks. */
		int64 TotalBytes() const { return StackBytes; }
		/** Number of entries dropped by the caps since construction. */
		int32 NumEvicted() const { return Evicted; }

		void Clear();

	private:
		struct FChunkBlock
		{
			int32 ChunkId = 0;
			TArray<float> HeightsBefore;
			TArray<float> HeightsAfter;
			TArray<uint8> SplatBefore;
			TArray<uint8> SplatAfter;

			int64 Bytes() const
			{
				return static_cast<int64>(HeightsBefore.Num() + HeightsAfter.Num()) * sizeof(float) + SplatBefore.Num() + SplatAfter.Num();
			}
		};

		struct FEntry
		{
			TArray<FChunkBlock> Blocks;
			int64 Bytes() const;
		};

		static void CaptureHeights(const FTerrainHeightfield& HF, const FTerrainRect& R, TArray<float>& Out);
		static void CaptureSplat(const FTerrainHeightfield& HF, const FTerrainRect& R, TArray<uint8>& Out);
		static void RestoreHeights(FTerrainHeightfield& HF, const FTerrainRect& R, const TArray<float>& In);
		static void RestoreSplat(FTerrainHeightfield& HF, const FTerrainRect& R, const TArray<uint8>& In);
		bool Apply(FTerrainHeightfield& HF, FEntry& Entry, bool bUseAfter, FTerrainEditDelta& OutDelta);
		void EnforceCaps();

		int64 MaxBytes;
		int32 MaxEntries;
		bool bStrokeOpen = false;
		TArray<FChunkBlock> Open;
		TArray<FEntry> UndoStack;
		TArray<FEntry> RedoStack;
		int64 StackBytes = 0;
		int32 Evicted = 0;
	};
}

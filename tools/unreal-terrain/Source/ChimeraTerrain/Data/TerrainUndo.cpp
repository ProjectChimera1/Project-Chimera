// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Data/TerrainUndo.h"

namespace ChimeraTerrain
{
	int64 FTerrainUndo::FEntry::Bytes() const
	{
		int64 Sum = 0;
		for (const FChunkBlock& B : Blocks)
		{
			Sum += B.Bytes();
		}
		return Sum;
	}

	void FTerrainUndo::CaptureHeights(const FTerrainHeightfield& HF, const FTerrainRect& R, TArray<float>& Out)
	{
		Out.Reset();
		Out.Reserve(static_cast<int32>(R.Area()));
		for (int32 Y = R.Y0; Y < R.Y1; ++Y)
		{
			for (int32 X = R.X0; X < R.X1; ++X)
			{
				Out.Add(HF.GetHeight(X, Y));
			}
		}
	}

	void FTerrainUndo::CaptureSplat(const FTerrainHeightfield& HF, const FTerrainRect& R, TArray<uint8>& Out)
	{
		Out.Reset();
		Out.Reserve(static_cast<int32>(R.Area()) * SplatLayerCount);
		for (int32 Y = R.Y0; Y < R.Y1; ++Y)
		{
			for (int32 X = R.X0; X < R.X1; ++X)
			{
				const uint8* T = HF.SplatTexel(X, Y);
				for (int32 C = 0; C < SplatLayerCount; ++C)
				{
					Out.Add(T[C]);
				}
			}
		}
	}

	void FTerrainUndo::RestoreHeights(FTerrainHeightfield& HF, const FTerrainRect& R, const TArray<float>& In)
	{
		check(In.Num() == R.Area());
		int32 I = 0;
		for (int32 Y = R.Y0; Y < R.Y1; ++Y)
		{
			for (int32 X = R.X0; X < R.X1; ++X)
			{
				HF.Heights[Y * HF.Width() + X] = In[I++];
			}
		}
	}

	void FTerrainUndo::RestoreSplat(FTerrainHeightfield& HF, const FTerrainRect& R, const TArray<uint8>& In)
	{
		check(In.Num() == R.Area() * SplatLayerCount);
		int32 I = 0;
		for (int32 Y = R.Y0; Y < R.Y1; ++Y)
		{
			for (int32 X = R.X0; X < R.X1; ++X)
			{
				uint8* T = HF.SplatTexel(X, Y);
				for (int32 C = 0; C < SplatLayerCount; ++C)
				{
					T[C] = In[I++];
				}
			}
		}
	}

	void FTerrainUndo::BeginStroke()
	{
		Open.Reset();
		bStrokeOpen = true;
	}

	FTerrainUndo::FChunkBlock& FTerrainUndo::OpenBlock(int32 Id)
	{
		for (FChunkBlock& B : Open)
		{
			if (B.ChunkId == Id)
			{
				return B;
			}
		}
		FChunkBlock& Block = Open.AddDefaulted_GetRef();
		Block.ChunkId = Id;
		return Block;
	}

	void FTerrainUndo::Touch(const FTerrainHeightfield& HF, const FTerrainRect& HeightRect, const FTerrainRect& SplatRect)
	{
		if (!bStrokeOpen)
		{
			BeginStroke();
		}
		// Snapshot only the data a tick is about to write (C5 rework, P6): a height tick captures its chunks' heights, a paint tick their
		// splat texels. A chunk keeps one block per stroke; each array is captured once, on the first touch that writes it.
		TArray<int32> ChunkIds;
		HF.ChunksOwningRect(HeightRect, ChunkIds);
		for (const int32 Id : ChunkIds)
		{
			FChunkBlock& Block = OpenBlock(Id);
			if (!Block.bHeights)
			{
				CaptureHeights(HF, HF.ChunkOwnedRect(Id), Block.HeightsBefore);
				Block.bHeights = true;
			}
		}
		HF.ChunksOwningSplatRect(SplatRect, ChunkIds);
		for (const int32 Id : ChunkIds)
		{
			FChunkBlock& Block = OpenBlock(Id);
			if (!Block.bSplat)
			{
				CaptureSplat(HF, HF.ChunkOwnedSplatRect(Id), Block.SplatBefore);
				Block.bSplat = true;
			}
		}
	}

	bool FTerrainUndo::EndStroke(const FTerrainHeightfield& HF)
	{
		if (!bStrokeOpen)
		{
			return false;
		}
		bStrokeOpen = false;

		FEntry Entry;
		for (FChunkBlock& Block : Open)
		{
			// Keep only the arrays whose bytes changed (C5 rework, P6: a height stroke used to keep two unchanged 64 KB splat copies per
			// chunk). Compare bytes, not float values, so -0.0 vs 0.0 or NaN payloads count as changes (undo is exact to the bit).
			bool bHeightsChanged = false;
			if (Block.bHeights)
			{
				TArray<float> HeightsNow;
				CaptureHeights(HF, HF.ChunkOwnedRect(Block.ChunkId), HeightsNow);
				bHeightsChanged = HeightsNow.Num() != Block.HeightsBefore.Num()
					|| FMemory::Memcmp(HeightsNow.GetData(), Block.HeightsBefore.GetData(), HeightsNow.Num() * sizeof(float)) != 0;
				if (bHeightsChanged)
				{
					Block.HeightsAfter = MoveTemp(HeightsNow);
				}
				else
				{
					Block.HeightsBefore.Empty();
					Block.bHeights = false;
				}
			}
			bool bSplatChanged = false;
			if (Block.bSplat)
			{
				TArray<uint8> SplatNow;
				CaptureSplat(HF, HF.ChunkOwnedSplatRect(Block.ChunkId), SplatNow);
				bSplatChanged = SplatNow != Block.SplatBefore;
				if (bSplatChanged)
				{
					Block.SplatAfter = MoveTemp(SplatNow);
				}
				else
				{
					Block.SplatBefore.Empty();
					Block.bSplat = false;
				}
			}
			if (!bHeightsChanged && !bSplatChanged)
			{
				continue;
			}
			// Exact-size storage: the history holds no slack.
			Block.HeightsBefore.Shrink();
			Block.HeightsAfter.Shrink();
			Block.SplatBefore.Shrink();
			Block.SplatAfter.Shrink();
			Entry.Blocks.Add(MoveTemp(Block));
		}
		Open.Reset();

		if (Entry.Blocks.Num() == 0)
		{
			return false;
		}

		// A new edit invalidates everything that was undone.
		for (const FEntry& R : RedoStack)
		{
			StackBytes -= R.Bytes();
		}
		RedoStack.Reset();

		StackBytes += Entry.Bytes();
		UndoStack.Add(MoveTemp(Entry));
		EnforceCaps();
		return true;
	}

	void FTerrainUndo::EnforceCaps()
	{
		// Always keep the newest entry, even when it alone is over the byte cap.
		while (UndoStack.Num() > 1 && (UndoStack.Num() > MaxEntries || StackBytes > MaxBytes))
		{
			StackBytes -= UndoStack[0].Bytes();
			UndoStack.RemoveAt(0);
			++Evicted;
		}
	}

	bool FTerrainUndo::Apply(FTerrainHeightfield& HF, FEntry& Entry, bool bUseAfter, FTerrainEditDelta& OutDelta)
	{
		OutDelta = FTerrainEditDelta();
		for (const FChunkBlock& Block : Entry.Blocks)
		{
			// Restore only the arrays the block kept; the delta's rects cover exactly what was rewritten (heights drive the mesh and
			// collision re-upload, splat the texture re-upload).
			OutDelta.Chunks.Add(Block.ChunkId);
			if (Block.bHeights)
			{
				const FTerrainRect HR = HF.ChunkOwnedRect(Block.ChunkId);
				RestoreHeights(HF, HR, bUseAfter ? Block.HeightsAfter : Block.HeightsBefore);
				OutDelta.HeightRect.Union(HR);
			}
			if (Block.bSplat)
			{
				const FTerrainRect SR = HF.ChunkOwnedSplatRect(Block.ChunkId);
				RestoreSplat(HF, SR, bUseAfter ? Block.SplatAfter : Block.SplatBefore);
				OutDelta.SplatRect.Union(SR);
			}
		}
		return true;
	}

	bool FTerrainUndo::Undo(FTerrainHeightfield& HF, FTerrainEditDelta& OutDelta)
	{
		OutDelta = FTerrainEditDelta();
		if (bStrokeOpen || UndoStack.Num() == 0)
		{
			return false;
		}
		FEntry Entry = UndoStack.Pop();
		Apply(HF, Entry, /*bUseAfter*/ false, OutDelta);
		RedoStack.Add(MoveTemp(Entry));
		return true;
	}

	bool FTerrainUndo::Redo(FTerrainHeightfield& HF, FTerrainEditDelta& OutDelta)
	{
		OutDelta = FTerrainEditDelta();
		if (bStrokeOpen || RedoStack.Num() == 0)
		{
			return false;
		}
		FEntry Entry = RedoStack.Pop();
		Apply(HF, Entry, /*bUseAfter*/ true, OutDelta);
		UndoStack.Add(MoveTemp(Entry));
		return true;
	}

	void FTerrainUndo::Clear()
	{
		UndoStack.Reset();
		RedoStack.Reset();
		Open.Reset();
		bStrokeOpen = false;
		StackBytes = 0;
	}
}

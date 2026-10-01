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

	void FTerrainUndo::Touch(const FTerrainHeightfield& HF, const FTerrainRect& HeightRect, const FTerrainRect& SplatRect)
	{
		if (!bStrokeOpen)
		{
			BeginStroke();
		}
		TArray<int32> ChunkIds;
		HF.ChunksOwningRect(HeightRect, ChunkIds);
		TArray<int32> SplatChunkIds;
		HF.ChunksOwningSplatRect(SplatRect, SplatChunkIds);
		for (const int32 Id : SplatChunkIds)
		{
			ChunkIds.AddUnique(Id);
		}
		for (const int32 Id : ChunkIds)
		{
			bool bKnown = false;
			for (const FChunkBlock& B : Open)
			{
				if (B.ChunkId == Id)
				{
					bKnown = true;
					break;
				}
			}
			if (bKnown)
			{
				continue;
			}
			FChunkBlock& Block = Open.AddDefaulted_GetRef();
			Block.ChunkId = Id;
			CaptureHeights(HF, HF.ChunkOwnedRect(Id), Block.HeightsBefore);
			CaptureSplat(HF, HF.ChunkOwnedSplatRect(Id), Block.SplatBefore);
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
			TArray<float> HeightsNow;
			TArray<uint8> SplatNow;
			CaptureHeights(HF, HF.ChunkOwnedRect(Block.ChunkId), HeightsNow);
			CaptureSplat(HF, HF.ChunkOwnedSplatRect(Block.ChunkId), SplatNow);
			// Compare bytes, not float values, so -0.0 vs 0.0 or NaN payloads count as changes (undo is exact to the bit).
			const bool bHeightsSame = HeightsNow.Num() == Block.HeightsBefore.Num()
				&& FMemory::Memcmp(HeightsNow.GetData(), Block.HeightsBefore.GetData(), HeightsNow.Num() * sizeof(float)) == 0;
			const bool bSplatSame = SplatNow == Block.SplatBefore;
			if (bHeightsSame && bSplatSame)
			{
				continue;
			}
			Block.HeightsAfter = MoveTemp(HeightsNow);
			Block.SplatAfter = MoveTemp(SplatNow);
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
			const FTerrainRect HR = HF.ChunkOwnedRect(Block.ChunkId);
			const FTerrainRect SR = HF.ChunkOwnedSplatRect(Block.ChunkId);
			RestoreHeights(HF, HR, bUseAfter ? Block.HeightsAfter : Block.HeightsBefore);
			RestoreSplat(HF, SR, bUseAfter ? Block.SplatAfter : Block.SplatBefore);
			OutDelta.Chunks.Add(Block.ChunkId);
			OutDelta.HeightRect.Union(HR);
			OutDelta.SplatRect.Union(SR);
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

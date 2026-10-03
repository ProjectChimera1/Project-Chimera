// Project Chimera terrain trial (plan C scatter 3.1, 3.4, 3.5, 3.8, task S2). Original Chimera code.

#include "Data/TerrainScatterPalette.h"
#include "Data/TerrainScatterMath.h"

#if !defined(CHIMERA_SCATTER_STANDALONE)
#include "Misc/Parse.h"
#include "Misc/CString.h"
#endif

#include <cmath>
#include <cstring>

namespace ChimeraTerrain
{
	namespace
	{
		const FScatterParamInfo GScatterParamTable[] = {
#define CHIMERA_SCATTER_ROW(Name, Kind, Default) {#Name, EScatterParamKind::Kind, static_cast<int64>(Default)},
			CHIMERA_SCATTER_PARAMS(CHIMERA_SCATTER_ROW)
#undef CHIMERA_SCATTER_ROW
		};
		static_assert(sizeof(GScatterParamTable) / sizeof(GScatterParamTable[0]) == static_cast<size_t>(ScatterParamCount), "palette table size");

		/** Cell parameters: a candidate cell must divide every allowed tile size (16/32/64 fine, 40/80/160 coarse), so only 0.5, 1, 2 or 4 m. */
		bool IsCellParam(int32 Index)
		{
			switch (static_cast<EScatterParam>(Index))
			{
			case EScatterParam::GrassCellM:
			case EScatterParam::TussockCellM:
			case EScatterParam::FlowerCellM:
			case EScatterParam::NearCardCellM:
			case EScatterParam::TreeCellM:
			case EScatterParam::SaplingCellM:
			case EScatterParam::ShrubCellM:
			case EScatterParam::FernCellM:
			case EScatterParam::RockCellM:
				return true;
			default:
				return false;
			}
		}

		int32 FindParam(const char* Name)
		{
			for (int32 I = 0; I < ScatterParamCount; ++I)
			{
				if (std::strcmp(GScatterParamTable[I].Name, Name) == 0)
				{
					return I;
				}
			}
			return -1;
		}

		/** Append text to a bounded, NUL-terminated error buffer (truncates silently at the capacity). */
		void AppendError(char* Out, int32 Capacity, const char* A, const char* B, const char* C)
		{
			if (Out == nullptr || Capacity <= 0)
			{
				return;
			}
			int32 Len = static_cast<int32>(std::strlen(Out));
			const char* const Parts[3] = {A, B, C};
			for (const char* Part : Parts)
			{
				for (const char* P = Part; P != nullptr && *P != 0 && Len + 1 < Capacity; ++P)
				{
					Out[Len++] = *P;
				}
			}
			Out[Len] = 0;
		}

		/** Integer text "[-]digits" (whole string) -> value. */
		bool ParseInt64(const char* Text, int64& Out)
		{
			const char* P = Text;
			bool bNeg = false;
			if (*P == '-')
			{
				bNeg = true;
				++P;
			}
			if (*P == 0)
			{
				return false;
			}
			int64 V = 0;
			for (; *P != 0; ++P)
			{
				if (*P < '0' || *P > '9' || V > (int64(1) << 58))
				{
					return false;
				}
				V = V * 10 + (*P - '0');
			}
			Out = bNeg ? -V : V;
			return true;
		}
	}

	const FScatterParamInfo& ScatterParamInfo(EScatterParam Param)
	{
		return GScatterParamTable[static_cast<int32>(Param)];
	}

	void FScatterPalette::SetDefaults()
	{
		for (int32 I = 0; I < ScatterParamCount; ++I)
		{
			V[I] = GScatterParamTable[I].Default;
		}
	}

	// SCATTER_FP_BEGIN: configuration only (an override text is converted to the integer the table stores); never reached by the generator.
	EScatterSetResult FScatterPalette::SetByName(const char* Name, double Value)
	{
		const int32 Index = FindParam(Name);
		if (Index < 0)
		{
			return EScatterSetResult::UnknownName;
		}
		if (!(Value == Value) || Value < 0.0)
		{
			return EScatterSetResult::OutOfRange;
		}
		const FScatterParamInfo& Info = GScatterParamTable[Index];
		int64 Stored = 0;
		switch (Info.Kind)
		{
		case EScatterParamKind::Frac:
		case EScatterParamKind::Meters:
		case EScatterParamKind::Deg:
			if (Value > 4000.0)
			{
				return EScatterSetResult::OutOfRange;
			}
			Stored = static_cast<int64>(Value * 65536.0 + 0.5);
			break;
		case EScatterParamKind::Byte:
			if (Value > 255.0)
			{
				return EScatterSetResult::OutOfRange;
			}
			Stored = static_cast<int64>(Value + 0.5);
			break;
		case EScatterParamKind::Slope:
		{
			if (Value >= 89.9)
			{
				return EScatterSetResult::OutOfRange;
			}
			const double T = std::tan(Value * 3.14159265358979323846 / 180.0);
			Stored = static_cast<int64>(T * T * 4294967296.0 + 0.5);
			break;
		}
		}
		return SetRawByName(Name, Stored);
	}

	// Decimal text "[-]digits[.digits]" (the whole string) -> double (configuration only).
	static bool ParseDecimal(const char* Text, double& Out)
	{
		const char* P = Text;
		bool bNeg = false;
		if (*P == '-' || *P == '+')
		{
			bNeg = *P == '-';
			++P;
		}
		double V = 0.0;
		double Scale = 1.0;
		bool bDot = false;
		int32 Digits = 0;
		for (; *P != 0; ++P)
		{
			if (*P == '.' && !bDot)
			{
				bDot = true;
				continue;
			}
			if (*P < '0' || *P > '9' || Digits >= 15)
			{
				return false;
			}
			V = V * 10.0 + static_cast<double>(*P - '0');
			if (bDot)
			{
				Scale *= 10.0;
			}
			++Digits;
		}
		if (Digits == 0)
		{
			return false;
		}
		Out = (bNeg ? -V : V) / Scale;
		return true;
	}

	// A decimal override in the parameter's natural unit (configuration only).
	static EScatterSetResult SetByDecimalText(FScatterPalette& P, const char* Name, const char* Text)
	{
		double D = 0.0;
		if (!ParseDecimal(Text, D))
		{
			return EScatterSetResult::OutOfRange;
		}
		return P.SetByName(Name, D);
	}
	// SCATTER_FP_END

	EScatterSetResult FScatterPalette::SetRawByName(const char* Name, int64 Stored)
	{
		const int32 Index = FindParam(Name);
		if (Index < 0)
		{
			return EScatterSetResult::UnknownName;
		}
		if (Stored < 0 || Stored > (int64(1) << 40))
		{
			return EScatterSetResult::OutOfRange;
		}
		if (GScatterParamTable[Index].Kind == EScatterParamKind::Byte && Stored > 255)
		{
			return EScatterSetResult::OutOfRange;
		}
		if (IsCellParam(Index) && Stored != 32768 && Stored != 65536 && Stored != 131072 && Stored != 262144)
		{
			return EScatterSetResult::OutOfRange;
		}
		V[Index] = Stored;
		return EScatterSetResult::Ok;
	}

	int32 FScatterPalette::ApplyOverrides(const char* Spec, char* OutError, int32 ErrorCapacity)
	{
		if (OutError != nullptr && ErrorCapacity > 0)
		{
			OutError[0] = 0;
		}
		const FScatterPalette Before = *this;
		int32 Errors = 0;
		const char* P = Spec != nullptr ? Spec : "";
		while (*P != 0)
		{
			// One item up to the next comma, trimmed.
			const char* ItemEnd = P;
			while (*ItemEnd != 0 && *ItemEnd != ',')
			{
				++ItemEnd;
			}
			char Item[128];
			int32 N = 0;
			for (const char* Q = P; Q < ItemEnd && N + 1 < static_cast<int32>(sizeof(Item)); ++Q)
			{
				if (*Q != ' ' && *Q != '\t')
				{
					Item[N++] = *Q;
				}
			}
			Item[N] = 0;
			P = (*ItemEnd == ',') ? ItemEnd + 1 : ItemEnd;
			if (N == 0)
			{
				continue;
			}
			char* Eq = std::strchr(Item, '=');
			if (Eq == nullptr)
			{
				AppendError(OutError, ErrorCapacity, "bad item '", Item, "'; ");
				++Errors;
				continue;
			}
			*Eq = 0;
			const char* Name = Item;
			const char* Value = Eq + 1;
			EScatterSetResult R = EScatterSetResult::OutOfRange;
			if (*Value == '#')
			{
				int64 Raw = 0;
				if (ParseInt64(Value + 1, Raw))
				{
					R = SetRawByName(Name, Raw);
				}
			}
			else
			{
				R = SetByDecimalText(*this, Name, Value);
			}
			if (R == EScatterSetResult::UnknownName)
			{
				AppendError(OutError, ErrorCapacity, "unknown scatter param '", Name, "'; ");
				++Errors;
			}
			else if (R == EScatterSetResult::OutOfRange)
			{
				AppendError(OutError, ErrorCapacity, "scatter param '", Name, "' out of range or not a number; ");
				++Errors;
			}
		}
		if (Errors == 0)
		{
			if (const char* Rule = Validate())
			{
				AppendError(OutError, ErrorCapacity, "palette rule broken: ", Rule, "; ");
				++Errors;
			}
		}
		if (Errors > 0)
		{
			*this = Before;
		}
		return Errors;
	}

	const char* FScatterPalette::Validate() const
	{
		const EScatterParam Fine[] = {EScatterParam::GrassVergeRingM};
		const EScatterParam Coarse[] = {EScatterParam::TreeProbeM, EScatterParam::TreeBaseProbeM, EScatterParam::ShrubHedgeRingM};
		for (const EScatterParam Q : Fine)
		{
			if (Get(Q) > ScatterFineReachMaxQ16)
			{
				return "a fine-grid reach (GrassVergeRingM) exceeds 2 m, beyond the 2.5 m fine apron";
			}
		}
		for (const EScatterParam Q : Coarse)
		{
			if (Get(Q) > ScatterCoarseReachMaxQ16)
			{
				return "a coarse-grid reach (TreeProbeM, TreeBaseProbeM, ShrubHedgeRingM) exceeds 4.5 m, beyond the 5 m coarse apron";
			}
		}
		if (Get(EScatterParam::RockCellM) != Get(EScatterParam::TreeCellM))
		{
			return "RockCellM must equal TreeCellM (a rock is vetoed by the tree candidate of its own cell)";
		}
		for (int32 I = 0; I < ScatterParamCount; ++I)
		{
			if (std::strstr(GScatterParamTable[I].Name, "Period") != nullptr && V[I] < 65536)
			{
				return "a noise period is below 1 m";
			}
		}
		return nullptr;
	}

	uint64 FScatterPalette::ConfigFnv() const
	{
		uint64 H = Fnv64OffsetBasis;
		H = Fnv1a64U32(H, Seed);
		H = Fnv1a64U8(H, static_cast<uint32>(Level));
		// The mesh slot list of the level (plan C scatter 3.5).
		for (int32 M = 0; M < ScatterMeshCount; ++M)
		{
			const EScatterMesh Mesh = static_cast<EScatterMesh>(M);
			if (ScatterMeshExistsAtLevel(Mesh, Level))
			{
				H = Fnv1a64Chars(H, ScatterMeshName(Mesh));
				H = Fnv1a64U8(H, 0);
			}
		}
		H = Fnv1a64U32(H, static_cast<uint32>(ScatterParamCount));
		for (int32 I = 0; I < ScatterParamCount; ++I)
		{
			H = Fnv1a64Chars(H, GScatterParamTable[I].Name);
			H = Fnv1a64U8(H, 0);
			H = Fnv1a64U64(H, static_cast<uint64>(V[I]));
		}
		return H;
	}

// SCATTER_FP_BEGIN: the option block parses command-line text (configuration only); the generator never reads FScatterOptions.
#if !defined(CHIMERA_SCATTER_STANDALONE)
	namespace
	{
		bool ParseLayerMask(const FString& Csv, uint32& OutMask)
		{
			if (Csv.Equals(TEXT("all"), ESearchCase::IgnoreCase))
			{
				OutMask = (1u << ScatterLayerCount) - 1u;
				return true;
			}
			TArray<FString> Parts;
			Csv.ParseIntoArray(Parts, TEXT(","), true);
			uint32 Mask = 0;
			for (const FString& P : Parts)
			{
				bool bFound = false;
				for (int32 L = 0; L < ScatterLayerCount; ++L)
				{
					if (P.TrimStartAndEnd().Equals(ANSI_TO_TCHAR(ScatterLayerName(static_cast<EScatterLayer>(L))), ESearchCase::IgnoreCase))
					{
						Mask |= 1u << L;
						bFound = true;
					}
				}
				if (!bFound)
				{
					return false;
				}
			}
			OutMask = Mask;
			return Parts.Num() > 0;
		}
	}

	FString FScatterOptions::ParseMeshesSpec(const FString& Spec, TArray<FString>& Out)
	{
		Out.Init(FString(), ScatterMeshCount);
		FString Errors;
		TArray<FString> Items;
		Spec.ParseIntoArray(Items, TEXT(","), true);
		for (const FString& RawItem : Items)
		{
			FString Slot;
			FString Choice;
			if (!RawItem.Split(TEXT("="), &Slot, &Choice))
			{
				Errors += FString::Printf(TEXT("ScatterMeshes item '%s' is not Slot=Choice; "), *RawItem);
				continue;
			}
			Slot.TrimStartAndEndInline();
			Choice.TrimStartAndEndInline();
			int32 Found = INDEX_NONE;
			for (int32 M = 0; M < ScatterMeshCount; ++M)
			{
				if (Slot.Equals(UTF8_TO_TCHAR(ScatterMeshName(static_cast<EScatterMesh>(M))), ESearchCase::CaseSensitive))
				{
					Found = M;
				}
			}
			if (Found == INDEX_NONE)
			{
				Errors += FString::Printf(TEXT("ScatterMeshes: unknown slot '%s'; "), *Slot);
				continue;
			}
			const bool bLevel = Choice == TEXT("L0") || Choice == TEXT("L1");
			const FString Prefix = FString(TEXT("L1/")) + Slot;
			const FString Variant = Choice.StartsWith(TEXT("L1/")) ? Choice.Mid(3) : FString();
			bool bVariantOk = Choice.StartsWith(Prefix + TEXT("__")) && Choice.Len() > Prefix.Len() + 2;
			for (const TCHAR Ch : Variant)
			{
				bVariantOk &= FChar::IsAlnum(Ch) || Ch == TEXT('_');
			}
			if (!bLevel && !bVariantOk)
			{
				Errors += FString::Printf(TEXT("ScatterMeshes: bad choice '%s' for %s (L0, L1 or L1/%s__<variant>); "), *Choice, *Slot, *Slot);
				continue;
			}
			if (!Out[Found].IsEmpty())
			{
				// A slot given twice is an error (the first choice is kept), never a silent last-one-wins.
				Errors += FString::Printf(TEXT("ScatterMeshes: slot %s given twice; "), *Slot);
				continue;
			}
			Out[Found] = Choice;
		}
		return Errors;
	}

	FString FScatterOptions::ParseCullSpec(const FString& Spec, TArray<double>& Out)
	{
		Out.Init(0.0, ScatterMeshCount);
		FString Errors;
		TArray<FString> Items;
		Spec.ParseIntoArray(Items, TEXT(","), true);
		for (const FString& RawItem : Items)
		{
			FString Slot;
			FString Value;
			if (!RawItem.Split(TEXT("="), &Slot, &Value))
			{
				Errors += FString::Printf(TEXT("ScatterCullM item '%s' is not Slot=metres; "), *RawItem);
				continue;
			}
			Slot.TrimStartAndEndInline();
			Value.TrimStartAndEndInline();
			int32 Found = INDEX_NONE;
			for (int32 M = 0; M < ScatterMeshCount; ++M)
			{
				if (Slot.Equals(UTF8_TO_TCHAR(ScatterMeshName(static_cast<EScatterMesh>(M))), ESearchCase::CaseSensitive))
				{
					Found = M;
				}
			}
			if (Found == INDEX_NONE)
			{
				Errors += FString::Printf(TEXT("ScatterCullM: unknown slot '%s'; "), *Slot);
				continue;
			}
			const double Metres = Value.IsNumeric() ? FCString::Atod(*Value) : -1.0;
			if (!(Metres >= 1.0 && Metres <= 1000.0))
			{
				Errors += FString::Printf(TEXT("ScatterCullM: bad end '%s' for %s (1..1000 m); "), *Value, *Slot);
				continue;
			}
			if (Out[Found] > 0.0)
			{
				// A slot given twice is an error (the first end is kept), never a silent last-one-wins.
				Errors += FString::Printf(TEXT("ScatterCullM: slot %s given twice; "), *Slot);
				continue;
			}
			Out[Found] = Metres;
		}
		return Errors;
	}

	FScatterOptions FScatterOptions::FromCommandLine(const TCHAR* Cmd)
	{
		FScatterOptions O;
		O.MeshChoice.Init(FString(), ScatterMeshCount);
		O.CullEndM.Init(0.0, ScatterMeshCount);
		FString Script;
		FParse::Value(Cmd, TEXT("ChimeraTerrainScript="), Script, false);
		const bool bScripted = !Script.IsEmpty();
		FString Explicit;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatter="), Explicit, false))
		{
			O.bEnabled = FCString::Atoi(*Explicit) != 0 || Explicit.Equals(TEXT("true"), ESearchCase::IgnoreCase);
		}
		else
		{
			O.bEnabled = !bScripted;
		}

		uint32 Seed = 0;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterSeed="), Seed))
		{
			O.Palette.Seed = Seed;
		}
		FString Level;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterLevel="), Level, false))
		{
			if (Level.Equals(TEXT("L1"), ESearchCase::IgnoreCase))
			{
				O.Palette.Level = EScatterLevel::L1;
			}
			else if (Level.Equals(TEXT("L0"), ESearchCase::IgnoreCase))
			{
				O.Palette.Level = EScatterLevel::L0;
			}
			else
			{
				O.ParamsError += FString::Printf(TEXT("bad ScatterLevel '%s' (L0 or L1; kept L0); "), *Level);
			}
		}
		float Density = 1.0f;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterDensity="), Density))
		{
			Density = FMath::Clamp(Density, 0.0f, 1.0f);
			O.Palette.SetByName("Density", static_cast<double>(Density));
		}
		FParse::Value(Cmd, TEXT("ChimeraTerrainScatterParams="), O.ParamsSpec, false);
		if (!O.ParamsSpec.IsEmpty())
		{
			// All or nothing: on any error the palette keeps its values (defaults plus Seed, Level, Density).
			const FTCHARToUTF8 Spec(*O.ParamsSpec);
			char Error[1024];
			if (O.Palette.ApplyOverrides(Spec.Get(), Error, static_cast<int32>(sizeof(Error))) > 0)
			{
				O.ParamsError += FString(UTF8_TO_TCHAR(Error));
				O.ParamsError += TEXT("(ScatterParams ignored); ");
			}
		}

		FParse::Value(Cmd, TEXT("ChimeraTerrainScatterDuringStroke="), O.DuringStrokeMs);
		O.DuringStrokeMs = FMath::Clamp(O.DuringStrokeMs, 0, 5000);
		FParse::Value(Cmd, TEXT("ChimeraTerrainScatterCasterDuringStroke="), O.CasterDuringStrokeMs);
		O.CasterDuringStrokeMs = FMath::Clamp(O.CasterDuringStrokeMs, 0, 5000);
		FParse::Value(Cmd, TEXT("ChimeraTerrainScatterBudgetMs="), O.BudgetMs);
		O.BudgetMs = FMath::Clamp(O.BudgetMs, 0.05f, 50.0f);
		FParse::Value(Cmd, TEXT("ChimeraTerrainScatterLoadBudgetMs="), O.LoadBudgetMs);
		O.LoadBudgetMs = FMath::Clamp(O.LoadBudgetMs, 0.05f, 100.0f);
		FString Predict;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterPredict="), Predict, false))
		{
			TArray<FString> Parts;
			Predict.ParseIntoArray(Parts, TEXT(","), true);
			if (Parts.Num() == 3 && Parts[0].IsNumeric() && Parts[1].IsNumeric() && Parts[2].IsNumeric())
			{
				O.PredictA = FMath::Max(0.0, FCString::Atod(*Parts[0]));
				O.PredictB = FMath::Max(0.0, FCString::Atod(*Parts[1]));
				O.PredictC = FMath::Max(0.0, FCString::Atod(*Parts[2]));
			}
			else
			{
				O.ParamsError += FString::Printf(TEXT("ScatterPredict '%s' is not a,b,c (kept the defaults); "), *Predict);
			}
		}
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterThreads="), O.Threads) && (O.Threads < 0 || O.Threads > 4))
		{
			O.ParamsError += FString::Printf(TEXT("ScatterThreads %d outside 0..4 (clamped); "), O.Threads);
		}
		O.Threads = FMath::Clamp(O.Threads, 0, 4);
		FString Layers;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterLayers="), Layers, false))
		{
			if (!ParseLayerMask(Layers, O.LayerMask))
			{
				O.ParamsError += FString::Printf(TEXT("bad ScatterLayers '%s'; "), *Layers);
			}
		}
		FParse::Value(Cmd, TEXT("ChimeraTerrainScatterGovernor="), O.Governor);
		O.Governor = FMath::Clamp(O.Governor, 0, 6);
		FString Mobility;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterMobility="), Mobility, false))
		{
			Mobility = Mobility.ToLower();
			if (Mobility == TEXT("static") || Mobility == TEXT("movable") || Mobility == TEXT("stationary"))
			{
				O.Mobility = Mobility;
			}
			else
			{
				O.ParamsError += FString::Printf(TEXT("bad ScatterMobility '%s' (kept stationary); "), *Mobility);
			}
		}
		int32 GrassNanite = 1;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterGrassNanite="), GrassNanite))
		{
			O.bGrassNanite = GrassNanite != 0;
		}
		FString Apply;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterApply="), Apply, false))
		{
			if (Apply.Equals(TEXT("clear"), ESearchCase::IgnoreCase) || Apply.Equals(TEXT("diff"), ESearchCase::IgnoreCase))
			{
				O.ApplyMode = Apply.ToLower();
			}
			else
			{
				O.ParamsError += FString::Printf(TEXT("bad ScatterApply '%s' (kept diff); "), *Apply);
			}
		}
		int32 Fine = 32;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterFineTileM="), Fine))
		{
			if (Fine == 16 || Fine == 32 || Fine == 64)
			{
				O.FineTileM = Fine;
			}
			else
			{
				O.ParamsError += FString::Printf(TEXT("bad ScatterFineTileM %d (16, 32 or 64; kept 32); "), Fine);
			}
		}
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterMeshes="), O.MeshesSpec, false))
		{
			O.ParamsError += ParseMeshesSpec(O.MeshesSpec, O.MeshChoice);
		}
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterCullM="), O.CullSpec, false))
		{
			O.ParamsError += ParseCullSpec(O.CullSpec, O.CullEndM);
		}
		int32 Coarse = 80;
		if (FParse::Value(Cmd, TEXT("ChimeraTerrainScatterCoarseTileM="), Coarse))
		{
			if (Coarse == 40 || Coarse == 80 || Coarse == 160)
			{
				O.CoarseTileM = Coarse;
			}
			else
			{
				O.ParamsError += FString::Printf(TEXT("bad ScatterCoarseTileM %d (40, 80 or 160; kept 80); "), Coarse);
			}
		}
		return O;
	}
#endif
// SCATTER_FP_END
}

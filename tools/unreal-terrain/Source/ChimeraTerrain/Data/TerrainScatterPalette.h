// Project Chimera terrain trial (plan C scatter 3.1, 3.4, 3.5, 3.8, task S2). Original Chimera code.
// The scatter palette: every constant of the placement rules of plan C scatter 3.4 as one named, integer-valued table (a look round changes
// numbers here, never code), its overrides ("Name=Value,..."), the config_fnv fold, and the option block `-ChimeraTerrainScatter<Name>=`.
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainScatterTypes.h"
#include "Data/TerrainLookShared.h"

namespace ChimeraTerrain
{
	/** How a palette value is stored and how an override text is converted. */
	enum class EScatterParamKind : uint8
	{
		/** Q16 fraction or ratio (65536 = 1.0). */
		Frac,
		/** Q16 metres. */
		Meters,
		/** A splat weight 0..255. */
		Byte,
		/** A slope threshold tan^2(theta) * 2^32 (an override is given in degrees). */
		Slope,
		/** Q16 degrees (tilt maxima). */
		Deg
	};

	// SCATTER_FP_BEGIN: compile-time only. ScQ turns a decimal literal of the table into its Q16 integer while the compiler folds the constant
	// (constexpr, so never evaluated at run time and unaffected by /fp:fast): v * 65536 is an exact power-of-two scaling, + 0.5 then truncation.
	// The generator only ever sees the resulting integers; the static_asserts below pin a few of them.
	/** Compile-time Q16 of a decimal constant. */
	constexpr int64 ScQ(double V)
	{
		return static_cast<int64>(V * 65536.0 + 0.5);
	}
	// SCATTER_FP_END
	static_assert(ScQ(1.0) == 65536 && ScQ(0.65) == 42598 && ScQ(0.5) == 32768 && ScQ(1.5) == 98304 && ScQ(0.0005) == 33, "ScQ folds to exact Q16 integers");

	/**
	 * The parameter table: X(Name, Kind, Default). Order is the hashing order of config_fnv; never reorder, only append.
	 * Slope defaults are the literals tan^2(theta) * 2^32 (checked against Python by test_scatter_constants.py); the 35-degree grass limit
	 * follows the ground material through TerrainLookShared.h (RockCos).
	 *
	 * S2 tuning against plan C scatter 3.4 (numbers, never code; every one moves config_fnv on purpose, S6 may tune again): the default Seed is
	 * 1853059 (a scan of 3,000 seeds for the plan's coverage ranges: woodland 16.0 % of the interior, 76.7 % of the border ring, drift 9.2 %);
	 * FlowerCellM 1 -> 0.5, TreeCoreP 0.35 -> 0.50, ShrubWoodP 0.35 -> 0.45, RockBoulderP 0.01 -> 0.20 with the Fr ramp 0.80..0.95 -> 0.65..0.85,
	 * so every class of a flat all-grass E = 160 map holds at least half the plan's EST count (flowers 6.0k, trees 1.0k, shrubs + saplings 1.8k,
	 * rocks 124, grass 222k, near cards 62k at L1, tussocks 4.6k, ferns 7.6k).
	 */
#define CHIMERA_SCATTER_PARAMS(X) \
	/* ---- global factors (plan C scatter 3.4 "Common factors") */ \
	X(Density, Frac, ScQ(1.0)) \
	X(GLo, Byte, 96) \
	X(GHi, Byte, 224) \
	X(E3DirtLo, Byte, 32) \
	X(E3DirtHi, Byte, 128) \
	X(E3RockLo, Byte, 32) \
	X(E3RockHi, Byte, 112) \
	X(E3SnowLo, Byte, 16) \
	X(E3SnowHi, Byte, 64) \
	X(SgLo, Slope, 933909852ll) \
	X(SgHi, Slope, LookShared::RockSlopeThr) \
	X(StLo, Slope, 687096722ll) \
	X(StHi, Slope, 1431655765ll) \
	X(HgLo, Meters, ScQ(80.0)) \
	X(HgHi, Meters, ScQ(110.0)) \
	X(HtLo, Meters, ScQ(40.0)) \
	X(HtHi, Meters, ScQ(60.0)) \
	/* ---- fields (periods in metres, value noise on the world lattice) */ \
	X(WPeriod1M, Meters, ScQ(96.0)) \
	X(WPeriod2M, Meters, ScQ(37.0)) \
	X(WWeight1, Frac, ScQ(0.65)) \
	X(WWarpM, Meters, ScQ(30.0)) \
	X(BorderBias, Frac, ScQ(0.35)) \
	X(BorderLoM, Meters, ScQ(118.0)) \
	X(BorderHiM, Meters, ScQ(140.0)) \
	X(MPeriod1M, Meters, ScQ(60.0)) \
	X(MPeriod2M, Meters, ScQ(23.0)) \
	X(MWeight1, Frac, ScQ(0.65)) \
	X(TPeriodM, Meters, ScQ(9.0)) \
	X(TWarpM, Meters, ScQ(4.0)) \
	X(FPeriod1M, Meters, ScQ(18.0)) \
	X(FPeriod2M, Meters, ScQ(7.0)) \
	X(FWeight1, Frac, ScQ(0.65)) \
	X(FcPeriodM, Meters, ScQ(30.0)) \
	X(CPeriodM, Meters, ScQ(120.0)) \
	X(KCellM, Meters, ScQ(32.0)) \
	X(KP, Frac, ScQ(0.20)) \
	X(KRadiusLoM, Meters, ScQ(6.0)) \
	X(KRadiusHiM, Meters, ScQ(14.0)) \
	X(FsPeriodM, Meters, ScQ(9.0)) \
	X(FhPeriodM, Meters, ScQ(40.0)) \
	X(FrPeriodM, Meters, ScQ(25.0)) \
	/* ---- grass: fine, 0.5 m cell */ \
	X(GrassCellM, Meters, ScQ(0.5)) \
	X(GrassJitter, Frac, ScQ(1.0)) \
	X(GrassScaleLo, Frac, ScQ(0.8)) \
	X(GrassScaleHi, Frac, ScQ(1.25)) \
	X(GrassZLo, Frac, ScQ(0.7)) \
	X(GrassZHi, Frac, ScQ(1.3)) \
	X(GrassAlign, Frac, ScQ(0.5)) \
	X(GrassSinkM, Meters, ScQ(0.02)) \
	X(GrassBaseP, Frac, ScQ(0.80)) \
	X(GrassDryMin, Frac, ScQ(0.65)) \
	X(GrassForestCut, Frac, ScQ(0.6)) \
	X(GrassForestLo, Frac, ScQ(0.62)) \
	X(GrassForestHi, Frac, ScQ(0.75)) \
	X(GrassTierSplit, Frac, ScQ(0.55)) \
	X(GrassVergeZ, Frac, ScQ(0.6)) \
	X(GrassVergeRingM, Meters, ScQ(1.5)) \
	X(GrassVergeRing, Byte, 64) \
	X(GrassVergeOwn, Byte, 32) \
	/* ---- tussock: fine, 2 m cell */ \
	X(TussockCellM, Meters, ScQ(2.0)) \
	X(TussockJitter, Frac, ScQ(1.0)) \
	X(TussockScaleLo, Frac, ScQ(0.9)) \
	X(TussockScaleHi, Frac, ScQ(1.4)) \
	X(TussockAlign, Frac, ScQ(0.4)) \
	X(TussockSinkM, Meters, ScQ(0.03)) \
	X(TussockIslandMin, Frac, ScQ(0.64)) \
	X(TussockIslandP, Frac, ScQ(0.60)) \
	X(TussockBgP, Frac, ScQ(0.02)) \
	X(TussockDryMin, Frac, ScQ(0.5)) \
	X(TussockForestP, Frac, ScQ(0.30)) \
	X(TussockForestLo, Frac, ScQ(0.50)) \
	X(TussockForestPk, Frac, ScQ(0.58)) \
	X(TussockForestHi, Frac, ScQ(0.66)) \
	/* ---- flower: fine, 0.5 m cell (plan 1 m; S2 tuning: the plan's flat-map EST of ~8k flowers needs the finer lattice) */ \
	X(FlowerCellM, Meters, ScQ(0.5)) \
	X(FlowerJitter, Frac, ScQ(1.0)) \
	X(FlowerScaleLo, Frac, ScQ(1.2)) \
	X(FlowerScaleHi, Frac, ScQ(1.8)) \
	X(FlowerAlign, Frac, ScQ(0.3)) \
	X(FlowerSinkM, Meters, ScQ(0.01)) \
	X(FlowerBaseP, Frac, ScQ(0.60)) \
	X(FlowerDriftLo, Frac, ScQ(0.70)) \
	X(FlowerDriftHi, Frac, ScQ(0.85)) \
	X(FlowerGLo, Byte, 200) \
	X(FlowerGHi, Byte, 235) \
	X(FlowerSlopeLo, Slope, 701098398ll) \
	X(FlowerSlopeHi, Slope, 1677020262ll) \
	X(FlowerForestLo, Frac, ScQ(0.55)) \
	X(FlowerForestHi, Frac, ScQ(0.65)) \
	X(FlowerYellowTo, Frac, ScQ(0.50)) \
	X(FlowerWhiteTo, Frac, ScQ(0.80)) \
	X(FlowerNextP, Frac, ScQ(0.15)) \
	/* ---- near card (L1 only): fine, 1 m cell */ \
	X(NearCardCellM, Meters, ScQ(1.0)) \
	X(NearCardJitter, Frac, ScQ(1.0)) \
	X(NearCardScaleLo, Frac, ScQ(2.2)) \
	X(NearCardScaleHi, Frac, ScQ(2.8)) \
	X(NearCardAlign, Frac, ScQ(0.6)) \
	X(NearCardSinkM, Meters, ScQ(0.02)) \
	X(NearCardBaseP, Frac, ScQ(0.60)) \
	/* ---- tree: coarse, 4 m cell, jitter 0.5 (trunks at least 2 m apart) */ \
	X(TreeCellM, Meters, ScQ(4.0)) \
	X(TreeJitter, Frac, ScQ(0.5)) \
	X(TreeBroadScaleLo, Frac, ScQ(0.9)) \
	X(TreeBroadScaleHi, Frac, ScQ(1.2)) \
	X(TreeConiferScaleLo, Frac, ScQ(0.85)) \
	X(TreeConiferScaleHi, Frac, ScQ(1.15)) \
	X(TreeLoneScale, Frac, ScQ(1.15)) \
	X(TreeSinkM, Meters, ScQ(0.05)) \
	X(TreeCoreP, Frac, ScQ(0.50)) \
	X(TreeCoreW, Frac, ScQ(0.70)) \
	X(TreeEdgeP, Frac, ScQ(0.10)) \
	X(TreeEdgeW, Frac, ScQ(0.62)) \
	X(TreeGroveP, Frac, ScQ(0.25)) \
	X(TreeLoneP, Frac, ScQ(0.002)) \
	X(TrunkDirt, Byte, 32) \
	X(TrunkRock, Byte, 64) \
	X(TreeProbeDirt, Byte, 64) \
	X(TreeProbeM, Meters, ScQ(4.0)) \
	X(TreeBroadSnow, Byte, 24) \
	X(TreeConiferSnow, Byte, 150) \
	X(TreeConiferHalfSnow, Byte, 60) \
	X(TreeConiferCLo, Frac, ScQ(0.40)) \
	X(TreeConiferCHi, Frac, ScQ(0.70)) \
	X(TreeConiferZLo, Meters, ScQ(20.0)) \
	X(TreeConiferZHi, Meters, ScQ(50.0)) \
	X(TreeConiferZWeight, Frac, ScQ(0.5)) \
	X(TreeBaseProbeM, Meters, ScQ(0.8)) \
	/* ---- sapling: coarse, 4 m cell, own stream, shows TreeBroadA at a small scale */ \
	X(SaplingCellM, Meters, ScQ(4.0)) \
	X(SaplingJitter, Frac, ScQ(0.5)) \
	X(SaplingScaleLo, Frac, ScQ(0.25)) \
	X(SaplingScaleHi, Frac, ScQ(0.40)) \
	X(SaplingP, Frac, ScQ(0.30)) \
	X(SaplingLo, Frac, ScQ(0.45)) \
	X(SaplingPk, Frac, ScQ(0.55)) \
	X(SaplingHi, Frac, ScQ(0.62)) \
	X(SaplingFringeP, Frac, ScQ(0.5)) \
	X(SaplingFringeIn, Frac, ScQ(0.7)) \
	X(SaplingFringeOut, Frac, ScQ(1.2)) \
	/* ---- shrub: coarse, 2 m cell */ \
	X(ShrubCellM, Meters, ScQ(2.0)) \
	X(ShrubJitter, Frac, ScQ(1.0)) \
	X(ShrubScaleLo, Frac, ScQ(0.8)) \
	X(ShrubScaleHi, Frac, ScQ(1.6)) \
	X(ShrubAlign, Frac, ScQ(0.3)) \
	X(ShrubSinkM, Meters, ScQ(0.08)) \
	X(ShrubWoodP, Frac, ScQ(0.45)) \
	X(ShrubWoodLo, Frac, ScQ(0.50)) \
	X(ShrubWoodPk, Frac, ScQ(0.58)) \
	X(ShrubWoodHi, Frac, ScQ(0.64)) \
	X(ShrubFsP, Frac, ScQ(0.08)) \
	X(ShrubFsLo, Frac, ScQ(0.75)) \
	X(ShrubFsHi, Frac, ScQ(0.90)) \
	X(ShrubHedgeP, Frac, ScQ(0.25)) \
	X(ShrubHedgeRingM, Meters, ScQ(3.0)) \
	X(ShrubHedgeLo, Byte, 48) \
	X(ShrubHedgePk, Byte, 96) \
	X(ShrubHedgeHi, Byte, 160) \
	X(ShrubHedgeOwnLo, Byte, 8) \
	X(ShrubHedgeOwnHi, Byte, 24) \
	X(ShrubFhLo, Frac, ScQ(0.50)) \
	X(ShrubFhHi, Frac, ScQ(0.70)) \
	X(ShrubRockLo, Byte, 32) \
	X(ShrubRockHi, Byte, 128) \
	X(ShrubSnowLo, Byte, 16) \
	X(ShrubSnowHi, Byte, 48) \
	X(ShrubZLo, Meters, ScQ(55.0)) \
	X(ShrubZHi, Meters, ScQ(85.0)) \
	/* ---- fern: coarse, 2 m cell */ \
	X(FernCellM, Meters, ScQ(2.0)) \
	X(FernJitter, Frac, ScQ(1.0)) \
	X(FernScaleLo, Frac, ScQ(1.2)) \
	X(FernScaleHi, Frac, ScQ(1.8)) \
	X(FernAlign, Frac, ScQ(0.4)) \
	X(FernSinkM, Meters, ScQ(0.03)) \
	X(FernP, Frac, ScQ(0.80)) \
	X(FernWLo, Frac, ScQ(0.55)) \
	X(FernWHi, Frac, ScQ(0.70)) \
	X(FernSlopeLo, Slope, 1431655765ll) \
	X(FernSlopeHi, Slope, 3024035754ll) \
	/* ---- rock: coarse, 4 m cell */ \
	X(RockCellM, Meters, ScQ(4.0)) \
	X(RockJitter, Frac, ScQ(1.0)) \
	X(RockScaleLo, Frac, ScQ(0.6)) \
	X(RockScaleHi, Frac, ScQ(1.8)) \
	X(RockAlign, Frac, ScQ(0.8)) \
	X(RockTiltDeg, Deg, 8 * 65536ll) \
	X(RockSinkFrac, Frac, ScQ(0.25)) \
	X(RockNominalHM, Meters, ScQ(1.2)) \
	X(RockPaintP, Frac, ScQ(0.60)) \
	X(RockBandLo, Byte, 48) \
	X(RockBandPk, Byte, 112) \
	X(RockBandHi, Byte, 176) \
	X(RockSnowLo, Byte, 64) \
	X(RockSnowHi, Byte, 160) \
	X(RockSlopeP, Frac, ScQ(0.15)) \
	X(RockSlopeLo, Slope, 933909852ll) \
	X(RockSlopeHi, Slope, 3024035754ll) \
	X(RockBoulderP, Frac, ScQ(0.20)) \
	X(RockFrLo, Frac, ScQ(0.65)) \
	X(RockFrHi, Frac, ScQ(0.85)) \
	/* ---- appended in the S2 fix round (append only): the warp-noise periods of W and T, formerly code constants */ \
	X(WWarpPeriodM, Meters, ScQ(64.0)) \
	X(TWarpPeriodM, Meters, ScQ(16.0))

	enum class EScatterParam : int32
	{
#define CHIMERA_SCATTER_ENUM(Name, Kind, Default) Name,
		CHIMERA_SCATTER_PARAMS(CHIMERA_SCATTER_ENUM)
#undef CHIMERA_SCATTER_ENUM
		Count
	};
	constexpr int32 ScatterParamCount = static_cast<int32>(EScatterParam::Count);

	/** Name and kind of one parameter (ANSI name, stable). */
	struct FScatterParamInfo
	{
		const char* Name;
		EScatterParamKind Kind;
		int64 Default;
	};
	const FScatterParamInfo& ScatterParamInfo(EScatterParam Param);

	/** Result of FScatterPalette::SetByName. */
	enum class EScatterSetResult : uint8
	{
		Ok,
		UnknownName,
		OutOfRange
	};

	/**
	 * The reach bounds Validate enforces (Q16 metres): every read a candidate makes away from its own position must stay inside its grid's dirty-tile
	 * apron (ScatterApronQ16: 2.5 m fine, 5 m coarse, which already include the bilinear and cell-corner reach) with 0.5 m to spare, and inside the
	 * snapshot apron. Fine grid: the grass verge ring. Coarse grid: the trunk probes, the base probes and the hedge ring.
	 */
	constexpr int64 ScatterFineReachMaxQ16 = 2 * 65536;
	constexpr int64 ScatterCoarseReachMaxQ16 = 4 * 65536 + 32768;

	/**
	 * The palette: the seed, the asset level and the parameter values. A pure value; the generator takes it by const reference and never looks
	 * at anything else for its constants. config_fnv folds all of it (plan C scatter 3.5).
	 */
	struct FScatterPalette
	{
		uint32 Seed = 1853059u;
		EScatterLevel Level = EScatterLevel::L0;
		int64 V[ScatterParamCount];

		FScatterPalette() { SetDefaults(); }
		void SetDefaults();

		int64 Get(EScatterParam P) const { return V[static_cast<int32>(P)]; }

		// SCATTER_FP_BEGIN: configuration only (override text -> the stored integer); the generator never calls these.
		/**
		 * Set one value by name from a decimal number in the parameter's natural unit (fraction, metres, byte, degrees for a slope).
		 * Cell sizes must be 0.5, 1, 2 or 4 m (they must divide every allowed tile size). Configuration only; never called by the generator.
		 * A slope given in degrees converts with std::tan at run time, which a /fp:fast build may round differently: any run that is compared
		 * across builds (SX16) gives slopes in the raw form of ApplyOverrides instead.
		 */
		EScatterSetResult SetByName(const char* Name, double Value);
		// SCATTER_FP_END
		/** Set the stored integer itself (Q16, byte, or tan^2 * 2^32 for a slope); the same cell-size rule as SetByName. */
		EScatterSetResult SetRawByName(const char* Name, int64 Stored);

		/**
		 * Apply "Name=Value,Name=Value" (the ScatterParams text). A value starting with '#' is the raw stored integer (SetRawByName: build
		 * independent, the form for slopes in cross-build runs); anything else is a decimal in the natural unit (SetByName). Validate runs after
		 * the last item. On any error the palette is left exactly as it was and the errors are written to OutError (NUL-terminated, truncated
		 * to ErrorCapacity); returns the number of errors (0 = applied).
		 */
		int32 ApplyOverrides(const char* Spec, char* OutError, int32 ErrorCapacity);

		/**
		 * Rules a palette must hold for the generator's invariances: reach parameters inside the aprons (ScatterFineReachMaxQ16,
		 * ScatterCoarseReachMaxQ16), RockCellM == TreeCellM (a rock is vetoed by the tree candidate of its own cell), every noise period >= 1 m.
		 * Returns nullptr when valid, else a static message naming the first broken rule.
		 */
		const char* Validate() const;

		/** The hash that tells two scatter configurations apart: seed, level and its mesh slot list, every parameter name and value. */
		uint64 ConfigFnv() const;
	};

#if !defined(CHIMERA_SCATTER_STANDALONE)
	/**
	 * Scatter options, parsed from the command line (plan C scatter 3.8). Each one is `-ChimeraTerrainScatter<Name>=`; the plain
	 * `-ChimeraTerrainScatter=0|1` defaults to 1 without `-ChimeraTerrainScript` and 0 with it. Only Seed, Level, Density and Params change the
	 * records (they live in Palette and so in config_fnv); everything else changes timing or drawing.
	 */
	struct FScatterOptions
	{
		bool bEnabled = false;
		FScatterPalette Palette;
		/**
		 * Raw `ScatterParams` text, and every option error (bad params, unknown layer, level, tile size, mobility or apply value): empty when
		 * fine. An invalid option keeps its default, and the owner logs ParamsError as an error when scatter was requested.
		 */
		FString ParamsSpec;
		FString ParamsError;
		/** Fine tile ms between dispatches of one dirty tile during a stroke (0 = stroke end only). */
		int32 DuringStrokeMs = 150;
		/** Same for coarse (caster) tiles; 0 = stroke end only. */
		int32 CasterDuringStrokeMs = 0;
		// SCATTER_FP_BEGIN: apply budgets (timing, not records).
		float BudgetMs = 1.0f;
		float LoadBudgetMs = 8.0f;
		/**
		 * Predicted apply cost of a unit, a + b * changes + c * instances_after (ms; plan C scatter 3.5 "stored as option defaults"), overridable
		 * with -ChimeraTerrainScatterPredict=a,b,c. Fitted in S4 on sx_smoke's 1,101 apply_unit_ms rows (build of 2026-10-02 17:18): least squares
		 * a 0.00321, b 0.0000697, c 0.0000293; a is raised by the fit's p95 residual (0.0287) so admission under-predicts about 5 % of units, not
		 * half of them; b and c rounded up.
		 */
		double PredictA = 0.032;
		double PredictB = 0.00007;
		double PredictC = 0.00003;
		// SCATTER_FP_END
		int32 Threads = 2;
		/** Bit per EScatterLayer: layers shown (visibility only; records and hashes are unchanged). */
		uint32 LayerMask = (1u << ScatterLayerCount) - 1u;
		int32 Governor = 0;
		/** "stationary" (default), "static" or "movable" (diagnosis only). */
		FString Mobility = TEXT("stationary");
		bool bGrassNanite = true;
		/** "diff" (keyed edit script) or "clear" (ClearInstances + AddInstances). */
		FString ApplyMode = TEXT("diff");
		int32 FineTileM = 32;
		int32 CoarseTileM = 80;

		static FScatterOptions FromCommandLine(const TCHAR* Cmd);
		/** config_fnv of the options' palette. */
		uint64 ConfigFnv() const { return Palette.ConfigFnv(); }
	};
#endif
}

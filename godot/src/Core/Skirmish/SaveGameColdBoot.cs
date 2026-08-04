#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.AI;                 // AiDifficulty
using ProjectChimera.Core.Definitions;   // ScenarioData, CanonicalModelHash
using ProjectChimera.Core.Persistence;   // SaveGameFile, SaveGameState, SaveGameHeaderData

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>The fully-resolved plan for cold-booting a saved game from the main menu (DW-465): everything the
    /// Godot layer needs to hand to the existing <c>LaunchSkirmish</c> spine — the rebuilt scenario, the launch
    /// record, the AI difficulty, and the parsed save to overlay after the reload.</summary>
    public sealed class ColdBootPlan
    {
        /// <summary>The scenario rebuilt from the save header's persisted launch record via
        /// <see cref="SkirmishSetupToScenario.Build"/> — proven identical to the saved match's scenario by the
        /// <see cref="CanonicalModelHash"/> gate.</summary>
        public ScenarioData Built = null!;
        /// <summary>The launch record (retained config for the boot + the save-header stamp of the resumed match).</summary>
        public SkirmishSetup Setup = null!;
        /// <summary>The AI difficulty from the launch record's AI slot (Normal when the record has none).</summary>
        public AiDifficulty AiLevel;
        /// <summary>The parsed save body to arm for the post-reload overlay.</summary>
        public SaveGameState State = null!;
        /// <summary>The parsed save header (the content-drift gate + the resumed match's launch stamp).</summary>
        public SaveGameHeaderData Header = null!;
    }

    /// <summary>
    /// DW-465 — cold-boot load-from-menu (FR-67's missing half): rebuild a saved match's scenario OUTSIDE any
    /// running match, from nothing but the save file and the shipped content catalogs. Story 11.3 wired the
    /// mid-match Load (which reuses the current match's in-memory scenario); this is the pure planning step for
    /// the main-menu path: parse the save fail-closed, resolve the header's <c>MapId</c> against the shipped map
    /// catalog, re-run <see cref="SkirmishSetupToScenario.Build"/> from the persisted launch record, and gate the
    /// rebuilt scenario against the save's <see cref="CanonicalModelHash"/> so a drifted/retuned/missing map is a
    /// located reject on the menu — never a mid-boot crash or a silently different board. Godot-free (the caller
    /// resolves <c>res://</c> paths); the presentation layer runs the CONTENT-hash gate afterwards (it needs the
    /// resolved faction defs) and then arms <see cref="SkirmishBootFlow.ArmLoad"/> + <c>LaunchSkirmish</c>.
    /// </summary>
    public static class SaveGameColdBoot
    {
        /// <summary>
        /// Build the cold-boot plan for a save blob. Returns null on success (with <paramref name="plan"/> set);
        /// otherwise a located, user-facing error string (and a null plan) — parse failures, an uninstalled or
        /// unloadable map, and a map whose content no longer matches the save all reject here, fail-closed.
        /// </summary>
        /// <param name="saveBytes">The raw <c>.chsav</c> bytes.</param>
        /// <param name="slotLabel">The slot name, for located error messages (the <c>SaveGameFile.Read</c> ctx).</param>
        /// <param name="maps">The shipped map catalog (<see cref="SkirmishCatalog.ScanMaps"/>).</param>
        /// <param name="factions">The shipped faction catalog (<see cref="SkirmishCatalog.ScanFactions"/>).</param>
        /// <param name="loadMapByResPath">Resolve a catalog <c>res://</c> map path to its parsed
        /// <see cref="ScenarioData"/> (the presentation layer globalizes; tests read temp files). Null = unreadable.</param>
        /// <param name="plan">The resolved plan on success; null on any reject.</param>
        public static string? TryPlan(
            byte[] saveBytes,
            string slotLabel,
            IReadOnlyList<MapEntry> maps,
            IReadOnlyList<FactionEntry> factions,
            Func<string, ScenarioData?> loadMapByResPath,
            out ColdBootPlan? plan)
        {
            plan = null;

            SaveGameHeaderData header;
            SaveGameState state;
            try
            {
                using var ms = new MemoryStream(saveBytes);
                (header, state) = SaveGameFile.Read(ms, slotLabel);
            }
            catch (InvalidDataException ex)
            {
                return ex.Message; // the reader's located, user-facing message (bad magic/version/corruption)
            }

            // Resolve the persisted MapId against the shipped catalog — the map must still be installed.
            MapEntry? map = null;
            foreach (MapEntry m in maps)
                if (string.Equals(m.Id, header.MapId, StringComparison.Ordinal)) { map = m; break; }
            if (map == null)
                return $"Save '{slotLabel}': the map this save used ('{header.MapId}') is no longer installed.";

            ScenarioData? baseMap = loadMapByResPath(map.ResPath);
            if (baseMap == null)
                return $"Save '{slotLabel}': the map file for '{header.MapId}' could not be loaded ({map.ResPath}).";

            // Rebuild the launch scenario from the persisted record — the exact transform the original launch ran.
            SkirmishSetup setup = header.ToSkirmishSetup();
            ScenarioData built = SkirmishSetupToScenario.Build(setup, baseMap, factions);

            // Fail-closed map-identity gate (the ThrowIfContentMismatch model half, runnable this early because the
            // model hash needs no resolved content): a retuned/edited map rebuilds to a DIFFERENT scenario than the
            // one the save was taken in — reject on the menu instead of overlaying the save onto the wrong board.
            if (CanonicalModelHash.Compute(built) != header.CanonicalModelHash)
                return $"Save '{slotLabel}': the map this save used has changed and no longer matches — it can no longer be loaded.";

            // The single launchable AI opponent's difficulty (mirrors SkirmishSetupOverlay.OnLaunchPressed).
            AiDifficulty ai = AiDifficulty.Normal;
            foreach (SetupSlot s in setup.Slots ?? new List<SetupSlot>())
                if (s != null && s.Kind == SlotKind.Ai) { ai = s.Ai; break; }

            plan = new ColdBootPlan { Built = built, Setup = setup, AiLevel = ai, State = state, Header = header };
            return null;
        }
    }
}

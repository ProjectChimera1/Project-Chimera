#nullable enable
using System;
using ProjectChimera.AI;                 // AiDifficulty (Godot-free)
using ProjectChimera.Core.Definitions;   // ScenarioData
using ProjectChimera.Core.Persistence;   // SaveGameState, SaveGameHeaderData

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>
    /// DW-459 — the cross-reload skirmish-boot handoff state, extracted from <c>MainScene</c>'s five loose statics
    /// (<c>PendingSkirmishStart</c> / <c>PendingSkirmishAiLevel</c> / <c>PendingSkirmishConfig</c> /
    /// <c>PendingSkirmishError</c> + <c>ScenarioLoadPhase.PendingGeneratedScenario</c>) and the Story-11.3 pending-load
    /// pair, into ONE Godot-free object whose transitions (<see cref="SkirmishBootFlow"/>) are Tier-1 testable.
    /// MainScene holds the single live instance in a static field (it must survive <c>ReloadCurrentScene</c>) and the
    /// Godot layer only performs side effects (reload, overlays, logging) around these pure transitions.
    /// Plain mutable data — no behavior; all decisions live on <see cref="SkirmishBootFlow"/>.
    /// </summary>
    public sealed class SkirmishBootHandoff
    {
        /// <summary>A skirmish launch is armed: the fresh <c>_Ready</c> must consume it (read-then-clear).</summary>
        public bool Start;

        /// <summary>The AI difficulty override carried by an armed launch; null on a normal boot.</summary>
        public AiDifficulty? AiLevel;

        /// <summary>The retained setup config — kept across the launch reload so a boot failure can re-open the
        /// setup screen pre-filled. Cleared on a successful start or after the fail-safe re-open consumes it.</summary>
        public SkirmishSetup? Config;

        /// <summary>Set when a skirmish boot threw: the located error the fail-safe re-open surfaces on the next
        /// boot (paired with <see cref="Config"/>). Null on a normal boot.</summary>
        public string? Error;

        /// <summary>The built in-memory scenario handed across the reload (the <c>PendingGeneratedScenario</c>
        /// slot — shared with the AI map-generator path, which arms it without <see cref="Start"/>).</summary>
        public ScenarioData? PendingScenario;

        /// <summary>Story 11.3 — a parsed SP save armed for the post-reload overlay (consumed once by the
        /// Play-entry reset, or disarmed by a failed boot / vetoed reset).</summary>
        public SaveGameState? PendingLoad;

        /// <summary>The armed save's header (paired with <see cref="PendingLoad"/>).</summary>
        public SaveGameHeaderData? PendingLoadHeader;
    }

    /// <summary>
    /// DW-459 — the PURE decisions of the <c>MainScene</c> skirmish match-start orchestration, extracted so the
    /// headline flow (registry sizing from the in-memory scenario, the read-then-clear of the handoff statics, the
    /// fail-safe transition, the success commit, and the fail-safe re-open) is pinned by Godot-free Tier-1 tests.
    /// Every method mutates only the passed <see cref="SkirmishBootHandoff"/> and returns plain data; all Godot
    /// side effects (scene reload, overlays, logging) stay in the presentation layer.
    /// </summary>
    public static class SkirmishBootFlow
    {
        /// <summary>The launch decision consumed by a fresh <c>_Ready</c>: whether this boot is a skirmish start and
        /// the AI-difficulty override to apply (null = keep the scene's inspector value).</summary>
        public readonly struct BootStart
        {
            /// <summary>True = this boot consumes an armed skirmish launch.</summary>
            public bool SkirmishStart { get; }
            /// <summary>Non-null = override the scene's AI level BEFORE the sim host is built.</summary>
            public AiDifficulty? AiOverride { get; }
            public BootStart(bool start, AiDifficulty? ai) { SkirmishStart = start; AiOverride = ai; }
        }

        /// <summary>
        /// Arm a skirmish launch (the <c>LaunchSkirmish</c> stash half): the built scenario, the AI level, and the
        /// retained config; clears any stale error. The caller then reloads the scene.
        /// </summary>
        public static void Arm(SkirmishBootHandoff h, ScenarioData built, AiDifficulty ai, SkirmishSetup retained)
        {
            h.PendingScenario = built;
            h.Start           = true;
            h.AiLevel         = ai;
            h.Config          = retained;
            h.Error           = null;
        }

        /// <summary>
        /// Consume the armed launch at the top of <c>_Ready</c> (read-then-clear, exactly once): returns the
        /// start decision and clears <see cref="SkirmishBootHandoff.Start"/>/<see cref="SkirmishBootHandoff.AiLevel"/>.
        /// <see cref="SkirmishBootHandoff.Config"/>/<see cref="SkirmishBootHandoff.Error"/> are left intact so a boot
        /// exception can still re-open the setup screen pre-filled; <see cref="SkirmishBootHandoff.PendingScenario"/>
        /// is left for the scenario-load phase to consume. A stale AI level from a non-start boot is discarded, never
        /// applied.
        /// </summary>
        public static BootStart ConsumeStart(SkirmishBootHandoff h)
        {
            bool start = h.Start;
            AiDifficulty? ai = start ? h.AiLevel : null;
            h.Start   = false;
            h.AiLevel = null;
            return new BootStart(start, ai);
        }

        /// <summary>
        /// The FactionRegistry sizing source (Story 11.1 review PATCH 1, pinned): on a skirmish start the active
        /// count comes from the IN-MEMORY built scenario — the on-disk <c>ScenarioPath</c> peek is NEVER invoked
        /// (sizing from the stale default map mis-spans the active set for any non-2-slot skirmish). On a normal
        /// boot it defers to <paramref name="peekOnDisk"/>. Returns the RAW slot count — the caller clamps via
        /// <c>PlayerCountPolicy.SimActivePlayers</c> exactly as before.
        /// </summary>
        public static int RawRegistrySlots(bool skirmishStart, ScenarioData? pendingScenario, Func<int> peekOnDisk)
            => skirmishStart
                ? (pendingScenario?.PlayerSlots?.Length ?? 0)
                : peekOnDisk();

        /// <summary>
        /// The fail-safe transition (a skirmish boot threw): DROP the pending scenario so the clean reload cannot
        /// re-apply the bad model, record the located error, RETAIN the config for the pre-filled re-open — and
        /// disarm any pending SP load, so a save armed by the load path can never silently overlay a later,
        /// unrelated launch (the reset-wrapper disarm cannot run when the boot itself failed).
        /// </summary>
        public static void FailBoot(SkirmishBootHandoff h, string errorMessage)
        {
            h.PendingScenario = null;
            h.Error           = errorMessage;
            // Config deliberately retained — the next boot's re-open consumes it.
            h.PendingLoad       = null;
            h.PendingLoadHeader = null;
        }

        /// <summary>
        /// The success commit (the skirmish boot completed cleanly): consume + return the retained config (the
        /// caller stores it as the current match's launch record for save headers) and clear the error. A pending
        /// SP load stays ARMED — the Play-entry reset consumes it after this commit.
        /// </summary>
        public static SkirmishSetup? CommitSuccess(SkirmishBootHandoff h)
        {
            SkirmishSetup? retained = h.Config;
            h.Config = null;
            h.Error  = null;
            return retained;
        }

        /// <summary>
        /// The fail-safe re-open decision on a NORMAL boot: when a previous skirmish boot threw (config + error both
        /// present), consume both and return them so the caller re-opens the setup screen pre-filled with the error
        /// surfaced. Returns null (touching nothing) otherwise.
        /// </summary>
        public static (SkirmishSetup Config, string Error)? TakeReopen(SkirmishBootHandoff h)
        {
            if (h.Config == null || string.IsNullOrEmpty(h.Error)) return null;
            SkirmishSetup config = h.Config;
            string error = h.Error!;
            h.Config = null;
            h.Error  = null;
            return (config, error);
        }

        /// <summary>Story 11.3 — arm a parsed SP save for the post-reload overlay (mid-match Load and the DW-465
        /// cold-boot menu Load both route through this before <c>LaunchSkirmish</c>).</summary>
        public static void ArmLoad(SkirmishBootHandoff h, SaveGameState state, SaveGameHeaderData header)
        {
            h.PendingLoad       = state;
            h.PendingLoadHeader = header;
        }

        /// <summary>Consume the armed SP save exactly once (the Play-entry reset's final overlay step). Null when
        /// nothing is armed.</summary>
        public static SaveGameState? TakeLoad(SkirmishBootHandoff h)
        {
            SaveGameState? state = h.PendingLoad;
            h.PendingLoad       = null;
            h.PendingLoadHeader = null;
            return state;
        }

        /// <summary>Disarm a pending SP save without consuming it (the reset-veto sweep): returns true when
        /// something was actually armed, so the caller can surface the "load discarded" notice only then.</summary>
        public static bool DisarmLoad(SkirmishBootHandoff h)
        {
            bool had = h.PendingLoad != null;
            h.PendingLoad       = null;
            h.PendingLoadHeader = null;
            return had;
        }
    }
}

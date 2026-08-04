#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI;                 // AiDifficulty
using ProjectChimera.Core.Definitions;   // ScenarioData
using ProjectChimera.Core.Persistence;   // SaveGameState, SaveGameHeaderData
using ProjectChimera.Core.Skirmish;      // SkirmishBootHandoff, SkirmishBootFlow, SkirmishSetup
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-459 — the MainScene skirmish match-start orchestration's PURE decisions, extracted to
    /// <see cref="SkirmishBootFlow"/> and pinned here (the ledger's named regressions: sizing the FactionRegistry
    /// from the stale on-disk ScenarioPath instead of the in-memory scenario, and the fail-safe not clearing
    /// PendingGeneratedScenario — both previously covered by NO test). The Godot layer only performs side effects
    /// around these transitions, so a regression in the headline flow now fails Tier-1 instead of shipping green.
    /// </summary>
    public class SkirmishBootFlowTests
    {
        private static ScenarioData Scenario(int slots)
        {
            var m = new ScenarioData { Id = "m", DisplayName = "m" };
            var ps = new ScenarioPlayerSlot[slots];
            for (int i = 0; i < slots; i++) ps[i] = new ScenarioPlayerSlot { Slot = i };
            m.PlayerSlots = ps;
            return m;
        }

        private static SkirmishSetup Config() => new() { MapId = "m", Slots = new List<SetupSlot>() };

        private static SkirmishBootHandoff Armed(out ScenarioData built, out SkirmishSetup config)
        {
            var h = new SkirmishBootHandoff();
            built = Scenario(3);
            config = Config();
            SkirmishBootFlow.Arm(h, built, AiDifficulty.Hard, config);
            return h;
        }

        // ── Arm ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Arm_SetsEveryHandoffField_AndClearsAStaleError()
        {
            var h = new SkirmishBootHandoff { Error = "stale from a prior failed boot" };
            var built = Scenario(2);
            var config = Config();

            SkirmishBootFlow.Arm(h, built, AiDifficulty.Easy, config);

            Assert.True(h.Start);
            Assert.Equal(AiDifficulty.Easy, h.AiLevel);
            Assert.Same(built, h.PendingScenario);
            Assert.Same(config, h.Config);
            Assert.Null(h.Error);
        }

        // ── ConsumeStart ────────────────────────────────────────────────────────────

        [Fact]
        public void ConsumeStart_ReadsThenClears_LeavingConfigErrorAndScenario()
        {
            SkirmishBootHandoff h = Armed(out ScenarioData built, out SkirmishSetup config);

            SkirmishBootFlow.BootStart boot = SkirmishBootFlow.ConsumeStart(h);

            Assert.True(boot.SkirmishStart);
            Assert.Equal(AiDifficulty.Hard, boot.AiOverride);
            Assert.False(h.Start);                 // read-then-clear: a second boot is a normal boot
            Assert.Null(h.AiLevel);
            Assert.Same(built, h.PendingScenario); // left for the scenario-load phase to consume
            Assert.Same(config, h.Config);         // left for the fail-safe re-open / success commit
        }

        [Fact]
        public void ConsumeStart_SecondConsume_IsANormalBoot()
        {
            SkirmishBootHandoff h = Armed(out _, out _);
            SkirmishBootFlow.ConsumeStart(h);

            SkirmishBootFlow.BootStart second = SkirmishBootFlow.ConsumeStart(h);

            Assert.False(second.SkirmishStart);
            Assert.Null(second.AiOverride);
        }

        [Fact]
        public void ConsumeStart_StaleAiLevelWithoutStartFlag_IsDiscardedNeverApplied()
        {
            var h = new SkirmishBootHandoff { Start = false, AiLevel = AiDifficulty.Hard };

            SkirmishBootFlow.BootStart boot = SkirmishBootFlow.ConsumeStart(h);

            Assert.False(boot.SkirmishStart);
            Assert.Null(boot.AiOverride); // a stale override must never leak into a normal boot
            Assert.Null(h.AiLevel);       // and it is cleared, not left armed
        }

        // ── RawRegistrySlots (Story 11.1 review PATCH 1, pinned) ────────────────────

        [Fact]
        public void RawRegistrySlots_SkirmishStart_UsesInMemoryScenario_NeverTouchesDisk()
        {
            int peeks = 0;
            int raw = SkirmishBootFlow.RawRegistrySlots(skirmishStart: true, Scenario(4), () => { peeks++; return 2; });

            Assert.Equal(4, raw);   // the IN-MEMORY built scenario's count
            Assert.Equal(0, peeks); // the stale on-disk default map is NEVER read on a skirmish start
        }

        [Fact]
        public void RawRegistrySlots_SkirmishStartWithNullScenario_YieldsZero_ForTheClampFallback()
        {
            int raw = SkirmishBootFlow.RawRegistrySlots(skirmishStart: true, null, () => 7);
            Assert.Equal(0, raw); // ClampActivePlayers turns 0 into the 2-player floor, as before
        }

        [Fact]
        public void RawRegistrySlots_NormalBoot_DefersToTheDiskPeek()
        {
            int raw = SkirmishBootFlow.RawRegistrySlots(skirmishStart: false, Scenario(4), () => 2);
            Assert.Equal(2, raw); // a normal boot sizes from ScenarioPath even when a pending scenario exists
        }

        // ── FailBoot ────────────────────────────────────────────────────────────────

        [Fact]
        public void FailBoot_ClearsPendingScenario_RecordsError_RetainsConfig()
        {
            SkirmishBootHandoff h = Armed(out _, out SkirmishSetup config);
            SkirmishBootFlow.ConsumeStart(h);

            SkirmishBootFlow.FailBoot(h, "located boot error");

            Assert.Null(h.PendingScenario);            // the clean reload must not re-apply the bad model
            Assert.Equal("located boot error", h.Error);
            Assert.Same(config, h.Config);             // retained for the pre-filled re-open
        }

        [Fact]
        public void FailBoot_DisarmsAPendingLoad_SoAStaleSaveNeverOverlaysALaterLaunch()
        {
            SkirmishBootHandoff h = Armed(out _, out _);
            SkirmishBootFlow.ArmLoad(h, new SaveGameState(), new SaveGameHeaderData());

            SkirmishBootFlow.FailBoot(h, "boot failed");

            Assert.Null(h.PendingLoad);
            Assert.Null(h.PendingLoadHeader);
        }

        // ── CommitSuccess ───────────────────────────────────────────────────────────

        [Fact]
        public void CommitSuccess_ReturnsRetainedConfig_ClearsConfigAndError()
        {
            SkirmishBootHandoff h = Armed(out _, out SkirmishSetup config);
            SkirmishBootFlow.ConsumeStart(h);

            SkirmishSetup? retained = SkirmishBootFlow.CommitSuccess(h);

            Assert.Same(config, retained); // becomes _currentSkirmishSetup for save headers
            Assert.Null(h.Config);
            Assert.Null(h.Error);
        }

        [Fact]
        public void CommitSuccess_LeavesAPendingLoadArmed_ForThePlayEntryOverlay()
        {
            SkirmishBootHandoff h = Armed(out _, out _);
            var state = new SaveGameState();
            SkirmishBootFlow.ArmLoad(h, state, new SaveGameHeaderData());

            SkirmishBootFlow.CommitSuccess(h);

            Assert.Same(state, h.PendingLoad); // consumed later by the Play-entry reset, not by the boot commit
        }

        // ── TakeReopen ──────────────────────────────────────────────────────────────

        [Fact]
        public void TakeReopen_AfterAFailedBoot_ConsumesConfigAndErrorOnce()
        {
            SkirmishBootHandoff h = Armed(out _, out SkirmishSetup config);
            SkirmishBootFlow.ConsumeStart(h);
            SkirmishBootFlow.FailBoot(h, "boom");

            (SkirmishSetup Config, string Error)? reopen = SkirmishBootFlow.TakeReopen(h);

            Assert.NotNull(reopen);
            Assert.Same(config, reopen!.Value.Config);
            Assert.Equal("boom", reopen.Value.Error);
            Assert.Null(h.Config); // consumed once — a later boot is clean
            Assert.Null(h.Error);
            Assert.Null(SkirmishBootFlow.TakeReopen(h));
        }

        [Fact]
        public void TakeReopen_NothingToReopen_OnACleanBoot_OrWithOnlyHalfTheState()
        {
            Assert.Null(SkirmishBootFlow.TakeReopen(new SkirmishBootHandoff()));
            Assert.Null(SkirmishBootFlow.TakeReopen(new SkirmishBootHandoff { Config = Config() }));          // no error
            Assert.Null(SkirmishBootFlow.TakeReopen(new SkirmishBootHandoff { Error = "orphaned error" }));   // no config
        }

        // ── ArmLoad / TakeLoad / DisarmLoad ─────────────────────────────────────────

        [Fact]
        public void TakeLoad_ConsumesTheArmedSaveExactlyOnce()
        {
            var h = new SkirmishBootHandoff();
            var state = new SaveGameState();
            var header = new SaveGameHeaderData();
            SkirmishBootFlow.ArmLoad(h, state, header);
            Assert.Same(header, h.PendingLoadHeader);

            Assert.Same(state, SkirmishBootFlow.TakeLoad(h));
            Assert.Null(h.PendingLoadHeader);
            Assert.Null(SkirmishBootFlow.TakeLoad(h)); // consumed once
        }

        [Fact]
        public void DisarmLoad_ReportsWhetherAnythingWasArmed()
        {
            var h = new SkirmishBootHandoff();
            Assert.False(SkirmishBootFlow.DisarmLoad(h)); // nothing armed → no "load discarded" notice

            SkirmishBootFlow.ArmLoad(h, new SaveGameState(), new SaveGameHeaderData());
            Assert.True(SkirmishBootFlow.DisarmLoad(h));  // armed → disarmed + notice
            Assert.Null(h.PendingLoad);
            Assert.Null(h.PendingLoadHeader);
        }
    }
}

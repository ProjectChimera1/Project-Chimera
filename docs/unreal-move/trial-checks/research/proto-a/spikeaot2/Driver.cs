#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectChimera.Sim.Tests.Golden;
namespace SpikeAot {
  public static partial class ScenarioRunner {
    public static void RunAll(TextWriter w) {
      Run(w, "AbilityDomainFilterScenario", AbilityDomainFilterScenario.DefaultTicks, AbilityDomainFilterScenario.Build, "ability-domain-filter-scenario.golden.txt");
      Run(w, "AiActiveScenario", AiActiveScenario.DefaultTicks, AiActiveScenario.Build, "ai-active-scenario.golden.txt");
      Run(w, "AntiBuildingScenario", AntiBuildingScenario.DefaultTicks, AntiBuildingScenario.Build, "anti-building-scenario.golden.txt");
      Run(w, "CombatAirGroundScenario", CombatAirGroundScenario.DefaultTicks, CombatAirGroundScenario.Build, "combat-air-ground-scenario.golden.txt");
      Run(w, "CommandVocabularyScenario", CommandVocabularyScenario.DefaultTicks, CommandVocabularyScenario.Build, "command-vocabulary-scenario.golden.txt");
      Run(w, "CritDodgeScenario", CritDodgeScenario.DefaultTicks, CritDodgeScenario.Build, "crit-dodge-scenario.golden.txt");
      Run(w, "DeliveryScenario", DeliveryScenario.DefaultTicks, DeliveryScenario.Build, "delivery-scenario.golden.txt");
      Run(w, "FormationSeparationScenario", FormationSeparationScenario.DefaultTicks, FormationSeparationScenario.Build, "formation-separation-scenario.golden.txt");
      Run(w, "GoldenApplierScenario", GoldenApplierScenario.DefaultTicks, GoldenApplierScenario.Build, "golden-applier-scenario.golden.txt");
      Run(w, "GoldenScenario", GoldenScenario.DefaultTicks, GoldenScenario.Build, "golden-scenario.golden.txt");
      Run(w, "GroundCastScenario", GroundCastScenario.DefaultTicks, GroundCastScenario.Build, "ground-cast-scenario.golden.txt");
      Run(w, "HeroXpScenario", HeroXpScenario.DefaultTicks, HeroXpScenario.Build, "hero-xp-scenario.golden.txt");
      Run(w, "ModifierScenario", ModifierScenario.DefaultTicks, ModifierScenario.Build, "modifier-scenario.golden.txt");
      Run(w, "MultiFaction3Scenario", MultiFaction3Scenario.DefaultTicks, MultiFaction3Scenario.Build, "multi-faction3-scenario.golden.txt");
      Run(w, "MultiFaction8Scenario", MultiFaction8Scenario.DefaultTicks, MultiFaction8Scenario.Build, "multi-faction8-scenario.golden.txt");
      Run(w, "MultiFactionScenario", MultiFactionScenario.DefaultTicks, MultiFactionScenario.Build, "multi-faction-scenario.golden.txt");
      Run(w, "PathabilityBlockScenario", PathabilityBlockScenario.DefaultTicks, PathabilityBlockScenario.Build, "pathability-block-scenario.golden.txt");
      Run(w, "ProjectileSnapScenario", ProjectileSnapScenario.DefaultTicks, ProjectileSnapScenario.Build, "projectile-snap-scenario.golden.txt");
      Run(w, "SameTickTieBreakScenario", SameTickTieBreakScenario.DefaultTicks, SameTickTieBreakScenario.Build, "same-tick-tie-break-scenario.golden.txt");
      Run(w, "ShiftQueueScenario", ShiftQueueScenario.DefaultTicks, ShiftQueueScenario.Build, "shift-queue-scenario.golden.txt");
      Run(w, "StatPipelineScenario", StatPipelineScenario.DefaultTicks, StatPipelineScenario.Build, "stat-pipeline-scenario.golden.txt");
      Run(w, "TagFilterScenario", TagFilterScenario.DefaultTicks, TagFilterScenario.Build, "tag-filter-scenario.golden.txt");
    }
  }
}
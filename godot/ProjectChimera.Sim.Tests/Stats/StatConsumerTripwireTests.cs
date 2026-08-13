#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Core.Stats;
using Xunit;

namespace ProjectChimera.Sim.Tests.Stats
{
    /// <summary>
    /// Story 15-24a — the DECLARED-BUT-NEVER-CONSUMED tripwire, the spec's own guard for the
    /// computed-but-never-consumed defect class (DW-918/DW-920 lineage): every registry stat declares the
    /// literal source token its consumer reads (<see cref="StatDefinition.ConsumerEvidence"/> — an Effective
    /// array name, a seam method name, or the percent accumulator its recompute pairing reads), and this test
    /// walks <c>godot/src/**</c> asserting the token EXISTS outside the registry's own files. A stat whose
    /// consumer is deleted — or a new registry row whose consumer was never written — fails HERE, loudly,
    /// with the declared site in the message. The reverse direction (CONSUMED-BUT-UNDECLARED) is closed by
    /// construction: <see cref="StatId"/> is the only key any stat surface accepts, and
    /// <c>StatVocabularyGuardTests</c> pins one registry row per member.
    /// </summary>
    public class StatConsumerTripwireTests
    {
        private static string SrcDir([CallerFilePath] string thisFile = "")
        {
            // …/godot/ProjectChimera.Sim.Tests/Stats/StatConsumerTripwireTests.cs → …/godot/src
            string testsDir = Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
            return Path.GetFullPath(Path.Combine(testsDir, "..", "src"));
        }

        [Fact]
        public void EveryRegistryStat_DeclaresConsumerEvidence_ThatExistsInTheSourceTree()
        {
            string src = SrcDir();
            Assert.True(Directory.Exists(src), $"src tree not found at '{src}' (via [CallerFilePath]).");

            string[] files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories);
            Assert.True(files.Length > 100, "non-vacuity: the scan really sees the source tree");

            foreach (var def in StatVocabulary.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(def.ConsumerEvidence),
                    $"'{def.JsonName}' declares no ConsumerEvidence — every Active stat must name the token its consumer reads.");

                bool found = false;
                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    // The registry's own declaration files don't count as consumption — that would let a
                    // registry row testify for itself (exactly the self-report the tripwire exists to refuse).
                    if (name == "StatVocabulary.cs" || name == "StatDefinition.cs" || name == "StatId.cs") continue;
                    if (File.ReadAllText(file).Contains(def.ConsumerEvidence, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }
                Assert.True(found,
                    $"DECLARED-BUT-NEVER-CONSUMED: stat '{def.JsonName}' declares consumer evidence token " +
                    $"'{def.ConsumerEvidence}' (site: {def.ConsumerSite}) but the token appears NOWHERE in godot/src " +
                    "outside the registry itself. Either its consumer was deleted (restore it or retire the stat " +
                    "consciously) or a new registry row shipped without its one consumer read — the recipe's step (3).");
            }
        }
    }
}

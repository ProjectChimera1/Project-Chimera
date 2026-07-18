#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 7.15 — the pure read-side <see cref="DslVarReadback.Enumerate"/> accessor that feeds the trigger-debug
    /// variable watch. It lists every declared watchable variable (Global + Per-player scalars + Global arrays) with
    /// its current value + version off the published snapshot; <c>TriggerLocal</c> scratch is absent by construction
    /// (it is never in the read rail). A pure read — no fold impact.
    /// </summary>
    public class DslVarReadbackEnumerateTests
    {
        private static (DslVarTable table, DslVarReadback rb) Build(params DslVarDecl[] decls)
        {
            var table = new DslVarTable();
            table.InitFromDeclarations(decls, Array.Empty<DslTimerDecl>());
            var rb = new DslVarReadback();
            rb.InitFromDeclarations(decls);
            return (table, rb);
        }

        [Fact]
        public void Enumerate_ListsGlobalPerPlayerAndArray_WithValues()
        {
            var (table, rb) = Build(
                new DslVarDecl("score", DslValueType.Int, VarScope.Global, 7),
                new DslVarDecl("gold", DslValueType.Int, VarScope.PerPlayer, 0),
                new DslVarDecl("board", DslValueType.Array, VarScope.Global, 0,
                    elementType: DslValueType.Int, capacity: 8));

            table.SetInt("gold", 2, 300); // slot 2 (Player3)
            table.ArrayPush("board", 11);
            table.ArrayPush("board", 22);
            rb.Publish(table, 1);

            List<DslVarReadback.WatchVar> vars = rb.Enumerate(faction: 2);
            Assert.Equal(3, vars.Count);

            DslVarReadback.WatchVar score = vars.Single(v => v.Name == "score");
            Assert.Equal(VarScope.Global, score.Scope);
            Assert.False(score.IsArray);
            Assert.Equal(7, score.Raw0);

            DslVarReadback.WatchVar gold = vars.Single(v => v.Name == "gold");
            Assert.Equal(VarScope.PerPlayer, gold.Scope);
            Assert.Equal(300, gold.Raw0); // slot 2 value for the requested faction

            DslVarReadback.WatchVar board = vars.Single(v => v.Name == "board");
            Assert.True(board.IsArray);
            Assert.Equal(2, board.ArrayCount);
        }

        [Fact]
        public void Enumerate_ExcludesTriggerLocalScratch()
        {
            // A TriggerLocal scalar declaration is never tracked by the read rail — it must not appear in Enumerate.
            var (_, rb) = Build(
                new DslVarDecl("score", DslValueType.Int, VarScope.Global, 0),
                new DslVarDecl("scratch", DslValueType.Int, VarScope.TriggerLocal, 0));

            List<DslVarReadback.WatchVar> vars = rb.Enumerate();
            Assert.Single(vars);
            Assert.Equal("score", vars[0].Name);
            Assert.DoesNotContain(vars, v => v.Name == "scratch");
        }

        [Fact]
        public void Enumerate_VersionTracksChanges()
        {
            var (table, rb) = Build(new DslVarDecl("score", DslValueType.Int, VarScope.Global, 0));
            rb.Publish(table, 1);
            uint v0 = rb.Enumerate()[0].Version;

            table.SetInt("score", 0, 99);
            rb.Publish(table, 2);
            DslVarReadback.WatchVar after = rb.Enumerate()[0];
            Assert.Equal(99, after.Raw0);
            Assert.True(after.Version > v0); // a real change bumps the version
        }

        [Fact]
        public void Enumerate_EmptyWhenNoDeclarations()
        {
            var rb = new DslVarReadback();
            rb.InitFromDeclarations(Array.Empty<DslVarDecl>());
            Assert.Empty(rb.Enumerate());
        }
    }
}

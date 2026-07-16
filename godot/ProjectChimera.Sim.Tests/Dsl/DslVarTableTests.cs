#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;   // Fixed
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.3 — unit coverage for the <see cref="DslVarTable"/> store: typed/scoped get/set, declaration-index
    /// fold determinism, trigger-local allocate/free (and its absence from the fold), integer-tick timer
    /// decrement/expiry, and Clear.
    /// </summary>
    public class DslVarTableTests
    {
        // A local FNV-1a mix mirroring SimChecksum.Mix, so fold determinism can be asserted directly.
        private static uint Mix(uint hash, int value)
        {
            const uint prime = 16777619u;
            uint v = (uint)value;
            hash ^= v & 0xFF;         hash *= prime;
            hash ^= (v >> 8) & 0xFF;  hash *= prime;
            hash ^= (v >> 16) & 0xFF; hash *= prime;
            hash ^= (v >> 24) & 0xFF; hash *= prime;
            return hash;
        }

        // Story 7.3 (P2): FoldInto folds EVERY player slot (0..7), so the fold takes no active-slot list.
        private static uint Fold(DslVarTable t)
        {
            uint h = 2166136261u;
            t.FoldInto(ref h, Mix);
            return h;
        }

        [Fact]
        public void Global_Int_GetSet_RoundTrips()
        {
            var t = new DslVarTable();
            t.InitFromDeclarations(new[] { new DslVarDecl("g", DslValueType.Int, VarScope.Global, 3) },
                                   Array.Empty<DslTimerDecl>());
            Assert.Equal(3, t.GetInt("g", faction: 0));   // initial preserved
            t.SetInt("g", faction: 5, 42);                // faction ignored for Global
            Assert.Equal(42, t.GetInt("g", faction: 0));
        }

        [Fact]
        public void PerPlayer_SelectsFactionSlot()
        {
            var t = new DslVarTable();
            t.InitFromDeclarations(new[] { new DslVarDecl("pp", DslValueType.Int, VarScope.PerPlayer, 0) },
                                   Array.Empty<DslTimerDecl>());
            t.SetInt("pp", faction: 0, 10);
            t.SetInt("pp", faction: 1, 20);
            Assert.Equal(10, t.GetInt("pp", faction: 0));
            Assert.Equal(20, t.GetInt("pp", faction: 1));
            Assert.Equal(0,  t.GetInt("pp", faction: 2)); // untouched slot stays at its initial
        }

        [Fact]
        public void Undeclared_ReadsZero_WithoutCreating_AndAppendsGlobalOnWrite()
        {
            var t = new DslVarTable();
            t.InitFromDeclarations(Array.Empty<DslVarDecl>(), Array.Empty<DslTimerDecl>());

            // A read of an undeclared name returns 0 and does NOT create a slot (fold is still empty-globals).
            Assert.Equal(0, t.GetInt("x", faction: 0));
            uint before = Fold(t);

            t.SetInt("x", faction: 0, 7);      // append a Global/Int slot (legacy SetVariable parity)
            Assert.Equal(7, t.GetInt("x", faction: 0));
            uint after = Fold(t);
            Assert.NotEqual(before, after);    // the appended global now folds
        }

        [Fact]
        public void Fixed_InitialPreservedAsRaw()
        {
            var t = new DslVarTable();
            Fixed init = Fixed.FromFloat(2.5f);
            t.InitFromDeclarations(new[] { new DslVarDecl("f", DslValueType.Fixed, VarScope.Global, init.Raw) },
                                   Array.Empty<DslTimerDecl>());
            // A Fixed slot stores Fixed.Raw verbatim; GetInt returns that raw (7.3 ECA reads Int only in practice).
            Assert.Equal(init.Raw, t.GetInt("f", faction: 0));
        }

        [Fact]
        public void TriggerLocal_ResetsOnEnter_AndIsNeverFolded()
        {
            var t = new DslVarTable();
            t.InitFromDeclarations(new[]
            {
                new DslVarDecl("g",  DslValueType.Int, VarScope.Global,       0),
                new DslVarDecl("tl", DslValueType.Int, VarScope.TriggerLocal, 1),
            }, Array.Empty<DslTimerDecl>());

            uint foldBefore = Fold(t);

            // Within a trigger scope the local is readable/writable, seeded to its initial on Enter.
            t.Enter();
            Assert.Equal(1, t.GetInt("tl", faction: 0));
            t.SetInt("tl", faction: 0, 99);
            Assert.Equal(99, t.GetInt("tl", faction: 0));
            // The trigger-local write must NOT change the fold (never persisted, never folded).
            Assert.Equal(foldBefore, Fold(t));
            t.Exit();

            // Freed after Exit; a re-Enter resets it to the declared initial (not the prior 99).
            t.Enter();
            Assert.Equal(1, t.GetInt("tl", faction: 0));
            t.Exit();
        }

        [Fact]
        public void Timer_DecrementsInCreationIndex_AndExpiresOnZeroTick()
        {
            var t = new DslVarTable();
            t.InitFromDeclarations(Array.Empty<DslVarDecl>(), Array.Empty<DslTimerDecl>());
            t.TimerSet("a", 2);
            t.TimerSet("b", 1);

            var expired = new List<string>();
            t.TimerTickAndCollectExpired(expired);   // a:2→1, b:1→0 → b expires
            Assert.Equal(new[] { "b" }, expired);

            expired.Clear();
            t.TimerTickAndCollectExpired(expired);   // a:1→0 → a expires
            Assert.Equal(new[] { "a" }, expired);

            expired.Clear();
            t.TimerTickAndCollectExpired(expired);   // both inactive → nothing
            Assert.Empty(expired);
        }

        [Fact]
        public void DeclaredTimers_StartActive_AtTickCount()
        {
            var t = new DslVarTable();
            t.InitFromDeclarations(Array.Empty<DslVarDecl>(), new[] { new DslTimerDecl("t", 1) });
            var expired = new List<string>();
            t.TimerTickAndCollectExpired(expired);
            Assert.Equal(new[] { "t" }, expired); // 1→0 fires this tick
        }

        [Fact]
        public void Fold_IsDeterministic_AndDeclarationIndexOrdered()
        {
            var a = new DslVarTable();
            var b = new DslVarTable();
            var decls = new[]
            {
                new DslVarDecl("g0", DslValueType.Int, VarScope.Global,    0),
                new DslVarDecl("pp", DslValueType.Int, VarScope.PerPlayer, 0),
                new DslVarDecl("g1", DslValueType.Int, VarScope.Global,    0),
            };
            a.InitFromDeclarations(decls, new[] { new DslTimerDecl("t", 5) });
            b.InitFromDeclarations(decls, new[] { new DslTimerDecl("t", 5) });

            a.SetInt("g0", 0, 11); a.SetInt("g1", 0, 22); a.SetInt("pp", 0, 33);
            b.SetInt("g0", 0, 11); b.SetInt("g1", 0, 22); b.SetInt("pp", 0, 33);

            Assert.Equal(Fold(a), Fold(b)); // identical state → identical fold

            // Distinct declaration-index values move the fold (g0 vs g1 are not commutative).
            var c = new DslVarTable();
            c.InitFromDeclarations(decls, new[] { new DslTimerDecl("t", 5) });
            c.SetInt("g0", 0, 22); c.SetInt("g1", 0, 11); c.SetInt("pp", 0, 33);
            Assert.NotEqual(Fold(a), Fold(c));
        }

        [Fact]
        public void TriggerLocalWrite_OutsideAScope_IsANoOp_NeverAPhantomGlobal()
        {
            // Review follow-up: a SetInt on a TriggerLocal-declared NAME outside a trigger scope is the documented
            // no-op. It previously fell through to the undeclared-append path and minted a phantom Global slot (same
            // name) that folded into the checksum — violating "TriggerLocal is never engine-global, never folded".
            var t = new DslVarTable();
            t.InitFromDeclarations(new[] { new DslVarDecl("tl", DslValueType.Int, VarScope.TriggerLocal, 1) },
                                   Array.Empty<DslTimerDecl>());
            uint before = Fold(t);

            t.SetInt("tl", faction: 0, 99);              // outside any Enter/Exit scope → no-op
            Assert.Equal(0, t.GetInt("tl", faction: 0)); // reads 0 outside a scope (documented contract)
            Assert.Equal(before, Fold(t));               // and no phantom Global slot entered the fold

            t.Enter();
            Assert.Equal(1, t.GetInt("tl", faction: 0)); // scope re-seeds the declared initial, not the 99
            t.Exit();
        }

        [Fact]
        public void PlayerSlots_MatchesTheEngineCount()
        {
            // DslVarTable cannot reference FactionRegistry (the Dsl→Core-boundary rule), so PlayerSlots is a
            // hand-maintained copy of FactionRegistry.PLAYER_COUNT — this pin turns RED if either side moves alone
            // (a divergence would silently mis-size the folded per-player region).
            Assert.Equal(FactionRegistry.PLAYER_COUNT, DslVarTable.PlayerSlots);
        }

        [Fact]
        public void Clear_ResetsEverything()
        {
            var t = new DslVarTable();
            t.InitFromDeclarations(new[] { new DslVarDecl("g", DslValueType.Int, VarScope.Global, 5) },
                                   new[] { new DslTimerDecl("t", 3) });
            t.SetInt("g", 0, 99);
            t.Clear();
            Assert.Equal(0, t.GetInt("g", 0)); // slot gone → undeclared → 0
            var expired = new List<string>();
            t.TimerTickAndCollectExpired(expired);
            Assert.Empty(expired);             // timer gone
        }
    }
}

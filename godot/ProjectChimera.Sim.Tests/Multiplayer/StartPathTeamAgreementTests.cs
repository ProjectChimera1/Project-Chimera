#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ProjectChimera.Combat;             // DamageTable
using ProjectChimera.Core;               // HeroStore, HeroId, Fixed, AllianceSeeder
using ProjectChimera.Core.Definitions;   // ScenarioData, MatchAgreementHash, CanonicalModelHash, AbilityRegistry, ItemRegistry
using ProjectChimera.Multiplayer;        // TickCommandPacket, ReadyPacketRouting, HandshakeGate, HaltReason
using ProjectChimera.Multiplayer.Server; // ServerLobbyPolicy
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-443 — the START-PATH half of the Story-9.14 team fold. <see cref="ScenarioPlayerSlot.Team"/> is folded ONLY
    /// into <see cref="MatchAgreementHash"/>; it is deliberately EXCLUDED from <see cref="CanonicalModelHash"/>. That
    /// exclusion is safe ONLY while every match-start / peer-agreement gate compares the AGREEMENT hash — a gate that
    /// compared the canonical model hash (or any single component of the agreement value) would see two peers with
    /// different team layouts as identical, start the match, seed divergent <c>AllianceStore</c> masks, and desync at
    /// tick 0. <c>MatchAgreementHashTests</c> already pins that the VALUE moves on a team change; nothing pinned
    /// that the START PATHS actually consume that value, so deleting the team fold's protection by rewiring a gate to
    /// the model hash would have shipped with a fully green suite.
    ///
    /// <para>Three layers, so the property is enforced end-to-end and for gates that do not exist yet:</para>
    /// <list type="number">
    ///   <item><b>The premise</b> — two models differing ONLY in <c>Team</c> hash IDENTICALLY under
    ///     <see cref="CanonicalModelHash"/> (both 64-bit and the 32-bit wire fold) and DIFFERENTLY under
    ///     <see cref="MatchAgreementHash"/>. Without this the rest would be vacuous.</item>
    ///   <item><b>The two live gates, driven with the REAL hashes</b> — the P2P route
    ///     (<see cref="ReadyPacketRouting.Route"/> → <see cref="HandshakeGate.CheckStart"/>, over bytes built by the
    ///     real <see cref="TickCommandPacket.MakeReady"/> codec) and the server-attested gate
    ///     (<see cref="ServerLobbyPolicy.CheckStartStateAgreement"/>, both the dense and the slot-map overload) must
    ///     REJECT a team-only disagreement, and each is paired with the counterfactual proving the same call ALLOWS
    ///     when fed model-hash values — i.e. the rejection comes from the agreement hash, not from the fixture.</item>
    ///   <item><b>The "EVERY gate" audit</b> — a source scan over <c>godot/src/**</c> asserting no start-gate call
    ///     site (and no Ready-packet payload) is fed a <see cref="CanonicalModelHash"/> / <see cref="StartStateHash"/>
    ///     / <see cref="ContentHash"/> / <see cref="RulesetHash"/>-derived value. The two production call sites are
    ///     Godot-coupled (<c>LobbyUi</c> / <c>DedicatedServer</c>) so Tier-1 cannot execute them; the scan is how the
    ///     "every start path" clause is enforced, including for a start path added later.</item>
    /// </list>
    /// Godot-free, integer-only; nothing here is folded into any golden.
    /// </summary>
    public class StartPathTeamAgreementTests
    {
        private const ushort V = TickCommandPacket.PROTOCOL_VERSION;

        /// <summary>The match's initial input delay. A literal, not <c>LockstepManager.INPUT_DELAY</c> — that type is
        /// Godot-coupled; <see cref="MatchAgreementHash.Compute"/> takes the delay as a parameter for exactly this
        /// reason. Both "peers" fold the SAME delay, so the delay component can never be the source of a mismatch.</summary>
        private const int InitialDelay = 4;

        private static readonly List<FactionDefinition> NoFactions = new();

        // ── Fixtures ──────────────────────────────────────────────────────────────

        /// <summary>A 4-slot model. Every field except <see cref="ScenarioPlayerSlot.Team"/> is identical across
        /// calls, and each call allocates FRESH slot objects so teaming one model can never leak into the other.</summary>
        private static ScenarioData BuildModel(int players = 4)
        {
            var slots = new ScenarioPlayerSlot[players];
            for (int i = 0; i < players; i++)
                slots[i] = new ScenarioPlayerSlot
                {
                    Slot = i, FactionJson = $"res://f{i}.json",
                    StartOre = 200f, StartCrystal = 50f, BaseX = -45f + i * 30f, BaseZ = 0f,
                };
            return new ScenarioData
            {
                Id = "team-agreement", DisplayName = "team-agreement", TerrainRef = "res://t.tres", MapBounds = 120f,
                WinCondition = WinCondition.EliminateAllUnits,
                PlayerSlots = slots,
                ResourceNodes = new[] { new ScenarioResourceNode { X = -20f, Z = -10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            };
        }

        /// <summary>The SAME model as <see cref="BuildModel"/>, teamed 2v2 ({slot0,slot1} vs {slot2,slot3}). The
        /// canonical seeded mask differs from FFA (P2's id becomes 1, P4's becomes 3), which is precisely the mask
        /// <c>AllianceSeeder</c> writes into the sim — so the two peers would run DIFFERENT alliance rules.</summary>
        private static ScenarioData BuildTeamedModel()
        {
            ScenarioData m = BuildModel();
            m.PlayerSlots[0].Team = 1;
            m.PlayerSlots[1].Team = 1;
            m.PlayerSlots[2].Team = 2;
            m.PlayerSlots[3].Team = 2;
            return m;
        }

        /// <summary>A NON-CONTIGUOUS roster (slot 2 removed → {0,1,3,4}) with the team on the two GAPPED-region high
        /// slots — the layout a positional fold missed (9.14 review) and the layout DW-397's slot-map gate overload
        /// exists for. Included so the start-path assertion covers the sparse case, not only the dense one.</summary>
        private static ScenarioData BuildGappedModel(bool teamed)
        {
            ScenarioData m = BuildModel(players: 5);
            m.RemoveStartSlot(2);
            if (teamed)
                foreach (ScenarioPlayerSlot s in m.PlayerSlots)
                    if (s.Slot == 3 || s.Slot == 4) s.Team = 1;
            return m;
        }

        private static HeroStore Heroes()
        {
            var s = new HeroStore();
            s.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));
            s.Mint(new HeroId(2_000_000_011UL), entityId: 8, level: 1, xp: Fixed.FromInt(0));
            return s;
        }

        /// <summary>The value a peer puts on its Ready packet: the full 64-bit match-agreement hash over its OWN
        /// applied model. Both peers fold identical content/heroes/delay, so the ONLY difference between the two
        /// peers in these tests is the per-slot team layout.</summary>
        private static ulong Agreement(ScenarioData model) =>
            MatchAgreementHash.Compute(InitialDelay, model, Heroes(),
                NoFactions, AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default,
                ProjectChimera.AI.AiControlPlan.None); // DW-908: the online plan — this file varies the TEAM layout only

        // ── 1. The premise: the model hash is BLIND to a team-only disagreement ───

        [Fact]
        public void TeamOnlyDifference_IsInvisibleToCanonicalModelHash_ButMovesTheAgreementHash()
        {
            ScenarioData ffa = BuildModel();
            ScenarioData teamed = BuildTeamedModel();

            // The exclusion this whole entry rests on: Team is NOT folded into the canonical model hash, so the two
            // peers' models are indistinguishable to it — 64-bit AND the 32-bit Ready-wire fold the pre-9.4 gate used.
            Assert.Equal(CanonicalModelHash.Compute(ffa), CanonicalModelHash.Compute(teamed));
            Assert.Equal(CanonicalModelHash.ToWire(CanonicalModelHash.Compute(ffa)),
                         CanonicalModelHash.ToWire(CanonicalModelHash.Compute(teamed)));

            // …and the seeded alliance masks genuinely differ, so starting the match WOULD desync (this is the
            // real-consequence pin, not a hash tautology: it reads the same AllianceSeeder mapping the sim runs).
            Assert.NotEqual(AllianceSeeder.ComputeTeamIds(ffa), AllianceSeeder.ComputeTeamIds(teamed));

            // Only the agreement hash separates them.
            Assert.NotEqual(Agreement(ffa), Agreement(teamed));
        }

        [Fact]
        public void GappedRoster_TeamOnlyDifference_IsAlsoInvisibleToCanonicalModelHash()
        {
            ScenarioData ffa = BuildGappedModel(teamed: false);
            ScenarioData teamed = BuildGappedModel(teamed: true);

            Assert.Equal(CanonicalModelHash.Compute(ffa), CanonicalModelHash.Compute(teamed));
            Assert.NotEqual(Agreement(ffa), Agreement(teamed));
        }

        // ── 2a. The P2P start path (LobbyUi's Ready handler → ReadyPacketRouting → HandshakeGate) ─

        [Fact]
        public void P2PStartPath_TeamOnlyDisagreement_IsRejectedBeforeTick0()
        {
            ulong local = Agreement(BuildModel());        // this peer loaded the FFA layout
            ulong peer  = Agreement(BuildTeamedModel());  // the peer loaded the 2v2 layout

            byte[] ready = TickCommandPacket.MakeReady(V, peer); // the REAL wire bytes the peer would send
            ReadyRouteDecision d = ReadyPacketRouting.Route(ready, ready.Length, local, null, isHostRole: true);

            Assert.False(d.PeerReady);   // never marked ready
            Assert.Equal(-1, d.PeerSlot);
            Assert.NotNull(d.BlockReason);
            Assert.Contains("MISMATCH", d.BlockReason);
        }

        [Fact]
        public void P2PStartPath_GappedRosterTeamDisagreement_IsRejectedBeforeTick0()
        {
            ulong local = Agreement(BuildGappedModel(teamed: false));
            ulong peer  = Agreement(BuildGappedModel(teamed: true));

            byte[] ready = TickCommandPacket.MakeReady(V, peer);
            ReadyRouteDecision d = ReadyPacketRouting.Route(ready, ready.Length, local, null, isHostRole: true);

            Assert.False(d.PeerReady);
            Assert.Contains("MISMATCH", d.BlockReason!);
        }

        [Fact]
        public void P2PStartPath_AgreedTeamLayout_Starts()
        {
            // Negative control: two peers that agree on the SAME 2v2 layout must still start — the gate rejects
            // disagreement, not teams. Without this a gate that blocked everything would pass the tests above.
            ulong local = Agreement(BuildTeamedModel());
            ulong peer  = Agreement(BuildTeamedModel());
            Assert.Equal(local, peer);

            byte[] ready = TickCommandPacket.MakeReady(V, peer);
            ReadyRouteDecision d = ReadyPacketRouting.Route(ready, ready.Length, local, null, isHostRole: true);

            Assert.True(d.PeerReady);
            Assert.Equal(1, d.PeerSlot);
            Assert.Null(d.BlockReason);
        }

        [Fact]
        public void P2PStartPath_FedCanonicalModelHashes_WouldFailOPEN_TheDefectThisPins()
        {
            // The counterfactual that makes the rejection above meaningful: run the IDENTICAL gate over the values a
            // CanonicalModelHash-based start path would exchange. The team-only disagreement sails through and the
            // peer is marked READY — a tick-0 desync with a green handshake. This is documentation-by-assertion of
            // the failure mode, and it fails if Team ever starts folding into CanonicalModelHash (at which point the
            // ledger's premise, and this file's framing, would need revisiting rather than silently rotting).
            ulong local = CanonicalModelHash.Compute(BuildModel());
            ulong peer  = CanonicalModelHash.Compute(BuildTeamedModel());

            byte[] ready = TickCommandPacket.MakeReady(V, peer);
            ReadyRouteDecision d = ReadyPacketRouting.Route(ready, ready.Length, local, null, isHostRole: true);

            Assert.True(d.PeerReady);   // ← the fail-OPEN start the agreement hash exists to prevent
            Assert.Null(d.BlockReason);
        }

        [Fact]
        public void HandshakeGate_Directly_RejectsTheTeamOnlyDisagreement()
        {
            // The pure decision under the same real values (the route above also exercises the codec + version gate;
            // this isolates CheckStart so a routing change can't mask a gate change).
            string? reason = HandshakeGate.CheckStart(Agreement(BuildModel()), Agreement(BuildTeamedModel()));
            Assert.NotNull(reason);
            Assert.Contains("MISMATCH", reason);
        }

        // ── 2b. The server-attested start path (DedicatedServer → ServerLobbyPolicy) ─

        [Fact]
        public void ServerAttestedGate_TeamOnlyDisagreement_HaltsInsteadOfStarting()
        {
            var hashes   = new[] { Agreement(BuildModel()), Agreement(BuildTeamedModel()) };
            var versions = new[] { V, V };

            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, 2));
        }

        [Fact]
        public void ServerAttestedGate_SlotMapOverload_TeamOnlyDisagreement_HaltsInsteadOfStarting()
        {
            // DW-397's slot-map overload is a SECOND gate entry point — a fix applied to only one of the two would
            // leave the non-contiguous lobby (Story 9.15 open slots) starting on a team disagreement.
            var hashes   = new ulong[4];
            var versions = new ushort[4];
            hashes[0] = Agreement(BuildGappedModel(teamed: false)); versions[0] = V;
            hashes[3] = Agreement(BuildGappedModel(teamed: true));  versions[3] = V;

            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 3 }));
        }

        [Fact]
        public void ServerAttestedGate_AgreedTeamLayout_Starts()
        {
            var hashes   = new[] { Agreement(BuildTeamedModel()), Agreement(BuildTeamedModel()) };
            var versions = new[] { V, V };

            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, 2));
            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 1 }));
        }

        [Fact]
        public void ServerAttestedGate_FedCanonicalModelHashes_WouldFailOPEN_TheDefectThisPins()
        {
            var hashes   = new[] { CanonicalModelHash.Compute(BuildModel()), CanonicalModelHash.Compute(BuildTeamedModel()) };
            var versions = new[] { V, V };

            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, 2));           // ← starts anyway
            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 1 }));
        }

        // ── 3. The "EVERY start path" source audit ────────────────────────────────

        /// <summary>The gate entry points a match start must go through. A call to any of them IS a start-state gate,
        /// wherever it lives. Bare method names for the two gate decisions so ANY qualification form is caught
        /// (<c>CheckStartStateAgreement</c>, <c>ServerLobbyPolicy.CheckStartStateAgreement</c>,
        /// <c>Server.ServerLobbyPolicy.CheckStartStateAgreement</c> — the form <c>DedicatedServer</c> actually uses);
        /// the two wrappers are qualified because their bare names are too generic. The declarations themselves also
        /// match (their parameter lists read as an argument list) — harmless: a parameter list names types, never a
        /// hash source.</summary>
        private static readonly string[] StartGateCalls =
        {
            "CheckStart",                         // HandshakeGate.CheckStart — the P2P decision
            "CheckStartStateAgreement",           // ServerLobbyPolicy — the server-attested decision (both overloads)
            "ReadyPacketRouting.Route",           // the wrapper that marshals into CheckStart
            "TickCommandPacket.MakeReady",        // the producer of the value the gates compare
        };

        /// <summary>Hash sources that are NOT the agreement value: the canonical model hash (Team-blind by design)
        /// and every individual COMPONENT of the agreement hash (each is a strict subset — folding only one back into
        /// a gate would silently narrow the surface the start is proven over). <c>MatchAgreementHash</c> is the only
        /// legal source and matches none of these.</summary>
        private static readonly string[] BannedGateHashSources =
        {
            "CanonicalModelHash", "ScenarioHash", "StartStateHash", "ContentHash", "RulesetHash", "ToWire",
        };

        [Fact]
        public void EveryStartGateCallSite_IsFedTheAgreementHash_NeverAModelHash()
        {
            string src = Path.Combine(LocateGodotRoot(), "src");
            var offenders = new List<string>();
            int callSitesSeen = 0;

            foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string code = StripCommentsAndLiterals(File.ReadAllText(file));
                foreach (string call in StartGateCalls)
                    foreach (string args in ExtractCallArguments(code, call))
                    {
                        callSitesSeen++;
                        foreach (string banned in BannedGateHashSources)
                            if (args.Contains(banned, StringComparison.Ordinal))
                                offenders.Add($"{Path.GetFileName(file)}: {call}({args.Trim()}) references {banned}");
                    }
            }

            // Sanity: the scan must actually be finding the production call sites (a broken locator/stripper would
            // otherwise report a vacuous pass). LobbyUi's MakeReady + Route, DedicatedServer's gate, and
            // ReadyPacketRouting's CheckStart are the floor.
            Assert.True(callSitesSeen >= 4, $"Expected to find the start-gate call sites under {src}; found {callSitesSeen}.");
            Assert.Empty(offenders);
        }

        [Fact]
        public void LobbyUi_SendsAndComparesTheMatchAgreementHash()
        {
            // The P2P path's two Godot-coupled halves, which Tier-1 cannot execute: the OUTBOUND Ready payload and
            // the INBOUND route's local-hash argument must both be the agreement hash. Rewiring either to
            // ScenarioHash (the pre-9.4 value LobbyUi still carries for status text) reopens DW-443 exactly.
            string lobby = StripCommentsAndLiterals(
                File.ReadAllText(Path.Combine(LocateGodotRoot(), "src", "Multiplayer", "LobbyUi.cs")));

            List<string> ready = ExtractCallArguments(lobby, "TickCommandPacket.MakeReady");
            Assert.Single(ready);
            Assert.Equal("MatchAgreementHash", SplitArgs(ready[0])[1].Trim());

            List<string> route = ExtractCallArguments(lobby, "ReadyPacketRouting.Route");
            Assert.Single(route);
            Assert.Equal("MatchAgreementHash", SplitArgs(route[0])[2].Trim());
        }

        [Fact]
        public void DedicatedServer_GatesOnTheHashItReadOffTheReadyWire()
        {
            // The server-attested half: the per-slot value handed to the gate must be the one parsed off the Ready
            // packet (the peer-attested agreement hash), never a hash the server recomputes from its own model.
            string server = StripCommentsAndLiterals(
                File.ReadAllText(Path.Combine(LocateGodotRoot(), "src", "Multiplayer", "DedicatedServer.cs")));

            Assert.Matches(new Regex(@"TryReadReady\([^)]*out\s+ulong\s+readyHash\s*\)"), server);
            Assert.Matches(new Regex(@"_readyHash\[\s*slot\s*\]\s*=\s*readyHash\s*;"), server);

            List<string> gate = ExtractCallArguments(server, "CheckStartStateAgreement");
            Assert.Single(gate);
            Assert.Equal("_readyHash", SplitArgs(gate[0])[0].Trim());
        }

        // ── Source-scan helpers ───────────────────────────────────────────────────

        /// <summary>Walk up from the test-assembly directory to the <c>godot/</c> dir (the one holding both
        /// <c>src</c> and <c>scenes</c>) — the <c>SecretExclusionTest.LocateGodotRoot</c> precedent.</summary>
        private static string LocateGodotRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "scenes")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate the godot/ root (a dir with both src/ and scenes/) above {AppContext.BaseDirectory}");
        }

        /// <summary>Blank out comments, string literals and char literals (replacing each with spaces so offsets and
        /// line structure survive) so the scan reads CODE only. Without it every <c>&lt;see cref="CanonicalModelHash"/&gt;</c>
        /// doc reference beside a gate call would read as an offender, and the guard would be un-keepable.</summary>
        private static string StripCommentsAndLiterals(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    sb.Append("  "); i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    { sb.Append(text[i] == '\n' ? '\n' : ' '); i++; }
                    if (i < text.Length) { sb.Append("  "); i += 2; }
                    continue;
                }
                if (c == '@' && i + 1 < text.Length && text[i + 1] == '"') // verbatim string: "" is an escaped quote
                {
                    sb.Append("  "); i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                            sb.Append(' '); i++; break;
                        }
                        sb.Append(text[i] == '\n' ? '\n' : ' '); i++;
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    sb.Append(' '); i++;
                    while (i < text.Length && text[i] != quote)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append("  "); i += 2; continue; }
                        sb.Append(text[i] == '\n' ? '\n' : ' '); i++;
                    }
                    if (i < text.Length) { sb.Append(' '); i++; }
                    continue;
                }

                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        /// <summary>Every argument list passed to <paramref name="call"/> in <paramref name="code"/> (already
        /// comment/literal-stripped), read to the BALANCED closing paren so a nested call is captured whole. A
        /// preceding <c>.</c> is ALLOWED (a qualified call site is the same call site), but a preceding identifier
        /// character is not, so a longer method name merely ENDING with the probe never matches. The probe must be
        /// followed — modulo whitespace — by <c>(</c>, so <c>CheckStart</c> does not match inside
        /// <c>CheckStartStateAgreement</c>.</summary>
        private static List<string> ExtractCallArguments(string code, string call)
        {
            var results = new List<string>();
            int from = 0;
            while (true)
            {
                int at = code.IndexOf(call, from, StringComparison.Ordinal);
                if (at < 0) break;
                from = at + call.Length;

                if (at > 0 && (char.IsLetterOrDigit(code[at - 1]) || code[at - 1] == '_')) continue;

                int p = from;
                while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
                if (p >= code.Length || code[p] != '(') continue;

                int depth = 0, start = p + 1, end = -1;
                for (int q = p; q < code.Length; q++)
                {
                    if (code[q] == '(') depth++;
                    else if (code[q] == ')') { depth--; if (depth == 0) { end = q; break; } }
                }
                if (end < 0) continue;
                results.Add(code.Substring(start, end - start));
                from = end + 1;
            }
            return results;
        }

        /// <summary>Split an extracted argument list on TOP-LEVEL commas (a nested call's commas stay put). Depth is
        /// tracked on <c>()</c>/<c>[]</c> only — never on angle brackets, which are far more often comparison
        /// operators than generic argument lists in an expression position.</summary>
        private static List<string> SplitArgs(string args)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) { parts.Add(args.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(args.Substring(start));
            return parts;
        }
    }
}

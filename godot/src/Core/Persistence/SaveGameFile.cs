#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjectChimera.AI;               // AiDifficulty
using ProjectChimera.Core.Definitions; // CanonicalModelHash, ContentHash, StartStateHash algo pins
using ProjectChimera.Core.Skirmish;    // SkirmishSetup, SetupSlot, SlotKind

namespace ProjectChimera.Core.Persistence
{
    /// <summary>
    /// Story 11.3 (FR-67) — the versioned, fail-closed <c>.chsav</c> binary container for an SP mid-match save. Mirrors
    /// the <c>.chmr</c> replay container: a fixed header (magic + formatVersion + drift stamps + the launch record) then
    /// the length-framed, type-tagged body (<see cref="SaveGameState.WriteBody"/>). The reader throws
    /// <see cref="InvalidDataException"/> with a clear, user-facing message on bad magic, an older/newer
    /// <c>formatVersion</c>, a mismatched hash <c>AlgoVersion</c>, an unknown section tag, or truncation — never a silent
    /// partial load or a desyncing best-effort resume (the <c>ReplayPlayer</c> precedent).
    ///
    /// <para><b>Two-layer gating</b> (the <c>ReplayPlayer.ScenarioGateBlockReason</c> precedent): <see cref="Read"/>
    /// throws on STRUCTURAL corruption + build-version drift (magic / formatVersion / algo pins that are build
    /// constants). The CONTENT-VALUE drift check (this save's <c>CanonicalModelHash</c>/<c>ContentHash</c> vs the
    /// currently-loaded content) needs the rebuilt scenario, so it is a separate step:
    /// <see cref="SaveGameHeaderData.ThrowIfContentMismatch"/>, run by the loader after re-resolving content.</para>
    ///
    /// <para><b>1.0 portability note:</b> saves are SAME-MACHINE in 1.0. The AI opponent still runs a float-based
    /// scorer (a pre-existing AI-float determinism limitation); its per-match decision state is captured/restored as
    /// raw ints, so a resume on the SAME machine is byte-identical, but cross-machine portability of an AI-bearing
    /// save is not guaranteed until the AI goes Fixed-point.</para>
    /// </summary>
    public static class SaveGameFile
    {
        /// <summary>Magic 'C','H','S','V' (little-endian uint on disk).</summary>
        public const uint MAGIC = 0x56534843u;

        /// <summary>Current on-disk format version. A bump is a documented save-break (fail-closed), NOT a migrate.
        /// <para>v2 (DW-581): the entity section gained a <c>Generation</c> lane (the per-slot recycle generation
        /// backing <c>EntityWorld.PackRef</c>/<c>TryResolveRef</c>). A v1 body carries one fewer entity lane, so
        /// without this bump it would parse and then be rejected by <c>SaveGameState.Validate</c> as a CORRUPT save
        /// ("entity lane count mismatch") and would still be listed as readable by <c>SaveGameHeader</c> — the bump
        /// makes an older save fail with the accurate "made by an older game version" message, at the header, before
        /// the body is read.</para>
        /// <para>v3 (DW-548, post-merge review fix): the Director section gained the four deferred trigger-phase
        /// death-rail lanes. A v2 body ends the Director frame after <c>DirFirstTick</c>, so without this bump it
        /// would fail as a TRUNCATED SECTION — technically fail-closed, but with a "corrupt save" message for a save
        /// that is merely older. Same reasoning as v2.</para>
        /// <para>v4 (Story 15.11, DW-280): the entity section gained the <c>PendingCastPointX</c>/<c>PendingCastPointZ</c>
        /// lanes (the transient ground-cast point), inserted mid-order in the <c>SaveGameState.EA</c> lane enum. A v3
        /// body carries the OLD lane ordering, so WITHOUT this bump it would parse at the same format version and
        /// silently MISALIGN every entity lane after PendingCastTarget. The bump makes a pre-15.11 save fail-closed at
        /// the header with the accurate "older game version" message, before any lane is read. Same reasoning as v2/v3.</para>
        /// <para>v5 (Story 15.12, DW-265): the entity section gained the <c>RegenRate</c> lane (flat energy regen),
        /// inserted mid-order in the <c>SaveGameState.EA</c> lane enum (between <c>MaxEnergy</c> and <c>StatusFlags</c>).
        /// A v4 body carries the OLD lane ordering, so WITHOUT this bump the positional <c>A(EA.X, n)</c> addressing
        /// would silently MISALIGN every entity lane from RegenRate onward (RegenRate would read StatusFlags bytes, and
        /// so on). The bump makes a pre-15.12 save fail-closed at the header with the "older game version" message,
        /// before any lane is read. Same reasoning as v2/v3/v4.</para>
        ///
        /// <para>v6 (DW-937): the building section gained the <c>RequiresBuilder</c> lane (worker-built sites only
        /// advance construction while a builder is present), appended at the tail of the <c>SaveGameState.BA</c>
        /// lane enum. A v5 body has one fewer building lane, so the positional addressing would misalign; the bump
        /// makes a pre-DW-937 save fail-closed at the header. Same reasoning as v5.</para>
        ///
        /// <para>v7 (Story 15-21): the hero section gained the <c>AttrStatBase</c>/<c>AttrStatPerLevel</c> lanes
        /// (per-hero resolved attribute contributions, stride-<c>AttributeStats.Count</c> flat rings), appended at
        /// the tail of the <c>SaveGameState.HA</c> lane enum. A v6 body has two fewer hero lanes → positional
        /// misalignment; the bump fail-closes a pre-15-21 save at the header. Same reasoning as v6. (The same
        /// story's SimChecksum stayed at 25 — attributes add NO folded state — so this is the save gate's only
        /// 15-21 movement.)</para>
        ///
        /// <para>v8 (DW-690): the ENTITY section gained the <c>RallyMovePending</c> lane (the outstanding rally first
        /// leg of a trained worker — the one input to DW-634's stand-down gate the save used to drop, so a mid-leg
        /// worker reloaded with its Flags/CommandState/MoveTarget intact but its gate off and had the player's rally
        /// silently discarded on the next tick). Appended at the tail of the per-entity half of the
        /// <see cref="SaveGameState"/> <c>EA</c> lane enum (still before <c>PatrolWpX</c>, where the flat/strided half
        /// begins). A v7 body has one fewer entity lane → positional misalignment; the bump fail-closes a pre-DW-690
        /// save at the header. Same reasoning as v5/v6/v7. No fold changes, so no golden moves.</para>
        ///
        /// <para>v9 (DW-804): the entity section gained the <c>GatherWalkStall</c> lane (the DW-532 walk-stall streak
        /// and its SLOT_YIELDED sentinel), appended after <c>RallyMovePending</c> at the tail of the per-entity half of
        /// the <c>SaveGameState.EA</c> lane enum — i.e. BEFORE the flat-stride lanes, which therefore all shift by one.
        /// A v8 body would misalign every lane from <c>PatrolWpX</c> onward; the bump fail-closes a pre-DW-804 save at
        /// the header. The lane is unfolded (it is NOT in SimChecksum), so this bump moves no golden — it exists
        /// because the lane pairs with the FOLDED node-side <c>AssignedGatherers</c> counter, and restoring one half
        /// without the other let a saved yielder gather past a node's capacity with no reservation at all. Same
        /// reasoning as v7/v8.</para>
        ///
        /// <para>v10 (Story 15-24a, 2026-08-12 — the StatVocabulary pipeline): the entity section gained SIX lanes
        /// (<c>EffAttackSpeedFactor</c>, <c>EffCooldownReduction</c>, <c>BaseHealthRegen</c>, <c>EffHealthRegen</c>,
        /// <c>VisionBonusFlat</c>, <c>VisionBonusPct</c> — the new stats' identity-default modifier-term channels +
        /// health regen's authored base), appended after
        /// <c>GatherWalkStall</c> at the tail of the per-entity half of the <c>SaveGameState.EA</c> lane enum (still
        /// before <c>PatrolWpX</c>); the HERO section's <c>AttrStatBase</c>/<c>AttrStatPerLevel</c> rings re-stride
        /// from 6 to <c>StatVocabulary.Count</c> (the registry supersedes the closed 6-stat AttributeStats list); and
        /// the research section's cumulative lanes generalize per-stat. A v9 body would misalign every lane from the
        /// new entries onward; the bump fail-closes a pre-15-24a save at the header — which the SAME story's
        /// SimChecksum 25→26 / CanonicalModelHash 16→17 / ContentHash 2→3 pins would reject anyway (DW-874: one
        /// constant, fail-closed, no migrate).</para>
        ///
        /// <para>v11 (Story 15-24b, 2026-08-13 — the deterministic combat dice): the entity section gained THREE
        /// lanes (<c>EffCritChance</c>, <c>EffDodgeChance</c>, <c>EffCritBonus</c> — the dice channels, restored
        /// with registry-domain re-clamps), appended after <c>VisionBonusPct</c>, still before <c>PatrolWpX</c>;
        /// and the hero attribute rings re-stride again with the registry (14 → 17 stats). A v10 body would
        /// misalign from the new entries onward; the bump fail-closes it — which the same story's SimChecksum
        /// 26→27 pin rejects anyway (DW-874).</para>
        ///
        /// <para>v12 (DW-997, 2026-08-13): the <c>Modifiers</c> frame gained a BY-VALUE entry kind (2) for a
        /// RUNTIME-MINTED modifier — the descriptor an item pickup / research completion / hero level-up creates,
        /// which no effect-graph walk can reach, so capture used to THROW the fail-closed "needs a content-model
        /// change" error and NO save could be taken while any of those was live (shipped <c>ring_of_vigor</c>
        /// reached it). A minted entry now carries its shape + canonical sparse stat vector inline. Entries of the
        /// two by-index kinds are written exactly as before, so a save with no minted modifier has a
        /// byte-identical Modifiers frame — the bump exists because an OLD reader would mis-parse a NEW blob that
        /// does contain one.</para></summary>
        public const ushort FormatVersion = 12;

        /// <summary>Max player slots in a persisted launch record — a fail-closed corruption bound on the slot count.</summary>
        public const int MaxSlots = 64;

        /// <summary>Write <paramref name="state"/> under <paramref name="header"/> to <paramref name="stream"/>. The body
        /// is serialized to a buffer first so a <see cref="Fnv64"/> integrity checksum over it can be stamped in the
        /// header (#3) — a flipped body byte then fails the check on load rather than loading as valid state.</summary>
        public static void Write(Stream stream, SaveGameState state, SaveGameHeaderData header)
        {
            byte[] body;
            using (var bodyMs = new MemoryStream())
            {
                using (var bw = new BinaryWriter(bodyMs, Encoding.UTF8, leaveOpen: true)) { state.WriteBody(bw); bw.Flush(); }
                body = bodyMs.ToArray();
            }
            ulong bodyHash = Fnv64(body);

            using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            w.Write(MAGIC);
            w.Write(FormatVersion);
            w.Write(SimChecksum.AlgoVersion);
            w.Write(CanonicalModelHash.AlgoVersion);
            w.Write(StartStateHash.AlgoVersion);
            w.Write(header.CanonicalModelHash);
            w.Write(header.ContentHash);
            w.Write(bodyHash); // #3 — integrity over the framed body
            w.Write(header.Tick);
            // Launch record (SkirmishSetup): MapId + per-slot Kind/FactionId/Team/Ai.
            w.Write(header.MapId ?? "");
            w.Write(header.Slots?.Count ?? 0);
            if (header.Slots != null)
                foreach (SetupSlot slot in header.Slots)
                {
                    w.Write(slot.Slot);
                    w.Write((byte)slot.Kind);
                    w.Write((byte)slot.Ai);
                    w.Write(slot.Team);
                    w.Write(slot.FactionId ?? "");
                }
            w.Write(body); // the framed body (already terminated by its zero-length frame)
            w.Flush();
        }

        /// <summary>FNV-1a 64-bit over a byte buffer — the body integrity checksum (#3). Not a security MAC; a tripwire
        /// for accidental corruption / a flipped byte that primitive framing would otherwise load as valid state.</summary>
        internal static ulong Fnv64(byte[] data)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= 1099511628211UL; }
            return h;
        }

        /// <summary>Read a <c>.chsav</c> fail-closed. Throws <see cref="InvalidDataException"/> on bad magic, an
        /// older/newer format version, a mismatched hash algo version, truncation, or an unknown body section tag.</summary>
        public static (SaveGameHeaderData header, SaveGameState state) Read(Stream stream, string ctx = "save")
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            SaveGameHeaderData header;
            try
            {
                uint magic = r.ReadUInt32();
                if (magic != MAGIC)
                    throw new InvalidDataException($"Save '{ctx}': not a Chimera save file (bad magic 0x{magic:X8}).");

                ushort version = r.ReadUInt16();
                if (version < FormatVersion)
                    throw new InvalidDataException($"Save '{ctx}': made by an older game version (save format v{version}) — it can no longer be loaded.");
                if (version > FormatVersion)
                    throw new InvalidDataException($"Save '{ctx}': made by a newer game version (save format v{version}) than this build supports.");

                int simAlgo   = r.ReadInt32();
                int modelAlgo = r.ReadInt32();
                int startAlgo = r.ReadInt32();
                // Fail-closed BY DESIGN — decision DW-874 (Alec, 2026-08-06). These three constants gate save
                // LOADING, so any of them moving rejects the save even when its own body format is untouched.
                // That includes a pure golden re-record marker (the v23 -> v24 bump was exactly that: the fold
                // was byte-for-byte identical and SimChecksumCoverageGuardTest's known-state hash never moved).
                // Deliberately NOT split into a separate save-only world-format constant: while the sim still
                // changes every epic, a save that silently RESUMES under corrected combat/AI rules is a worse
                // and far harder-to-diagnose failure than one that cleanly refuses to open. Revisit before 1.0
                // ships to players, when re-records stop being routine. Do not "fix" this by loosening the gate.
                // The message names the actual version pairs rather than claiming the format changed, because
                // on a re-record bump it has not — only the folded values have.
                if (simAlgo != SimChecksum.AlgoVersion || modelAlgo != CanonicalModelHash.AlgoVersion || startAlgo != StartStateHash.AlgoVersion)
                    throw new InvalidDataException(
                        $"Save '{ctx}': made by a different simulation build (save: sim v{simAlgo}, model v{modelAlgo}, start v{startAlgo}; " +
                        $"this build: sim v{SimChecksum.AlgoVersion}, model v{CanonicalModelHash.AlgoVersion}, start v{StartStateHash.AlgoVersion}) — it can no longer be loaded.");

                header = new SaveGameHeaderData
                {
                    FormatVersion       = version,
                    CanonicalModelHash  = r.ReadUInt64(),
                    ContentHash         = r.ReadUInt64(),
                    BodyHash            = r.ReadUInt64(), // #3 — expected body integrity checksum
                    Tick                = r.ReadUInt32(),
                    MapId               = r.ReadString(),
                };
                int slotCount = r.ReadInt32();
                if (slotCount < 0 || slotCount > MaxSlots)
                    throw new InvalidDataException($"Save '{ctx}': corrupt launch record (slot count {slotCount}).");
                header.Slots = new List<SetupSlot>(slotCount);
                for (int i = 0; i < slotCount; i++)
                {
                    header.Slots.Add(new SetupSlot
                    {
                        Slot      = r.ReadInt32(),
                        Kind      = (SlotKind)r.ReadByte(),
                        Ai        = (AiDifficulty)r.ReadByte(),
                        Team      = r.ReadInt32(),
                        FactionId = r.ReadString(),
                    });
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"Save '{ctx}': truncated header.");
            }

            // #3 — read the remaining bytes as the body, verify the integrity checksum BEFORE parsing/restoring, then
            // parse from the buffer. A flipped body byte (or a truncated tail) fails here fail-closed.
            long remaining = stream.Length - stream.Position;
            if (remaining < 0) remaining = 0;
            byte[] body = r.ReadBytes((int)remaining);
            if (Fnv64(body) != header.BodyHash)
                throw new InvalidDataException($"Save '{ctx}': body integrity check failed — the save is corrupted.");
            using var bodyMs = new MemoryStream(body);
            using var br = new BinaryReader(bodyMs, Encoding.UTF8, leaveOpen: true);
            SaveGameState state = SaveGameState.ReadBody(br, ctx);
            return (header, state);
        }
    }

    /// <summary>Story 11.3 — the <c>.chsav</c> header contents: the drift stamps + the launch record needed to
    /// rebuild the scenario on load and to render slot metadata. A plain DTO (Godot-free).</summary>
    public sealed class SaveGameHeaderData
    {
        public ushort FormatVersion;
        public ulong  CanonicalModelHash;
        public ulong  ContentHash;
        public ulong  BodyHash;
        public uint   Tick;
        public string MapId = "";
        public List<SetupSlot> Slots = new();

        /// <summary>Rebuild a <see cref="SkirmishSetup"/> from the persisted launch record (the load path re-runs
        /// <c>SkirmishSetupToScenario.Build</c> from it to reconstruct the identical scenario).</summary>
        public SkirmishSetup ToSkirmishSetup() => new() { MapId = MapId, Slots = Slots };

        /// <summary>Fail-closed CONTENT-drift gate (the <c>ReplayPlayer.ScenarioGateBlockReason</c> precedent): throws
        /// <see cref="InvalidDataException"/> with a user-facing message when the save's map/content hash no longer
        /// matches the currently-loaded content. Run by the loader after re-resolving the scenario + content.</summary>
        public void ThrowIfContentMismatch(ulong currentModelHash, ulong currentContentHash, string ctx = "save")
        {
            if (CanonicalModelHash != currentModelHash)
                throw new InvalidDataException($"Save '{ctx}': the map this save used has changed and no longer matches — it can no longer be loaded.");
            if (ContentHash != currentContentHash)
                throw new InvalidDataException($"Save '{ctx}': the content (units/abilities/items) this save used has changed and no longer matches — it can no longer be loaded.");
        }
    }
}

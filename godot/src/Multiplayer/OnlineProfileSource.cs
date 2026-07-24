#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProjectChimera.Core.Definitions; // IProfileSource, PlayerProfile, StorageWriteResult

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 9.12 (FR-7c / AR-12) — the ONLINE hero-profile rail: an <see cref="IProfileSource"/> over
    /// <see cref="NakamaService"/> whose ONLY write path is the validating server RPC
    /// (<see cref="NakamaService.WriteHeroProfileViaRpcAsync"/>) — it NEVER calls <c>WriteStorageObjects</c>.
    ///
    /// <para><b>Tamper model (P8 — precise).</b> The guarantee that a client cannot enter online play with a forged hero
    /// rests on TWO server-side facts, NOT on storage write-permission alone: (1) the SERVER owns the write — the only
    /// write path is the validating RPC, which validates + reconstructs the stored object from a field whitelist; and
    /// (2) the attest RPC RE-VALIDATES the stored object on read, so even a hypothetical first-time raw client write
    /// (Nakama's <c>permissionWrite=0</c> only protects an <i>already-stored</i> object, not a not-yet-created one) fails
    /// attestation → fail-closed. Owner-Read/No-Client-Write is set on every server write so a client can read but not
    /// edit an existing object.</para>
    ///
    /// <para><b>One active online profile per user.</b> The storage object is a single key (<c>heroes</c>/<c>profile</c>)
    /// per authenticated Nakama user — a deliberate EA simplification vs. the offline multi-profile <c>profiles.json</c>.
    /// <see cref="LoadAll"/> returns 0 or 1 profile; <see cref="SaveAsync"/> upserts that one object.</para>
    ///
    /// <para><b>Non-blocking (P2).</b> The <see cref="IProfileSource"/> surface is synchronous, but the Nakama calls are
    /// NOT run on the caller's thread with a blocking wait (that froze the Godot main thread on every picker open/save).
    /// Instead <see cref="LoadAll"/> returns a CACHE populated by the async <see cref="PrefetchAsync"/>, and writes go
    /// through the async <see cref="SaveAsync"/>; the synchronous <see cref="Save"/> throws (a sync network write on the
    /// UI thread is a bug). SDK-coupled and thin (like <see cref="NakamaService"/>/<c>PartyService</c>): untested per repo
    /// convention — the validity rules it relies on are Tier-1 tested in <c>HeroProfileValidator</c> + the TS mirror, and
    /// the reply parsing in <c>AttestationReplyParser</c>.</para>
    /// </summary>
    public sealed class OnlineProfileSource : IProfileSource
    {
        private readonly NakamaService _nakama;

        // Cache of the single server-owned profile, populated by PrefetchAsync so LoadAll never blocks the caller (P2).
        private PlayerProfile? _cached;
        private bool _loaded;

        public OnlineProfileSource(NakamaService nakama)
            => _nakama = nakama ?? throw new ArgumentNullException(nameof(nakama));

        /// <summary>Story 9.12 (P2): asynchronously read the single server-owned profile object into the cache. Call this
        /// off the render path (the online picker awaits it, then marshals the UI populate back to the main thread).
        /// Fail-soft — the underlying read never throws; a missing/unreadable object caches <c>null</c> (empty list).</summary>
        public async Task PrefetchAsync()
        {
            _cached = await _nakama.ReadHeroProfileAsync();
            _loaded = true;
        }

        /// <summary>NON-BLOCKING (P2): return the last <see cref="PrefetchAsync"/>ed value (0 or 1 profile). Never calls
        /// Nakama synchronously, so it cannot freeze the Godot main thread. Returns empty until the first prefetch.</summary>
        public IReadOnlyList<PlayerProfile> LoadAll()
            => _loaded && _cached != null ? new List<PlayerProfile> { _cached } : Array.Empty<PlayerProfile>();

        /// <summary>Story 9.12 (P2): upsert the single server-owned object, routing ONLY through the async validating RPC
        /// (never <c>WriteStorageObjects</c>). Returns the <see cref="StorageWriteResult"/> (<c>Ok=false</c> on a server
        /// rejection or transport failure — never throws); updates the cache on success so a subsequent
        /// <see cref="LoadAll"/> reflects the write without a re-fetch.</summary>
        public async Task<StorageWriteResult> SaveAsync(PlayerProfile profile)
        {
            if (profile == null) return StorageWriteResult.Failed("null_profile");
            StorageWriteResult result = await _nakama.WriteHeroProfileViaRpcAsync(profile);
            if (result.Ok) { _cached = profile; _loaded = true; }
            return result;
        }

        /// <summary>The synchronous <see cref="IProfileSource.Save"/> is UNSUPPORTED online: a blocking network write on
        /// the Godot main thread froze the UI (P2). The online picker uses <see cref="SaveAsync"/> with main-thread
        /// marshaling instead. Throwing here (rather than silently blocking) makes any accidental sync caller loud.</summary>
        public void Save(PlayerProfile profile)
            => throw new NotSupportedException(
                "OnlineProfileSource.Save is async — use SaveAsync (the online picker path). A synchronous Save would " +
                "block the Godot main thread on a Nakama round-trip.");

        /// <summary>The server-owned object is No-Client-Write (there is no delete RPC in this EA slice), so a client
        /// cannot remove it — a no-op. The single object is instead replaced by the next <see cref="SaveAsync"/> (upsert).
        /// The picker disables the Delete affordance online (P5). Host-side deletion is part of the named DW follow-up.</summary>
        public void Delete(string profileId) { /* server-owned, No-Client-Write; no delete RPC in this slice */ }

        /// <summary>Story 9.12 (P11): the online rail is intentionally SINGLE-active-profile-per-user — there is exactly
        /// one server object (key <c>profile</c>) per authenticated user, upserted by <see cref="SaveAsync"/>. So the id
        /// is a stable constant derived from the hero-def id (NOT "next from current store state" like the multi-profile
        /// offline rail), while still matching the offline id shape (<c>{heroDefId}#…</c>) so
        /// <see cref="HeroProfileLoader.MintId"/> stays stable/deterministic.</summary>
        public string NextProfileId(string heroDefId) => (heroDefId ?? "") + "#online";
    }
}

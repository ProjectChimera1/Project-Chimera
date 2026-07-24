#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.12 (FR-7c / AR-12) — the persistence seam over a hero-profile store. The offline
    /// <see cref="LocalProfileSource"/> (a raw <c>profiles.json</c> disk rail) implements it unchanged; the online
    /// <c>OnlineProfileSource</c> (a Nakama adapter) implements it so its only write path is a validating <b>server
    /// RPC</b> — never a raw client storage write. The hero picker depends on this interface, so the same UI drives
    /// either rail (offline disk vs. server-owned object).
    /// </summary>
    public interface IProfileSource
    {
        /// <summary>Load every profile this source holds (offline: all saved heroes; online: the single server-owned
        /// object, so 0 or 1). Fail-soft — an absent/unreadable store yields an empty list, never a throw.</summary>
        IReadOnlyList<PlayerProfile> LoadAll();

        /// <summary>Persist <paramref name="profile"/>. Offline writes the disk file; online routes ONLY through the
        /// validating server RPC and throws if the server rejects the payload (never a raw client storage write).</summary>
        void Save(PlayerProfile profile);

        /// <summary>Delete the profile with <paramref name="profileId"/> (no-op if absent / unsupported by the rail).</summary>
        void Delete(string profileId);

        /// <summary>The next stable profile id for <paramref name="heroDefId"/>, derived deterministically from the
        /// current store state (no wall-clock / RNG / Guid).</summary>
        string NextProfileId(string heroDefId);
    }
}

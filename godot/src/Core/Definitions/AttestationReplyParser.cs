#nullable enable
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>Story 9.12 — the parsed reply from the <c>rpc_write_hero_profile</c> server RPC. <see cref="Ok"/> is
    /// true only when the server validated the payload AND wrote the owner-read/no-client-write object;
    /// <see cref="Version"/> is the stored object version on success, <see cref="Reason"/> the rejection/failure reason
    /// otherwise. Godot-free / SDK-free (a Core.Definitions value type) so it is Tier-1 testable and callable from both
    /// <c>NakamaService</c> and <c>OnlineProfileSource</c>.</summary>
    public readonly record struct StorageWriteResult(bool Ok, string? Reason, string? Version)
    {
        /// <summary>A rejected/failed write (nothing stored).</summary>
        public static StorageWriteResult Failed(string reason) => new(false, reason, null);
    }

    /// <summary>
    /// Story 9.12 (P4/P9) — the pure, Godot-free/SDK-free parser that turns the RAW RPC reply strings emitted by the TS
    /// module (<c>docs/server-deploy/nakama-modules/src/main.ts</c>) into the fail-closed value types the client gates
    /// on. Extracted out of the SDK-coupled <c>NakamaService</c> so the fail-closed guarantee is Tier-1 unit-testable:
    /// a malformed or empty reply MUST resolve to a "cannot enter match" outcome, never fail-open.
    ///
    /// <para>The reply shapes are exactly what the TS handlers emit: attest → <c>{ "attested": bool, "reason": string }</c>;
    /// write → <c>{ "ok": bool, "version"?: string, "reason"?: string }</c>.</para>
    /// </summary>
    public static class AttestationReplyParser
    {
        /// <summary>Parse an <c>rpc_attest_hero_profile</c> reply into an <see cref="AttestationOutcome"/>.
        /// <list type="bullet">
        /// <item><c>{attested:true}</c> ⇒ <see cref="AttestationOutcome.Ok"/>.</item>
        /// <item><c>{attested:false, reason:"range"}</c> (a validation reason) or <c>reason:"not_found"</c> ⇒
        ///   <see cref="AttestationOutcome.Unattested"/> (call SUCCEEDED, just not attested — a legitimate "no attested
        ///   hero" answer).</item>
        /// <item>An EMPTY or UNPARSEABLE completed reply ⇒ <see cref="AttestationOutcome.CallFailed"/>
        ///   (<c>CallSucceeded=false</c>): we cannot trust the reply, so it is fail-closed as a server error, distinct
        ///   from a legitimate <c>not_found</c> (P9).</item>
        /// </list></summary>
        public static AttestationOutcome ParseAttestation(string? json)
        {
            if (string.IsNullOrEmpty(json)) return AttestationOutcome.CallFailed; // empty reply → fail-closed server error
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                bool attested = root.TryGetProperty("attested", out JsonElement a) && a.ValueKind == JsonValueKind.True;
                string? reason = root.TryGetProperty("reason", out JsonElement r) ? r.GetString() : null;
                return attested ? AttestationOutcome.Ok : AttestationOutcome.Unattested(ReasonOf(reason));
            }
            catch
            {
                return AttestationOutcome.CallFailed; // garbled reply → fail-closed server error (never fail-open)
            }
        }

        /// <summary>Parse an <c>rpc_write_hero_profile</c> reply into a <see cref="StorageWriteResult"/>. An empty or
        /// unparseable reply is a failure (nothing was reliably stored).</summary>
        public static StorageWriteResult ParseWriteResult(string? json)
        {
            if (string.IsNullOrEmpty(json)) return StorageWriteResult.Failed("empty_reply");
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                bool ok = root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
                string? reason  = root.TryGetProperty("reason", out JsonElement r) ? r.GetString() : null;
                string? version = root.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
                return new StorageWriteResult(ok, reason, version);
            }
            catch { return StorageWriteResult.Failed("bad_reply"); }
        }

        /// <summary>Map the TS module's reason string onto <see cref="ProfileInvalidReason"/> (for surfacing to the
        /// player). An unknown reason (e.g. <c>"not_found"</c>) maps to <see cref="ProfileInvalidReason.None"/> — the
        /// distinction only shapes the surfaced text; the gate refuses launch regardless.</summary>
        public static ProfileInvalidReason ReasonOf(string? reason) => reason switch
        {
            "identity"   => ProfileInvalidReason.Identity,
            "range"      => ProfileInvalidReason.Range,
            "inventory"  => ProfileInvalidReason.Inventory,
            "attributes" => ProfileInvalidReason.Attributes,
            _            => ProfileInvalidReason.None,
        };
    }
}

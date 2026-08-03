// Story 9.12 — Project Chimera Nakama server runtime module (TypeScript).
//
// Registers the two server RPCs that make the online hero profile server-authoritative (FR-7c / AR-12):
//   • rpc_write_hero_profile  — parse → validate → write the profile as an Owner-Read / No-Client-Write storage object.
//   • rpc_attest_hero_profile — read the stored object → re-validate → return { attested, reason }.
//
// The validity rules live in ./validation.ts, which MIRRORS godot/src/Core/Definitions/HeroProfileValidator.cs.
//
// Tamper model (P8 — precise): the guarantee that a client cannot forge the stored profile rests on TWO things, NOT on
// storage write-permission alone. (1) The server writes the object; the ONLY server write path is this validating RPC,
// which parses → validates → RECONSTRUCTS the stored object from a fixed field whitelist (below) so junk keys / large
// blobs never persist. (2) `handleAttestHeroProfile` RE-VALIDATES the stored object on every read, so even if a client
// managed a first-time raw WriteStorageObjects (Nakama's permissionWrite=0 only protects an ALREADY-stored object, not a
// not-yet-created one), that injected object fails attestation → fail-closed. Owner-Read/No-Client-Write is written on
// every server write so a client can read but not edit an existing object. Collection/key + rpc ids MUST match the C#
// NakamaService constants.
//
// The RPC HANDLER BODIES are extracted into the pure functions `handleWriteHeroProfile` / `handleAttestHeroProfile`
// (below), which take a `nk` interface and are unit-tested with a mocked nk in test/handlers.test.ts (no live Nakama).

import type * as nkruntime from 'nkruntime';
import { validateHeroProfile, type HeroProfile } from './validation.ts';

export const HERO_COLLECTION = 'heroes';
export const HERO_KEY = 'profile';
export const RPC_WRITE = 'rpc_write_hero_profile';
export const RPC_ATTEST = 'rpc_attest_hero_profile';

/** Max serialized size (bytes) of the whitelisted stored object — a generous bound for a hero profile that still blocks
 * a large-blob DoS. A payload whose sanitized form exceeds this is rejected, nothing written (P6). */
export const MAX_STORED_PROFILE_BYTES = 8192;

/** DW-436: cap on the RAW request payload string, checked FIRST — before any JSON.parse / validation work — so a
 * multi-megabyte request can't buy CPU on Nakama's single-threaded goja runtime. Measured with `String.length`
 * (UTF-16 code units — goja-safe; the C# wire is ASCII-escaped System.Text.Json output, so code units == bytes).
 * 4x the stored cap: generous headroom over any legitimate profile (a real wire payload is a few hundred bytes),
 * while the stored object itself stays bounded by MAX_STORED_PROFILE_BYTES after sanitization. */
export const MAX_RAW_PAYLOAD_BYTES = 4 * MAX_STORED_PROFILE_BYTES; // 32768

/** DW-436: cap on the `values`/`inventory` element COUNTS, checked before validateHeroProfile's O(n^2) duplicate
 * scans. Deliberately chosen so it can never flip a verdict the byte caps would have allowed: >512 elements cannot
 * serialize under MAX_STORED_PROFILE_BYTES (each element is ≥ ~19 bytes), so any payload this rejects was already
 * doomed — the guard only moves the rejection BEFORE the quadratic work instead of after it. */
export const MAX_PROFILE_ELEMENTS = 512;

/** DW-436 guard: true when a profile-shaped object's PRESENT `values`/`inventory` arrays exceed
 * MAX_PROFILE_ELEMENTS. Handler-only — a non-array container falls through to validateHeroProfile, which rejects it
 * with the parity-mirrored reason (`attributes`/`inventory`), so the shared C#<->TS fixture behaviour is untouched. */
function exceedsElementCaps(p: unknown): boolean {
  if (!p || typeof p !== 'object') return false;
  const obj = p as { values?: unknown; inventory?: unknown };
  if (Array.isArray(obj.values) && obj.values.length > MAX_PROFILE_ELEMENTS) return true;
  if (Array.isArray(obj.inventory) && obj.inventory.length > MAX_PROFILE_ELEMENTS) return true;
  return false;
}

/**
 * Reconstruct the object to persist from ONLY the known PlayerProfile fields (P6) — junk/extra keys (top-level AND
 * nested in values/inventory entries) are dropped, so a forged payload cannot smuggle arbitrary data into storage.
 * Runs AFTER validateHeroProfile, so `values`/`inventory` are known arrays and every raw/charge/slot a valid Int32.
 */
function sanitizeProfile(p: HeroProfile): { [key: string]: any } {
  const out: { [key: string]: any } = {
    profile_id: p.profile_id,
    hero_def_id: p.hero_def_id,
    values: Array.isArray(p.values) ? p.values.map((v) => ({ key: v.key, raw: v.raw })) : [],
    inventory: Array.isArray(p.inventory)
      ? p.inventory.map((it) => ({ item_id: it.item_id, charges: it.charges, slot: it.slot }))
      : [],
  };
  // Optional metadata (card display only) — copied only when present as a string.
  if (typeof p.faction_id === 'string') out.faction_id = p.faction_id;
  if (typeof p.display_name === 'string') out.display_name = p.display_name;
  if (typeof p.signature_ability === 'string') out.signature_ability = p.signature_ability;
  return out;
}

/**
 * PURE handler for rpc_write_hero_profile — validate the client payload and, ONLY if valid, write the
 * owner-read/no-client-write storage object. An invalid payload writes nothing and returns { ok:false, reason }. Takes
 * the `nk` runtime interface so it is unit-testable with a mock. Returns the JSON reply string.
 */
export function handleWriteHeroProfile(
  nk: nkruntime.Nakama,
  userId: string,
  payload: string,
  logger?: nkruntime.Logger,
): string {
  // DW-436: reject an oversized RAW payload before ANY parse/validate work — the size caps below only ran after
  // validateHeroProfile's O(n^2) duplicate scans, so a single large in-range payload could spike goja CPU.
  if (typeof payload === 'string' && payload.length > MAX_RAW_PAYLOAD_BYTES) {
    logger?.warn('rpc_write_hero_profile rejected oversized raw payload for user %s', userId);
    return JSON.stringify({ ok: false, reason: 'too_large' });
  }

  let profile: HeroProfile;
  try {
    profile = JSON.parse(payload || '{}');
  } catch (e) {
    return JSON.stringify({ ok: false, reason: 'bad_json' });
  }

  // DW-436: bound the element counts BEFORE the validator's quadratic duplicate-key/duplicate-slot scans. Anything
  // this rejects could never have sanitized under MAX_STORED_PROFILE_BYTES anyway (see MAX_PROFILE_ELEMENTS).
  if (exceedsElementCaps(profile)) {
    logger?.warn('rpc_write_hero_profile rejected over-count profile for user %s', userId);
    return JSON.stringify({ ok: false, reason: 'too_large' });
  }

  const result = validateHeroProfile(profile);
  if (!result.valid) {
    logger?.warn('rpc_write_hero_profile rejected profile for user %s: %s', userId, result.reason);
    return JSON.stringify({ ok: false, reason: result.reason });
  }

  // P6: persist ONLY the whitelisted fields (drops junk/extra keys), and enforce a max size so a large blob can't be
  // stored. Nothing is written on rejection.
  const sanitized = sanitizeProfile(profile);
  if (JSON.stringify(sanitized).length > MAX_STORED_PROFILE_BYTES) {
    logger?.warn('rpc_write_hero_profile rejected oversized profile for user %s', userId);
    return JSON.stringify({ ok: false, reason: 'too_large' });
  }

  // Server-owned write: read=1 (owner-read), write=0 (no client write). userId = the authenticated caller.
  const acks = nk.storageWrite([
    {
      collection: HERO_COLLECTION,
      key: HERO_KEY,
      userId: userId,
      value: sanitized,
      permissionRead: 1,
      permissionWrite: 0,
    },
  ]);

  const version = acks && acks.length > 0 ? acks[0].version : undefined;
  return JSON.stringify({ ok: true, version: version });
}

/**
 * PURE handler for rpc_attest_hero_profile — read the caller's stored profile object, re-validate it, and attest.
 * Returns { attested:false, reason:'not_found' } when no object exists; { attested:false, reason } when it fails
 * validation (or its id disagrees with the requested profileId); { attested:true } only for a present, valid, matching
 * profile. Takes the `nk` runtime interface so it is unit-testable with a mock. Returns the JSON reply string.
 */
export function handleAttestHeroProfile(
  nk: nkruntime.Nakama,
  userId: string,
  payload: string,
): string {
  // DW-436: a legitimate attest request is a tiny `{ profileId }` — an oversized one is unambiguous abuse, rejected
  // fail-closed before JSON.parse buys any goja CPU.
  if (typeof payload === 'string' && payload.length > MAX_RAW_PAYLOAD_BYTES) {
    return JSON.stringify({ attested: false, reason: 'too_large' });
  }

  let requestedId = '';
  try {
    const req = JSON.parse(payload || '{}');
    if (req && typeof req.profileId === 'string') requestedId = req.profileId;
  } catch (e) {
    // A malformed request payload is non-fatal — attest whatever is stored.
  }

  const objects = nk.storageRead([{ collection: HERO_COLLECTION, key: HERO_KEY, userId: userId }]);
  if (!objects || objects.length === 0) {
    return JSON.stringify({ attested: false, reason: 'not_found' });
  }

  const stored = objects[0].value as HeroProfile;

  // DW-436: guard the STORED object before the O(n^2) re-validation scans. Every SERVER-written object is under
  // MAX_STORED_PROFILE_BYTES by construction, but a first-time raw client WriteStorageObjects (permissionWrite=0
  // only protects an ALREADY-stored object) could have planted an oversized one — previously re-validated here with
  // no size cap at all. Fail-closed on it instead of burning CPU scanning it (and never attest it).
  if (exceedsElementCaps(stored) || JSON.stringify(stored).length > MAX_STORED_PROFILE_BYTES) {
    return JSON.stringify({ attested: false, reason: 'too_large' });
  }

  const result = validateHeroProfile(stored);
  if (!result.valid) {
    return JSON.stringify({ attested: false, reason: result.reason });
  }

  // Defensive: the requested id (if any) must match the stored object's id.
  if (requestedId.length > 0 && stored.profile_id !== requestedId) {
    return JSON.stringify({ attested: false, reason: 'identity' });
  }

  return JSON.stringify({ attested: true, reason: 'none' });
}

// ── Nakama-registered RPC wrappers (thin adapters over the pure handlers above) ──

function rpcWriteHeroProfile(
  ctx: nkruntime.Context,
  logger: nkruntime.Logger,
  nk: nkruntime.Nakama,
  payload: string,
): string {
  return handleWriteHeroProfile(nk, ctx.userId, payload, logger);
}

function rpcAttestHeroProfile(
  ctx: nkruntime.Context,
  logger: nkruntime.Logger,
  nk: nkruntime.Nakama,
  payload: string,
): string {
  return handleAttestHeroProfile(nk, ctx.userId, payload);
}

// Nakama entry point — registered functions are loaded from the bundled build/index.js mounted into /nakama/data/modules.
function InitModule(
  ctx: nkruntime.Context,
  logger: nkruntime.Logger,
  nk: nkruntime.Nakama,
  initializer: nkruntime.Initializer,
): void {
  initializer.registerRpc(RPC_WRITE, rpcWriteHeroProfile);
  initializer.registerRpc(RPC_ATTEST, rpcAttestHeroProfile);
  logger.info('Project Chimera hero-profile module loaded (rpc: %s, %s).', RPC_WRITE, RPC_ATTEST);
}

// Reference InitModule so the bundler does not tree-shake it away (it is invoked by the Nakama runtime, not by us).
// The `!InitModule` guard is always false at runtime; it exists purely so the symbol is retained in the bundle.
if (!InitModule) { throw new Error('unreachable'); }

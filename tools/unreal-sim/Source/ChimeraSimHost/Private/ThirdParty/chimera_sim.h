/*
 * chimera_sim.h - the C ABI of ChimeraSim.dll (Project Chimera NativeAOT deterministic sim). ABI of record.
 *
 * Unreal trial check (a), task A5 (plan A section 3.4). Version 1.0 (CHIMERA_ABI_VERSION 0x00010000).
 *
 * How a host uses it:
 *   1. LoadLibrary / FPlatformProcess::GetDllHandle by FULL path, once per process, never FreeLibrary (a NativeAOT
 *      library cannot unload). Resolve every export below with GetProcAddress / GetDllExport. Each export's type is
 *      decltype(&chimera_xxx) in C++ (these are declarations only; the host never links the DLL).
 *   2. chimera_abi_version() then chimera_abi_check(sizeof(ChimeraUnit), sizeof(ChimeraBuilding)); stop on non-zero.
 *   3. chimera_session_create, chimera_set_checksum_interval(id, 1), chimera_pre_tick_hashes; then per tick:
 *      chimera_submit_order for every order due, chimera_step, chimera_last_checksum; render from chimera_read_units.
 *   4. chimera_session_destroy.
 *
 * Rules:
 *   - Every export returns int32_t. 0 = OK. Negative = error (below). chimera_abi_version returns the version itself;
 *     chimera_submit_order returns 0 (applied) or 1 (dropped), both successes.
 *   - Ownership: the library allocates nothing a host frees. Strings in are UTF-8, NUL-terminated. Strings out go into
 *     a caller buffer with capacity cap (bytes) and are NUL-terminated: *len = byte count WITHOUT the NUL, and the call
 *     needs cap >= *len + 1, else it writes nothing, sets *len to the required length and returns CHIMERA_E_BUFFER.
 *     Row arrays go into a caller buffer with capacity cap (ROWS); *count = rows needed, same -5 rule.
 *   - A session is a small positive id; an id never reused. A destroyed or unknown id returns CHIMERA_E_SESSION.
 *   - Every export body is try/catch: no managed exception crosses the ABI; the message is kept for chimera_last_error.
 *     An exception inside chimera_submit_order or chimera_step FAULTS the session (the world may be part-stepped):
 *     every later chimera_submit_order / chimera_step on it returns CHIMERA_E_EXCEPTION with the original message.
 *     Reads stay available; destroy the session.
 *   - Threads: no threads or callbacks are created; every export (and every error-slot write) takes one process-wide
 *     lock. Call from one thread (Unreal: the game thread).
 *   - Floating point: the sim is fixed-point; the host should still run with the default x64 MXCSR (0x1F80), see the
 *     host shim in the harness.
 *   - No export writes folded sim state except chimera_submit_order and chimera_step (the command stream and the tick)
 *     and the host-config calls chimera_set_checksum_interval, chimera_session_create and chimera_session_destroy.
 *     tools/sim-trial/check_exports.py asserts DLL exports == this header == the allow-list.
 *
 * Coordinates: Fixed raw values (Q16.16, raw / 65536 = metres), Godot axes: x right, y up, z forward.
 *
 * Factions: one ordinal everywhere (ChimeraUnit.faction, ChimeraBuilding.faction, chimera_submit_order's faction):
 *   0 = Neutral, 1 = Player1 (scenario slot 0, "alpha" in the trial), 2 = Player2 (slot 1, "beta"), ... 8 = Player8.
 *   The sim's Faction enum; FactionRegistry.ToFaction(slot) = slot + 1. CHIMERA_FACTION_COUNT ordinals (0..8).
 */
#ifndef CHIMERA_SIM_H
#define CHIMERA_SIM_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#define CHIMERA_STATIC_ASSERT(cond, msg) static_assert(cond, msg)
#else
#define CHIMERA_STATIC_ASSERT(cond, msg) _Static_assert(cond, msg)
#endif

/* --- constants --------------------------------------------------------------------------------------------------- */
#define CHIMERA_ABI_VERSION      0x00010000
#define CHIMERA_UNIT_SIZE        56
#define CHIMERA_BUILDING_SIZE    28
#define CHIMERA_PRE_TICK_COUNT   6   /* chimera_pre_tick_hashes: start_state, canonical_model, content, ruleset, agreement, tick0 */
#define CHIMERA_STATS_COUNT      8   /* chimera_stats */
#define CHIMERA_FACTION_COUNT    9   /* faction ordinals 0..8: 0 Neutral, 1 Player1, 2 Player2, ... 8 Player8 */

#define CHIMERA_FLAG_AI          0x1u /* chimera_session_create flags: bit0 = AI plan OfflineDefault (else None) */

/* --- status codes ------------------------------------------------------------------------------------------------ */
#define CHIMERA_OK               0
#define CHIMERA_E_ARG           (-1)  /* bad argument (null pointer, out of range, unknown flag bit, session table full) */
#define CHIMERA_E_SESSION       (-2)  /* unknown, destroyed or never-created session id */
#define CHIMERA_E_CONTENT       (-3)  /* content or scenario file could not be read or parsed */
#define CHIMERA_E_SCENARIO      (-4)  /* scenario parsed but the validator rejected it */
#define CHIMERA_E_BUFFER        (-5)  /* caller buffer too small; *len / *count holds the size needed */
#define CHIMERA_E_EXCEPTION     (-6)  /* a managed exception was caught (or the session faulted earlier); see chimera_last_error */
#define CHIMERA_E_ABI           (-7)  /* struct sizes or ABI version differ from the library's */

/* chimera_submit_order results (successes) */
#define CHIMERA_ORDER_APPLIED    0
#define CHIMERA_ORDER_DROPPED    1    /* the sim's own guard ignored it (dead/stale/other-faction/phased unit) */

/* --- row types --------------------------------------------------------------------------------------------------- */

/* One entity slot. chimera_read_units returns EVERY id 0..HighWaterMark-1, dead slots included. */
typedef struct ChimeraUnit {
    int32_t id;          /* entity slot id */
    int32_t ref;         /* packed generation-stamped reference (Generation << 12) | id; what orders take as unit_ref */
    int32_t pos[3];      /* Fixed raw, x y z */
    int32_t prev[3];     /* Fixed raw, the position before the last step (interpolation) */
    int32_t vel[3];      /* Fixed raw per second */
    int32_t hp_raw;      /* Fixed raw health */
    int32_t max_hp_raw;  /* Fixed raw effective max health */
    uint8_t faction;     /* Faction ordinal: 0 = Neutral, 1 = Player1 (slot 0, alpha), 2 = Player2 (slot 1, beta), ... 8 */
    uint8_t mesh_type;   /* the sim's MeshType byte */
    uint8_t flags;       /* EntityFlags: 1 Alive, 2 Moving, 4 Attacking, 8 Phased (inside a building: not drawn) */
    uint8_t command;     /* UnitCommand currently executing */
} ChimeraUnit;

/* One building slot. chimera_read_buildings returns every slot 0..Count-1 (Count is the store's high-water mark).
 * Slots are RECYCLED: a destroyed building's slot can be reused by a new one, and this row carries no generation, so a
 * host that caches per slot must re-check alive/type/faction/def id (chimera_building_def_id) every time it reads. */
typedef struct ChimeraBuilding {
    int32_t slot;
    int32_t pos[3];      /* Fixed raw */
    int32_t hp_raw;
    int32_t max_hp_raw;
    uint8_t faction;     /* Faction ordinal: 0 = Neutral, 1 = Player1 (slot 0, alpha), 2 = Player2 (slot 1, beta), ... 8 */
    uint8_t type;        /* BuildingType ordinal */
    uint8_t alive;       /* 1 alive, 0 dead slot */
    uint8_t pad;         /* always 0 */
} ChimeraBuilding;

CHIMERA_STATIC_ASSERT(sizeof(ChimeraUnit) == CHIMERA_UNIT_SIZE, "ChimeraUnit must be 56 bytes");
CHIMERA_STATIC_ASSERT(sizeof(ChimeraBuilding) == CHIMERA_BUILDING_SIZE, "ChimeraBuilding must be 28 bytes");
CHIMERA_STATIC_ASSERT(offsetof(ChimeraUnit, pos) == 8 && offsetof(ChimeraUnit, hp_raw) == 44 &&
                      offsetof(ChimeraUnit, faction) == 52, "ChimeraUnit layout");
CHIMERA_STATIC_ASSERT(offsetof(ChimeraBuilding, hp_raw) == 16 && offsetof(ChimeraBuilding, faction) == 24,
                      "ChimeraBuilding layout");

/* --- exports ----------------------------------------------------------------------------------------------------- */

/* Library identity. */
int32_t chimera_abi_version(void);                                  /* returns CHIMERA_ABI_VERSION (0x00010000) */
int32_t chimera_abi_check(int32_t unit_size, int32_t building_size); /* 0 if 56/28 match the library, else CHIMERA_E_ABI */
/* "abi=1.0;algo=29;commit=<sha>;dirty=<0|1>;runtime=<FrameworkDescription>;aot=<0|1>" */
int32_t chimera_build_info(char* buf, int32_t cap, int32_t* len);

/* Sessions. content_root = the directory that holds resources/data (what Godot calls res://); scenario = a file path or
 * a res:// path under content_root. seed = the match seed. flags = CHIMERA_FLAG_* (unknown bits -> CHIMERA_E_ARG).
 * Failure: CHIMERA_E_CONTENT / CHIMERA_E_SCENARIO / CHIMERA_E_EXCEPTION, message in chimera_last_error(0, ...). */
int32_t chimera_session_create(const char* content_root, const char* scenario, uint64_t seed, uint32_t flags,
                               int32_t* out_id);
int32_t chimera_session_destroy(int32_t session);

/* Checksum reading. The host sets the interval (0 = never, 1 = every tick) and reads (tick, hash) of the last fold
 * after each chimera_step. The hash is SimChecksum.Compute (uint32), tick the tick it covers (0, 0 before the first).
 * A new session keeps the sim loop's DEFAULT interval of 60 (SimulationLoop.ChecksumInterval): a host that never sets
 * it reads (60, h) for ticks 60-119 and so on. The pair is recorded by the loop itself when it folds, so tick and hash
 * always belong together. */
int32_t chimera_set_checksum_interval(int32_t session, int32_t interval);
int32_t chimera_last_checksum(int32_t session, uint32_t* tick, uint32_t* hash);

/* The five pre-tick agreement hashes and tick0, read at session creation (before any step), in this order:
 * out[0] start_state, [1] canonical_model, [2] content, [3] ruleset, [4] agreement, [5] tick0 (low 32 bits = the sim checksum). */
int32_t chimera_pre_tick_hashes(int32_t session, uint64_t out[CHIMERA_PRE_TICK_COUNT]);

/* The one way in. Applies the order through the sim's OrderApplier for `faction`; returns CHIMERA_ORDER_APPLIED (0) or
 * CHIMERA_ORDER_DROPPED (1) (the sim ignored it; behaviour is identical either way), or a negative error. cmd is the
 * raw wire byte (low 7 bits the command, 0x80 = queued). faction is the issuing faction ordinal 0..8 (see Factions
 * above: alpha = 1, beta = 2); outside 0..8 -> CHIMERA_E_ARG. cmd and slot are 0-255. */
int32_t chimera_submit_order(int32_t session, int32_t faction, int32_t unit_ref, int32_t cmd,
                             int32_t x_raw, int32_t z_raw, int32_t slot);

/* Exactly one simulation tick. */
int32_t chimera_step(int32_t session);

/* Read the world. cap = capacity in rows; *count = HighWaterMark (units) / store count (buildings). Short -> -5.
 * Size query: out = NULL, cap = 0 returns CHIMERA_E_BUFFER with *count set and leaves chimera_last_error untouched. */
int32_t chimera_read_units(int32_t session, ChimeraUnit* out, int32_t cap, int32_t* count);
int32_t chimera_read_buildings(int32_t session, ChimeraBuilding* out, int32_t cap, int32_t* count);

/* Read-only 64-bit FNV-1a digests of the live world (WorldDigest): the unit rows a host holds, and every sim array. */
int32_t chimera_units_digest(int32_t session, uint64_t* out);
int32_t chimera_wide_digest(int32_t session, uint64_t* out);

/* Mesh lookup: the definition id string ("worker", "command_center") of an entity slot id / building slot; "" when the
 * entity has no definition. Out of range id -> CHIMERA_E_ARG. */
int32_t chimera_unit_def_id(int32_t session, int32_t unit_id, char* buf, int32_t cap, int32_t* len);
int32_t chimera_building_def_id(int32_t session, int32_t slot, char* buf, int32_t cap, int32_t* len);

/* Win state: Verdict[Player1] | (Verdict[Player2] << 4); per faction 0 undecided, 1 won, 2 lost. */
int32_t chimera_verdict(int32_t session, int32_t* out);

/* Diagnostics, int64 out[CHIMERA_STATS_COUNT]: [0] GC.GetTotalMemory(false), [1] gen0 collections, [2] gen1, [3] gen2,
 * [4] GC.GetTotalAllocatedBytes(false), [5] current tick, [6] alive units, [7] unit high-water mark.
 * session 0 = process-wide: fields 5-7 are 0. */
int32_t chimera_stats(int32_t session, int64_t out[CHIMERA_STATS_COUNT]);

/* Lowercase hex SHA-256 (64 chars) of any file (scenario, orders, the DLL itself). Null or empty path -> CHIMERA_E_ARG;
 * a missing or unreadable file -> CHIMERA_E_CONTENT (message in chimera_last_error(0, ...)). */
int32_t chimera_file_sha256(const char* path, char* buf, int32_t cap, int32_t* len);

/* Runtime self tests, 0 = handled: 1 managed throw/catch, 2 null dereference caught as NullReferenceException,
 * 3 allocation followed by a full blocking GC. Other kinds -> CHIMERA_E_ARG. */
int32_t chimera_selftest(int32_t kind);

/* Last error message: per session, or session 0 = process-wide (create failures, bad ids). Kept until the next error. */
int32_t chimera_last_error(int32_t session_or_0, char* buf, int32_t cap, int32_t* len);

#ifdef __cplusplus
}
#endif

#endif /* CHIMERA_SIM_H */

// chimera_harness.cpp - the C++ leg of Unreal trial check (a), task A7 (plan A sections 3.4, 3.5, 3.6, 4 A7).
//
// A plain console process that LoadLibrary's ChimeraSim.dll (the NativeAOT sim) by full path, resolves every export of
// include/chimera_sim.h with GetProcAddress (the host never links the DLL), checks the ABI, replays the frozen trial
// order list for N ticks with the checksum interval at 1, and writes a trace in the format compare_traces.py reads
// (plan A section 3.5). It is also the ABI test bench: --cycles (repeat the whole replay in one process), --selftest-errors,
// and the MXCSR shim / FTZ-DAZ experiment. Every hash in the trace comes from the DLL; this file only transports them.
//
// Usage: chimera_harness.exe --dll <ChimeraSim.dll> --content <godot dir> --scenario <json> --orders <csv>
//          [--seed 0xC0FFEE1234567890] [--ai] [--ticks 1440] [--cycles 1] [--digest-ticks 0,300,900,1440]
//          [--out trace.txt] [--selftest-errors] [--mxcsr 0x9FC0] [--no-shim]
// Exit 0 iff every check passed (the replay itself, identical cycles, sha cross-check, selftest when requested).
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <intrin.h>
#include <xmmintrin.h>

#include <algorithm>
#include <cinttypes>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <vector>

#include "../include/chimera_sim.h"

#pragma comment(lib, "bcrypt.lib")

// ---- the DLL's exports (decltype of the header declarations) ------------------------------------------------------
#define CHIMERA_EXPORTS(X)                                                                                              \
    X(chimera_abi_version) X(chimera_abi_check) X(chimera_build_info) X(chimera_session_create)                         \
    X(chimera_session_destroy) X(chimera_set_checksum_interval) X(chimera_last_checksum) X(chimera_pre_tick_hashes)     \
    X(chimera_submit_order) X(chimera_step) X(chimera_read_units) X(chimera_read_buildings) X(chimera_units_digest)     \
    X(chimera_wide_digest) X(chimera_unit_def_id) X(chimera_building_def_id) X(chimera_verdict) X(chimera_stats)        \
    X(chimera_file_sha256) X(chimera_selftest) X(chimera_last_error)

struct Api {
#define X(name) decltype(&name) name = nullptr;
    CHIMERA_EXPORTS(X)
#undef X
};
static Api g_api;
static HMODULE g_dll = nullptr;

// ---- MXCSR shim (plan A section 3.4 "FP environment") ---------------------------------------------------------------
// Every call goes through Shim: it saves MXCSR, sets the default 0x1F80, calls, restores, and counts the calls that found
// a non-default value. Reported, not gated (correctness is gated by trace equality). --no-shim passes calls through.
static bool g_shim = true;
static uint64_t g_mxcsr_calls = 0;
static uint64_t g_mxcsr_nondefault = 0;
static unsigned g_mxcsr_first_seen = 0;
static const unsigned kDefaultMxcsr = 0x1F80;

struct Shim {
    unsigned saved = 0;
    bool active;
    Shim() : active(g_shim) {
        if (!active) return;
        saved = _mm_getcsr();
        ++g_mxcsr_calls;
        if (saved != kDefaultMxcsr) {
            if (g_mxcsr_nondefault++ == 0) g_mxcsr_first_seen = saved;
        }
        _mm_setcsr(kDefaultMxcsr);
    }
    ~Shim() {
        if (active) _mm_setcsr(saved);
    }
};
// All sim calls: Call(g_api.chimera_x, args...)
template <class F, class... A>
static auto Call(F f, A... a) -> decltype(f(a...)) {
    Shim s;
    return f(a...);
}

// ---- small helpers --------------------------------------------------------------------------------------------------
static std::string Fmt(const char* f, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, f);
    vsnprintf(buf, sizeof buf, f, ap);
    va_end(ap);
    return buf;
}
static std::string Hex8(uint32_t v) { return Fmt("%08X", v); }
static std::string Hex16(uint64_t v) { return Fmt("0x%016" PRIX64, v); }

static bool ReadFile(const std::string& path, std::string* out) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return false;
    std::ostringstream ss;
    ss << f.rdbuf();
    *out = ss.str();
    return true;
}
static bool WriteFile(const std::string& path, const std::string& data) {
    std::ofstream f(path, std::ios::binary);
    if (!f) return false;
    f.write(data.data(), (std::streamsize)data.size());
    return (bool)f;
}

static std::string FullPath(const std::string& p) {
    char buf[MAX_PATH * 4];
    DWORD n = GetFullPathNameA(p.c_str(), (DWORD)sizeof buf, buf, nullptr);
    return (n > 0 && n < sizeof buf) ? std::string(buf, n) : p;
}

// SHA-256 through Windows CNG: independent of the DLL's System.Security.Cryptography, so chimera_file_sha256 is cross-checked
// by a second implementation (and again by Python hashlib in the acceptance run).
static bool Sha256File(const std::string& path, std::string* hex) {
    std::string data;
    if (!ReadFile(path, &data)) return false;
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE h = nullptr;
    bool ok = false;
    UCHAR digest[32];
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0 &&
        BCryptCreateHash(alg, &h, nullptr, 0, nullptr, 0, 0) == 0) {
        size_t off = 0;
        ok = true;
        while (off < data.size() && ok) {
            ULONG n = (ULONG)std::min<size_t>(data.size() - off, 1u << 24);
            ok = BCryptHashData(h, (PUCHAR)data.data() + off, n, 0) == 0;
            off += n;
        }
        ok = ok && BCryptFinishHash(h, digest, 32, 0) == 0;
    }
    if (h) BCryptDestroyHash(h);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    if (!ok) return false;
    static const char* d = "0123456789abcdef";
    hex->clear();
    for (int i = 0; i < 32; ++i) {
        hex->push_back(d[digest[i] >> 4]);
        hex->push_back(d[digest[i] & 15]);
    }
    return true;
}

// ---- checks ---------------------------------------------------------------------------------------------------------
static int g_checks = 0, g_failed = 0;
static void Check(bool ok, const char* what, const std::string& detail = "") {
    ++g_checks;
    if (!ok) ++g_failed;
    fprintf(stderr, "[%s] %s%s%s\n", ok ? "ok" : "FAIL", what, detail.empty() ? "" : " : ", detail.c_str());
}

// Text-out helper: calls f(args..., buf, cap, &len) with a growing buffer; returns rc.
template <class F, class... A>
static int32_t TextOut(F f, std::string* out, A... a) {
    std::vector<char> buf(256);
    for (int attempt = 0; attempt < 3; ++attempt) {
        int32_t len = 0;
        int32_t rc = Call(f, a..., buf.data(), (int32_t)buf.size(), &len);
        if (rc == CHIMERA_E_BUFFER && len >= 0) {
            buf.assign((size_t)len + 1, 0);
            continue;
        }
        if (rc == 0) out->assign(buf.data(), (size_t)len);
        return rc;
    }
    return CHIMERA_E_BUFFER;
}
static std::string LastError(int32_t session) {
    std::string s;
    TextOut(g_api.chimera_last_error, &s, session);
    return s;
}

// ---- orders ---------------------------------------------------------------------------------------------------------
struct OrderRow {
    int32_t v[7]; // tick, faction, unit_ref, cmd, x_raw, z_raw, slot
};
static bool ParseOrders(const std::string& text, std::vector<OrderRow>* rows, std::string* err) {
    std::istringstream in(text);
    std::string line;
    int ln = 0;
    while (std::getline(in, line)) {
        ++ln;
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
        if (ln == 1) {
            if (line != "tick,faction,unit_ref,cmd,x_raw,z_raw,slot") {
                *err = "orders header is '" + line + "'";
                return false;
            }
            continue;
        }
        if (line.find_first_not_of(" \t") == std::string::npos) continue;
        OrderRow r{};
        const char* p = line.c_str();
        for (int i = 0; i < 7; ++i) {
            char* end = nullptr;
            long long x = strtoll(p, &end, 10);
            if (end == p || (i < 6 && *end != ',') || (i == 6 && *end != 0)) {
                *err = Fmt("orders line %d: bad field %d", ln, i + 1);
                return false;
            }
            r.v[i] = (int32_t)x;
            p = end + (i < 6 ? 1 : 0);
        }
        if (!rows->empty() && r.v[0] < rows->back().v[0]) {
            *err = Fmt("orders line %d: tick goes backwards", ln);
            return false;
        }
        rows->push_back(r);
    }
    return true;
}
// FNV-1a 64 over every row in submit order, each field as a little-endian int32 (OrderScript.Digest).
static uint64_t OrdersDigest(const std::vector<OrderRow>& rows) {
    uint64_t h = 14695981039346656037ULL;
    for (const OrderRow& r : rows)
        for (int i = 0; i < 7; ++i) {
            uint32_t u = (uint32_t)r.v[i];
            for (int b = 0; b < 4; ++b) h = (h ^ ((u >> (8 * b)) & 0xFF)) * 1099511628211ULL;
        }
    return h;
}

// ---- options --------------------------------------------------------------------------------------------------------
struct Options {
    std::string dll, content, scenario, orders, out;
    uint64_t seed = 0xC0FFEE1234567890ULL;
    bool ai = false, selftest = false, no_shim = false, has_mxcsr = false;
    int ticks = 1440, cycles = 1;
    unsigned mxcsr = kDefaultMxcsr;
    std::vector<int> digest_ticks{0, 300, 900, 1440};
};
static bool ParseU64(const char* s, uint64_t* v) {
    char* end = nullptr;
    int base = (s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) ? 16 : 10;
    *v = strtoull(s, &end, base);
    return end && *end == 0 && end != s;
}
static bool ParseArgs(int argc, char** argv, Options* o) {
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        auto next = [&](const char* name) -> const char* {
            if (i + 1 >= argc) {
                fprintf(stderr, "%s needs a value\n", name);
                return nullptr;
            }
            return argv[++i];
        };
        const char* v = nullptr;
        uint64_t n = 0;
        if (a == "--dll") { if (!(v = next("--dll"))) return false; o->dll = v; }
        else if (a == "--content") { if (!(v = next("--content"))) return false; o->content = v; }
        else if (a == "--scenario") { if (!(v = next("--scenario"))) return false; o->scenario = v; }
        else if (a == "--orders") { if (!(v = next("--orders"))) return false; o->orders = v; }
        else if (a == "--out") { if (!(v = next("--out"))) return false; o->out = v; }
        else if (a == "--seed") { if (!(v = next("--seed")) || !ParseU64(v, &o->seed)) { fprintf(stderr, "bad --seed\n"); return false; } }
        else if (a == "--ticks") { if (!(v = next("--ticks")) || !ParseU64(v, &n) || n < 1) { fprintf(stderr, "bad --ticks\n"); return false; } o->ticks = (int)n; }
        else if (a == "--cycles") { if (!(v = next("--cycles")) || !ParseU64(v, &n) || n < 1) { fprintf(stderr, "bad --cycles\n"); return false; } o->cycles = (int)n; }
        else if (a == "--mxcsr") { if (!(v = next("--mxcsr")) || !ParseU64(v, &n)) { fprintf(stderr, "bad --mxcsr\n"); return false; } o->mxcsr = (unsigned)n; o->has_mxcsr = true; }
        else if (a == "--digest-ticks") {
            if (!(v = next("--digest-ticks"))) return false;
            o->digest_ticks.clear();
            std::string s = v, tok;
            std::istringstream in(s);
            while (std::getline(in, tok, ',')) {
                if (tok.empty()) continue;
                if (!ParseU64(tok.c_str(), &n)) { fprintf(stderr, "bad --digest-ticks\n"); return false; }
                o->digest_ticks.push_back((int)n);
            }
        }
        else if (a == "--ai") o->ai = true;
        else if (a == "--selftest-errors") o->selftest = true;
        else if (a == "--no-shim") o->no_shim = true;
        else { fprintf(stderr, "unknown option %s\n", a.c_str()); return false; }
    }
    if (o->dll.empty() || o->scenario.empty() || o->orders.empty() || o->content.empty()) {
        fprintf(stderr, "--dll, --content, --scenario and --orders are required\n");
        return false;
    }
    return true;
}

// ---- one full replay ------------------------------------------------------------------------------------------------
struct Replay {
    uint64_t pre[CHIMERA_PRE_TICK_COUNT] = {};
    int units_at_start = 0;
    std::vector<uint32_t> hashes;
    std::map<int, std::pair<uint64_t, uint64_t>> digests; // tick -> (units, wide)
    int applied = 0, dropped = 0, order_errors = 0;
    int alive_end = 0, verdict = -1;
    int32_t units_rows = 0;
    bool tick_desync = false, ok = false;
    std::string error;
    int64_t stats[CHIMERA_STATS_COUNT] = {};
    bool operator==(const Replay& o) const {
        return memcmp(pre, o.pre, sizeof pre) == 0 && units_at_start == o.units_at_start && hashes == o.hashes &&
               digests == o.digests && applied == o.applied && dropped == o.dropped && alive_end == o.alive_end &&
               verdict == o.verdict && units_rows == o.units_rows;
    }
};

static Replay RunReplay(const Options& o, const std::vector<OrderRow>& rows) {
    Replay r;
    int32_t s = 0;
    int32_t rc = Call(g_api.chimera_session_create, o.content.c_str(), o.scenario.c_str(), o.seed, o.ai ? CHIMERA_FLAG_AI : 0u, &s);
    if (rc != 0 || s <= 0) { r.error = Fmt("session_create rc=%d: %s", rc, LastError(0).c_str()); return r; }
    if (Call(g_api.chimera_set_checksum_interval, s, 1) != 0) { r.error = "set_checksum_interval failed"; return r; }
    if (Call(g_api.chimera_pre_tick_hashes, s, r.pre) != 0) { r.error = "pre_tick_hashes failed"; return r; }

    auto digest_at = [&](int t) {
        if (std::find(o.digest_ticks.begin(), o.digest_ticks.end(), t) == o.digest_ticks.end()) return;
        uint64_t u = 0, w = 0;
        Call(g_api.chimera_units_digest, s, &u);
        Call(g_api.chimera_wide_digest, s, &w);
        r.digests[t] = {u, w};
    };
    auto read_rows = [&](std::vector<ChimeraUnit>* out) {
        int32_t n = 0;
        int32_t q = Call(g_api.chimera_read_units, s, (ChimeraUnit*)nullptr, 0, &n); // size query
        if (q != CHIMERA_E_BUFFER || n < 0) return false;
        out->assign((size_t)std::max(n, 1), ChimeraUnit{});
        if (Call(g_api.chimera_read_units, s, out->data(), n, &n) != 0) return false;
        out->resize((size_t)n);
        return true;
    };
    {
        std::vector<ChimeraUnit> u;
        if (!read_rows(&u)) { r.error = "read_units at start failed"; return r; }
        for (const ChimeraUnit& x : u) r.units_at_start += (x.flags & 1) ? 1 : 0;
    }
    digest_at(0);

    r.hashes.assign((size_t)o.ticks, 0);
    size_t cursor = 0;
    for (int t = 0; t < o.ticks; ++t) {
        while (cursor < rows.size() && rows[cursor].v[0] == t) {
            const int32_t* v = rows[cursor].v;
            int32_t orc = Call(g_api.chimera_submit_order, s, v[1], v[2], v[3], v[4], v[5], v[6]);
            if (orc == CHIMERA_ORDER_APPLIED) ++r.applied;
            else if (orc == CHIMERA_ORDER_DROPPED) ++r.dropped;
            else ++r.order_errors;
            ++cursor;
        }
        if (Call(g_api.chimera_step, s) != 0) { r.error = Fmt("step failed at tick %d: %s", t + 1, LastError(s).c_str()); return r; }
        uint32_t tick = 0, hash = 0;
        Call(g_api.chimera_last_checksum, s, &tick, &hash);
        if (tick != (uint32_t)(t + 1)) r.tick_desync = true;
        r.hashes[(size_t)t] = hash;
        digest_at(t + 1);
    }
    std::vector<ChimeraUnit> u;
    if (!read_rows(&u)) { r.error = "read_units at end failed"; return r; }
    r.units_rows = (int32_t)u.size();
    for (const ChimeraUnit& x : u) r.alive_end += (x.flags & 1) ? 1 : 0;
    Call(g_api.chimera_verdict, s, &r.verdict);
    Call(g_api.chimera_stats, s, r.stats);
    Call(g_api.chimera_session_destroy, s);
    r.ok = r.order_errors == 0 && !r.tick_desync;
    if (!r.ok) r.error = r.order_errors ? "submit_order returned errors" : "last_checksum tick desync";
    return r;
}

static const char* tempdir() {
    static char b[MAX_PATH];
    DWORD n = GetTempPathA(MAX_PATH, b);
    if (n == 0 || n >= MAX_PATH) return ".";
    while (n > 0 && (b[n - 1] == '\\' || b[n - 1] == '/')) b[--n] = 0;
    return b;
}

// ---- ABI / error self test ------------------------------------------------------------------------------------------
static bool SelfTestErrors(const Options& o) {
    int before = g_failed;
    int32_t id = 0;
    std::string tmp = std::string(tempdir());
    // missing scenario -> -3 + message
    int32_t rc = Call(g_api.chimera_session_create, o.content.c_str(), (tmp + "\\no_such_chimera_scenario.json").c_str(), o.seed, 0u, &id);
    std::string msg = LastError(0);
    Check(rc == CHIMERA_E_CONTENT && id == 0 && !msg.empty(), "missing scenario -> -3 + message", Fmt("rc=%d msg=%.80s", rc, msg.c_str()));
    // unparseable scenario -> -3
    std::string bad = tmp + "\\chimera_harness_bad_scenario.json";
    WriteFile(bad, "{\"definitely\": \"not a scenario\"");
    rc = Call(g_api.chimera_session_create, o.content.c_str(), bad.c_str(), o.seed, 0u, &id);
    msg = LastError(0);
    Check(rc == CHIMERA_E_CONTENT && !msg.empty(), "unparseable scenario -> -3 + message", Fmt("rc=%d msg=%.80s", rc, msg.c_str()));
    // parsed but rejected by the validator -> -4 + message naming the bad definition
    std::string text;
    std::string rejected = tmp + "\\chimera_harness_rejected_scenario.json";
    bool have = ReadFile(o.scenario, &text);
    size_t at = text.find("\"unit_id\": \"");
    if (have && at != std::string::npos) {
        size_t b = at + 12, e = text.find('"', b);
        text.replace(b, e - b, "no_such_unit_definition");
        WriteFile(rejected, text);
        id = 0;
        rc = Call(g_api.chimera_session_create, o.content.c_str(), rejected.c_str(), o.seed, 0u, &id);
        msg = LastError(0);
        Check(rc == CHIMERA_E_SCENARIO && id == 0 && msg.find("no_such_unit_definition") != std::string::npos,
              "validator-rejected scenario -> -4 + message", Fmt("rc=%d msg=%.100s", rc, msg.c_str()));
    } else {
        Check(false, "validator-rejected scenario -> -4 + message", "could not build the rejected scenario from --scenario");
    }
    DeleteFileA(bad.c_str());
    DeleteFileA(rejected.c_str());
    // unknown flag bit -> -1
    Check(Call(g_api.chimera_session_create, o.content.c_str(), o.scenario.c_str(), o.seed, 2u, &id) == CHIMERA_E_ARG, "unknown flag bit -> -1");
    // bad id -> -2 on every session export that takes one
    Check(Call(g_api.chimera_step, 999999) == CHIMERA_E_SESSION, "step on a bad id -> -2");
    msg = LastError(0);
    Check(msg.find("999999") != std::string::npos, "last_error(0) names the bad id", msg.substr(0, 80));
    Check(Call(g_api.chimera_session_destroy, 999999) == CHIMERA_E_SESSION, "destroy on a bad id -> -2");
    uint32_t tk = 0, hs = 0;
    Check(Call(g_api.chimera_last_checksum, 999999, &tk, &hs) == CHIMERA_E_SESSION, "last_checksum on a bad id -> -2");

    // a live session for the buffer rules, then a destroyed id
    int32_t s = 0;
    rc = Call(g_api.chimera_session_create, o.content.c_str(), o.scenario.c_str(), o.seed, o.ai ? CHIMERA_FLAG_AI : 0u, &s);
    Check(rc == 0 && s > 0, "session_create for the buffer checks", Fmt("rc=%d id=%d", rc, s));
    if (rc == 0) {
        int32_t n = 0;
        ChimeraUnit one[1];
        rc = Call(g_api.chimera_read_units, s, one, 1, &n);
        Check(rc == CHIMERA_E_BUFFER && n > 1, "read_units short buffer -> -5 + rows needed", Fmt("rc=%d count=%d", rc, n));
        std::string err_before = LastError(s);
        rc = Call(g_api.chimera_read_units, s, (ChimeraUnit*)nullptr, 0, &n);
        Check(rc == CHIMERA_E_BUFFER && n > 1 && LastError(s) == err_before, "size query (NULL,0) -> -5 + count, last_error untouched", Fmt("rc=%d count=%d", rc, n));
        int32_t len = 0;
        char tiny[4];
        rc = Call(g_api.chimera_build_info, tiny, 4, &len);
        Check(rc == CHIMERA_E_BUFFER && len > 4, "build_info short buffer -> -5 + length", Fmt("rc=%d len=%d", rc, len));
        char tiny8[8];
        rc = Call(g_api.chimera_file_sha256, o.orders.c_str(), tiny8, 8, &len);
        Check(rc == CHIMERA_E_BUFFER && len == 64, "file_sha256 short buffer -> -5 + 64", Fmt("rc=%d len=%d", rc, len));
        char big[80];
        rc = Call(g_api.chimera_file_sha256, "Z:/definitely/not/here.bin", big, 80, &len);
        Check(rc == CHIMERA_E_CONTENT, "file_sha256 missing file -> -3", Fmt("rc=%d", rc));
        rc = Call(g_api.chimera_submit_order, s, 9, 0, 0, 0, 0, 0);
        Check(rc == CHIMERA_E_ARG, "submit_order faction 9 -> -1", Fmt("rc=%d", rc));
        Check(Call(g_api.chimera_session_destroy, s) == 0, "session_destroy");
        Check(Call(g_api.chimera_step, s) == CHIMERA_E_SESSION && Call(g_api.chimera_session_destroy, s) == CHIMERA_E_SESSION,
              "destroyed id -> -2 (step, destroy)");
    }
    // selftests: 1 managed throw/catch, 2 null deref caught, 3 full GC
    for (int k = 1; k <= 3; ++k) Check(Call(g_api.chimera_selftest, k) == 0, Fmt("selftest %d -> 0", k).c_str());
    Check(Call(g_api.chimera_selftest, 9) == CHIMERA_E_ARG, "selftest 9 -> -1");
    return g_failed == before;
}

// ---- main -----------------------------------------------------------------------------------------------------------
int main(int argc, char** argv) {
    Options o;
    if (!ParseArgs(argc, argv, &o)) return 2;
    o.dll = FullPath(o.dll);
    o.content = FullPath(o.content);
    o.scenario = FullPath(o.scenario);
    o.orders = FullPath(o.orders);
    g_shim = !o.no_shim;

    // struct layout the host compiles against (the header also static_asserts these)
    static_assert(sizeof(ChimeraUnit) == 56 && sizeof(ChimeraBuilding) == 28, "ABI sizes");

    if (o.has_mxcsr) _mm_setcsr(o.mxcsr); // thread MXCSR for the whole run; the shim (if on) resets it around each call

    g_dll = LoadLibraryA(o.dll.c_str());
    if (!g_dll) { fprintf(stderr, "LoadLibrary(%s) failed: %lu\n", o.dll.c_str(), GetLastError()); return 3; }
    int missing = 0;
#define X(name)                                                                                     \
    g_api.name = (decltype(&name))GetProcAddress(g_dll, #name);                                     \
    if (!g_api.name) { fprintf(stderr, "missing export %s\n", #name); ++missing; }
    CHIMERA_EXPORTS(X)
#undef X
    Check(missing == 0, "all 21 exports resolved");
    if (missing) return 4;

    int32_t ver = Call(g_api.chimera_abi_version);
    Check(ver == CHIMERA_ABI_VERSION, "abi_version == 0x00010000", Fmt("0x%08X", (unsigned)ver));
    Check(Call(g_api.chimera_abi_check, (int32_t)sizeof(ChimeraUnit), (int32_t)sizeof(ChimeraBuilding)) == 0, "abi_check(56, 28) == 0");
    Check(Call(g_api.chimera_abi_check, 1, 1) == CHIMERA_E_ABI, "abi_check(1, 1) == -7");
    std::string info;
    int32_t brc = TextOut(g_api.chimera_build_info, &info);
    auto field = [&](const char* key) {
        std::string k = std::string(key) + "=";
        size_t p = info.find(k);
        if (p == std::string::npos) return std::string();
        size_t e = info.find(';', p);
        return info.substr(p + k.size(), e == std::string::npos ? std::string::npos : e - p - k.size());
    };
    Check(brc == 0 && field("aot") == "1", "build_info", info);
    fprintf(stderr, "build_info: %s\n", info.c_str());

    // sha256 cross-check: DLL, scenario, orders, each via chimera_file_sha256 and via Windows CNG
    int sha_ok = 0;
    std::string dll_sha, scen_sha, ord_sha;
    {
        const std::pair<const char*, std::string*> files[3] = {{o.dll.c_str(), &dll_sha}, {o.scenario.c_str(), &scen_sha}, {o.orders.c_str(), &ord_sha}};
        for (const auto& f : files) {
            std::string a, b;
            int32_t src = TextOut(g_api.chimera_file_sha256, &a, f.first);
            bool cng = Sha256File(f.first, &b);
            bool same = src == 0 && cng && a == b && a.size() == 64;
            sha_ok += same ? 1 : 0;
            *f.second = a;
            Check(same, "file_sha256 == CNG SHA-256", Fmt("%s %s", f.first, a.c_str()));
        }
    }

    std::string orders_text, perr;
    std::vector<OrderRow> rows;
    if (!ReadFile(o.orders, &orders_text) || !ParseOrders(orders_text, &rows, &perr)) {
        fprintf(stderr, "orders: %s\n", perr.empty() ? "cannot read" : perr.c_str());
        return 5;
    }
    uint64_t orders_digest = OrdersDigest(rows);

    // the replay, --cycles times in this one process (sessions are created and destroyed; the DLL stays loaded)
    std::vector<Replay> runs;
    for (int c = 0; c < o.cycles; ++c) {
        runs.push_back(RunReplay(o, rows));
        const Replay& r = runs.back();
        Check(r.ok, Fmt("cycle %d replay %d ticks", c + 1, o.ticks).c_str(), r.error);
        if (r.ok)
            fprintf(stderr, "cycle %d: tick1=%s tick%d=%s alive_end=%d applied=%d dropped=%d gc_gen0=%lld gc_gen2=%lld heap=%lld\n", c + 1,
                    Hex8(r.hashes.front()).c_str(), o.ticks, Hex8(r.hashes.back()).c_str(), r.alive_end, r.applied, r.dropped,
                    (long long)r.stats[1], (long long)r.stats[3], (long long)r.stats[0]);
    }
    bool identical = !runs.empty();
    for (size_t i = 1; i < runs.size(); ++i) identical = identical && runs[i].ok && runs[0] == runs[i];
    for (const Replay& r : runs) identical = identical && r.ok;

    bool selftest_pass = true;
    if (o.selftest) selftest_pass = SelfTestErrors(o);

    // the trace (first cycle), plan A section 3.5
    const Replay& r = runs.front();
    if (!o.out.empty() && r.ok) {
        std::string t;
        auto H = [&](const char* k, const std::string& v) { t += "# "; t += k; t += ": "; t += v; t += "\n"; };
        H("leg", o.ai ? "cpp_ai" : "cpp");
        H("host", "chimera_harness");
        H("runtime", field("runtime"));
        H("config", "Release");
        H("commit", field("commit"));
        H("dirty", field("dirty"));
        H("algo", field("algo"));
        H("dll_sha256", dll_sha);
        H("scenario_sha256", scen_sha);
        H("orders_sha256", ord_sha);
        H("seed", Hex16(o.seed));
        H("ai", o.ai ? "1" : "0");
        H("hash.start_state", Hex16(r.pre[0]));
        H("hash.canonical_model", Hex16(r.pre[1]));
        H("hash.content", Hex16(r.pre[2]));
        H("hash.ruleset", Hex16(r.pre[3]));
        H("hash.agreement", Hex16(r.pre[4]));
        H("hash.tick0", "0x" + Hex8((uint32_t)r.pre[5]));
        H("units_at_start", Fmt("%d", r.units_at_start));
        H("mxcsr_mode", g_shim ? "shim 0x1F80" : Fmt("none (thread MXCSR 0x%04X)", o.has_mxcsr ? o.mxcsr : kDefaultMxcsr));
        H("mxcsr_nondefault_calls", Fmt("%" PRIu64 "/%" PRIu64, g_mxcsr_nondefault, g_mxcsr_calls));
        for (size_t i = 0; i < r.hashes.size(); ++i) t += Fmt("%d ", (int)i + 1) + Hex8(r.hashes[i]) + "\n";
        for (const auto& kv : r.digests) {
            H(Fmt("digest.%d", kv.first).c_str(), Hex16(kv.second.first));
            H(Fmt("wide.%d", kv.first).c_str(), Hex16(kv.second.second));
        }
        H("orders_digest", Hex16(orders_digest));
        H("orders_applied", Fmt("%d", r.applied));
        H("orders_dropped", Fmt("%d", r.dropped));
        H("alive_end", Fmt("%d", r.alive_end));
        H("verdict", Fmt("%d", r.verdict));
        if (!WriteFile(o.out, t)) { fprintf(stderr, "cannot write %s\n", o.out.c_str()); return 6; }
    }

    printf("units=%d orders=%zu verdict=%d\n", r.units_at_start, rows.size(), r.verdict);
    if (r.ok) {
        for (int t : {1, 60, 300, 1440})
            if (t >= 1 && t <= o.ticks) printf("tick %d %s\n", t, Hex8(r.hashes[(size_t)t - 1]).c_str());
        printf("applied=%d dropped=%d alive_end=%d\n", r.applied, r.dropped, r.alive_end);
        for (const auto& kv : r.digests) printf("digest.%d=%s wide.%d=%s\n", kv.first, Hex16(kv.second.first).c_str(), kv.first, Hex16(kv.second.second).c_str());
    }
    printf("mxcsr mode=%s nondefault_calls=%" PRIu64 "/%" PRIu64 " first_nondefault=0x%04X\n",
           g_shim ? "shim" : "no-shim", g_mxcsr_nondefault, g_mxcsr_calls, g_mxcsr_first_seen);
    printf("cycles=%d identical=%s selftest=%s sha_match=%d/3\n", o.cycles, identical ? "yes" : "no",
           o.selftest ? (selftest_pass ? "pass" : "fail") : "skipped", sha_ok);
    bool all_ok = g_failed == 0 && identical && selftest_pass && sha_ok == 3;
    fprintf(stderr, "chimera_harness: %s %d/%d checks\n", all_ok ? "PASS" : "FAIL", g_checks - g_failed, g_checks);
    return all_ok ? 0 : 1;
}

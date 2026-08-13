# Spec 15-14 — host-side identity enforcement (DW-200): as-built record

**Status:** rail + live-Nakama verifier BUILT + GREEN (2026-08-12); two residuals below.
**Supersedes:** the 2026-08-08 dev-auto HALT (`bmad-dev-auto-result-15-14-...md`). Its three blocking
decisions were resolved by Alec on 2026-08-12: trust mechanism = **live Nakama validation** (a); hosting =
deferred, the rail ships with the verifier as a SEAM; scope = build the rail now, LAN must never require login.

## The architecture (one paragraph)

Identity is enforced at the DEDICATED SERVER's door, per transport slot, under one of two trust modes frozen at
match construction: **LanTrust** (the default — the gate is inert, LAN/offline asks for and stores NO identity,
ever) and **OnlineAttest** (fail-closed — a player slot must attest a VERIFIED Nakama identity before its Ready
counts, and a mid-match rejoin must re-present the SAME account on top of the Story 15-1 RejoinToken). The
credential check is asynchronous and pluggable; a null/absent verifier in OnlineAttest rejects everything.

## The pieces (all Tier-1 tested)

- `godot/src/Multiplayer/Server/IdentityGate.cs` — per-slot attested identity; `MayReady` (the door);
  `CaptureForRejoin` at StartGame freezes WHO holds each slot; `RejoinIdentityOk` (the token-plus-account
  check); `RecordAttestation` (sync verifier seam) + `RecordVerifiedAttestation` (async channel). Reset per
  CONNECT (recycle discipline) — an attestation dies with its connection; the rejoin bind survives.
- `PacketType.Attestation` (0x5A) — type(1) + userIdLen(1) + userId + tokenLen(2 LE) + token; bounds 255/4096,
  fail-closed framing. Same PROTOCOL 6 window as the 15-1 family.
- `godot/src/Multiplayer/Server/NakamaTokenVerifier.cs` — the live-Nakama validator: `BeginValidate` fires
  `GET /v2/account` (Bearer token) on the pool via an INJECTED `fetchAccountUserId` seam; delivery only when
  the Nakama-confirmed account id equals the claimed userId; `DrainVerified` hands results to the main thread
  (the NakamaService.DrainEvents idiom). Positive verdicts cache per token; negatives are retryable; every
  fault fails closed. `ForServer(baseUrl)` is the only networked code.
- `DedicatedServer` wiring — `ConfigureOnlineTrust(verifier)` BEFORE `Start()` (nothing calls it yet: the
  online launch edge wires it once Nakama is hosted); Attestation dispatch (bounded violation log);
  **held-Ready replay**: the honest client sends Attestation then Ready on one ordered channel, so its Ready
  always races the HTTP round-trip — a Ready refused by the identity gate is STASHED (version+hash) and
  replayed through the shared `AcceptReady` path when the confirm drains in `_Process`. The rejoin arm calls
  `RejoinIdentityOk` before handing the request to the RejoinCoordinator.
- Client — `NakamaService.SessionToken`; `LobbyUi.OnReadyPressed` sends `MakeAttestation(UserId, SessionToken)`
  immediately before the Ready packet, ONLINE MODE ONLY. This is separate from (and beside) the Story 9-12
  online HERO-PROFILE attestation, which proves the hero exists in cloud storage; 15-14 proves the PERSON.
- Tests — `IdentityGateTests` (5), `NakamaTokenVerifierTests` (5): codec bounds, LanTrust inertness,
  fail-closed null-verifier, verifier-decides flow, connection-recycle, the rejoin bind (stolen token +
  different account refused), async drain, mismatch silence, outage retryability, cache single-fetch.

## Residuals (why DW-200 stays open)

1. **Attested-hero DEPLOYMENT** — the server distributing every peer's hero profile so all machines spawn the
   identical multi-hero `HeroStore` (StartStateHash bump + golden re-record → an ISOLATED story/session per
   the batch rule). The 9-12 client rail (OnlineProfileSource, HeroPickerOverlay attest gate) is the input.
2. **End-to-end confirm vs a HOSTED Nakama** — needs the `docs/server-deploy` recipe stood up (Docker,
   `npm build` of the gitignored TS module, `docker compose up`) and two clients; then wire the online launch
   edge: `server.ConfigureOnlineTrust(Server.NakamaTokenVerifier.ForServer("http://<nakama>:7350"))`.

## Rules future sessions must keep

- **LAN never asks for identity** — LanTrust stays the default; never gate offline/LAN behind an account.
- A trust mode is FROZEN per match (ConfigureTrust/ConfigureOnlineTrust before Start, never mid-match).
- The verifier seam is where any future mechanism plugs (JWT/shared-secret would be a second `ForX` factory);
  the gate, packet, and server wiring must not change for it.

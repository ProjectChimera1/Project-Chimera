# Telemetry & Analytics Posture (1.0)

_Status: canonical for 1.0. Source: **AR-41**, folded into **Story 1.10a** (see `_bmad-output/implementation-artifacts/.../epics.md:234,492`)._

## Policy: NO analytics or telemetry collection in 1.0

Project Chimera collects **no** analytics or telemetry in the 1.0 release. There is:

- **No** analytics pipeline, SDK, or third-party tracker.
- **No** network beacon, "phone-home", usage ping, or automatic crash reporter.
- **No** background collection of player, machine, or gameplay data.

The only shipped NuGet dependency remains `NakamaClient 3.13.0` (multiplayer matchmaking / auth / lobby) — it
is not an analytics dependency. Story 1.10a's `DependencyHygieneTests` guards that the dependency surface
cannot silently drift to add one.

## What we use instead — dev-only diagnostics (already in the tree)

When a determinism or gameplay issue needs diagnosis, the existing **local, dev-only** tools are sufficient:

- **Structured logs** via the `ILogSink` seam (the dedicated-server / loopback determinism logging from
  Stories 1.8–1.9). Local only; nothing is transmitted off the machine.
- **`.chmr` replay capture** — `ReplayRecorder` / `ReplayPlayer` (`godot/src/Multiplayer/`). A `.chmr` file
  deterministically re-runs a match locally for offline debugging.

These are diagnostics a developer runs on their own machine — not collection.

## Explicit fast-follow (NOT built in 1.0)

The following are **deliberately deferred** to a post-1.0 fast-follow and are **not** part of 1.0 scope:

- An **opt-in** crash / desync report that bundles the `.chmr` replay + the checksum log for a developer to
  inspect — opt-in, never automatic, and still with no analytics pipeline.
- Desync-diagnosis tooling (per-system sub-checksums, replay-diff).

Per AR-41 these are homed structurally as a fast-follow and are **not 1.0-blocking**. Until then, do not add
any telemetry SDK, analytics service, or network reporting. If such a feature is built later it MUST be
opt-in and this document MUST be updated to describe it.

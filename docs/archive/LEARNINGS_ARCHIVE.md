# Project Chimera — Learnings Archive

Entries moved here from LEARNINGS.md when the relevant system phase was complete.
These are still accurate but too narrow/specific to keep in the active reference.

---

## Tech Tree & Prerequisite Systems

- **`TechTreeChecker` static pure-C#**: `AreMet(BuildingStore, Faction, string[]?)` scans for alive + not-under-construction buildings; `FirstMissing()` returns human-readable name for UI (`[need: Barracks]`).
- **`prerequisites: []` JSON default**: `System.Array.Empty<string>()` — `AreMet()` treats null and empty identically; callers never need null-checks.
- **`FactionDefinition.GetBuilding(id)`**: mirror of `GetUnit(id)` — add when prereq lookup needs building defs by string ID.

---

## Editor Undo/Redo (Phase 2 — complete)

- **`(Action redo, Action undo)` pair stack** — `EditorHistory.Push(redo, undo)` records an already-executed command; `Undo()` runs the undo delegate and pushes to redo stack.
- **Free-list entity undo/redo via int[] box**: `int[] box = { id }; _history.Push(redo: () => { int r = Spawn(...); if (r >= 0) box[0] = r; }, undo: () => world.Destroy(box[0]))` — mutable array keeps id in sync across redo calls.
- **Non-free-list stores undo/redo**: slots are stable; undo = `Alive[id]=false`, redo = `Alive[id]=true`. NavObstacleManager auto-detects `Alive[]` changes.
- **Unit snapshot for delete undo**: capture SoA fields into a plain struct before `Destroy(id)`. New id stored in int[] box so redo can destroy it again.
- **Ctrl+Z/Ctrl+Y in `_Input`**: check `key.CtrlPressed`, call `SetInputAsHandled()`. Guard with `GameMode.Edit`.
- **Delete key targeting priority**: buildings (3u) → units (2.5u) → nodes (2u).

---

## Deterministic Lockstep — Input Delay Buffer (Phase 3 — complete)

- **Circular command buffer**: `BUFFER_SIZE=16` (power-of-2), flat `UnitOrder[BUFFER_SIZE * MAX_ORDERS]`; index with `(uint)tick & BUFFER_MASK`.
- **Input delay `Flush(T)` pattern**: send for `T + INPUT_DELAY`; execute for `T` when `_remoteArrived[execMod]` is true.
- **Bootstrap pre-seeding**: in `GoOnline()`, pre-seed ticks 0..INPUT_DELAY-1 as empty so match start has no stall.
- **`TickCommandPacket.Write` flat-array overload**: `Write(buf, tick, faction, orders, baseIdx, count)` — avoids temp array copy.

---

## Spectator Mode (Phase 3 — complete)

- **`Faction.Neutral` from server Hello = spectator trigger**: `OnMatchStart` → `GoSpectate()` + `_fogBridge.RevealAll = true`; skips `StartRecording()`.
- **Spectator `HandlePacket` routing**: read `cmdFaction`; route `Faction.Player1` → `_localBuf`, others → `_remoteBuf`.
- **Separate arrival flags**: `_localArrived[]`/`_localTickFor[]`; spectator `Flush()` waits for BOTH buffers.
- **`FogOfWarBridge.RevealAll` fast-path**: all-255 byte array → `SetData` + `Update` + early return.
- **Spectator slot indices ≥ `MAX_PLAYERS`**: guard all lobby state transitions with `slot < MAX_PLAYERS`.

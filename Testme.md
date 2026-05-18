# Project Chimera — Smoke Tests

## Utility AI (`AiOpponentSystem.cs`)

**Setup:** Open Godot → load the project → set `Ai Level = Normal` on `MainScene` in Inspector.

---

### Test 1 — Basic build order

1. Set `ScenarioPath = alpha_map_01` on MainScene in Inspector.
2. Press **F5** (Play mode).
3. Watch the P2 (red) side.
4. Within ~20s of P2 having 100 ore, a red Barracks should appear near `(36, 0, 6)`.
5. Check the Godot Output panel — no errors.

- [ ] Pass / [ ] Fail — Notes:

---

### Test 2 — Tech progression

Continuing from Test 1 (keep Play mode running):

1. After the Barracks finishes construction (~10s), an ArcheryRange should appear near `(42, 0, 12)`.
2. After the ArcheryRange completes, a SiegeWorkshop should appear near `(42, 0, -12)`.

- [ ] ArcheryRange appeared — Pass / [ ] Fail
- [ ] SiegeWorkshop appeared — Pass / [ ] Fail — Notes:

---

### Test 3 — Supply expansion

Continuing from Test 2:

1. Watch P2's supply (HUD). When headroom drops to ≤4, a second CommandCenter should appear at `(54, 0, 0)`.
2. Confirm P2 spent 150 ore for it (watch the HUD ore counter drop).

- [ ] CC expansion built — Pass / [ ] Fail — Notes:

---

### Test 4 — Attack waves

Continuing (or fresh map):

1. Once P2 has ≥5 idle combat units, a wave should attack-move toward `(-45, 0, 0)` (P1 base).
2. Red units should visibly march toward your base.
3. After the wave, the 25s cooldown resets — a second wave should eventually follow.

- [ ] Wave launched — Pass / [ ] Fail — Notes:

---

### Test 5 — Pre-placed buildings

1. Stop Play. Set `ScenarioPath = map_06_contested_peaks`.
2. Press **F5**.
3. P2 already has a Barracks in this scenario — AI should immediately start training from it with no build delay.
4. Confirm P2 units appear sooner than in Test 1.

- [ ] Pass / [ ] Fail — Notes:

---

### Test 6 — Destroyed Barracks recovery

1. In any map (Play mode), destroy P2's Barracks (let P1 units attack it, or use the editor placer to delete it).
2. AI should build a new Barracks at `(36, 0, 6)` once it has 100 ore.
3. It should NOT loop — one replacement only, no ore haemorrhage.

- [ ] Recovery built — Pass / [ ] Fail — Notes:

---

### Test 7 — Difficulty scaling

**Easy:**
1. Set `Ai Level = Easy` in Inspector, reload any map.
2. AI attacks with ≥8 units, ~40s cooldown between waves — slower and smaller.

- [ ] Pass / [ ] Fail — Notes:

**Hard:**
1. Set `Ai Level = Hard`, reload.
2. AI attacks with ≥3 units, ~15s cooldown — attacks early and often.

- [ ] Pass / [ ] Fail — Notes:

---

## Results Summary

| Test | Result | Notes |
|------|--------|-------|
| 1 — Basic build order | | |
| 2 — Tech progression | | |
| 3 — Supply expansion | | |
| 4 — Attack waves | | |
| 5 — Pre-placed buildings | | |
| 6 — Barracks recovery | | |
| 7 — Difficulty scaling | | |

# Project Chimera — VPS Server Deployment

## What runs on the VPS

| Service | Port | Description |
|---------|------|-------------|
| Nakama  | 7350 | Matchmaking / authentication HTTP API + hero-profile RPCs |
| PostgreSQL | 5432 | Nakama's backing store (internal only) |
| Godot dedicated server | 7777 | ENet game server (headless binary) |

The Nakama server and the Godot game server run on the same machine.  
Nakama groups players; once matched, both clients connect to the Godot server on port 7777.  
Nakama also runs a small **server runtime module** that makes each player's online hero profile
server-authoritative (Story 9.12) — see [Server-validated hero profiles](#server-validated-hero-profiles-story-912) below.

## Recommended VPS spec

- **$10–20/month** tier (e.g. DigitalOcean Basic, Hetzner CX21, Linode Nanode)
- 2 vCPU, 2–4 GB RAM, 40 GB SSD
- Ubuntu 22.04 LTS
- Open firewall ports: **7350** (TCP) and **7777** (UDP)

## 1 — Provision the VPS

```bash
# Install Docker + Compose (Ubuntu)
sudo apt-get update && sudo apt-get install -y docker.io docker-compose-plugin
sudo systemctl enable --now docker
```

## 2 — Start Nakama

```bash
# On the VPS
mkdir ~/chimera-server && cd ~/chimera-server
# Upload docker-compose.yml from docs/server-deploy/

# Set your server key (change from default!)
export NAKAMA_SERVER_KEY="your-secret-key-here"

docker compose up -d
docker compose logs -f nakama   # watch for "startup done"
```

The Nakama console is available at `http://<VPS_IP>:7351` (admin / admin by default).  
**Close port 7351 on the VPS firewall in production.**

## 3 — Export and deploy the Godot headless binary

In the Godot editor on your dev machine:
1. **Project → Export → Add → Linux/X11**
2. Enable "Export Without Textures/Audio" for smaller binary
3. Export as `chimera-server.x86_64`
4. Upload to the VPS

```bash
# On the VPS
chmod +x chimera-server.x86_64

# Start the dedicated server (keeps running in background)
nohup ./chimera-server.x86_64 --headless -- --port 7777 > server.log 2>&1 &

# Or use a systemd service (recommended for auto-restart)
```

### systemd service (optional but recommended)

```ini
# /etc/systemd/system/chimera-server.service
[Unit]
Description=Project Chimera Dedicated Server
After=network.target

[Service]
WorkingDirectory=/home/ubuntu/chimera-server
ExecStart=/home/ubuntu/chimera-server/chimera-server.x86_64 --headless -- --port 7777
Restart=always
RestartSec=5
StandardOutput=append:/var/log/chimera-server.log
StandardError=append:/var/log/chimera-server.log

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now chimera-server
sudo journalctl -u chimera-server -f   # tail logs
```

## 4 — Configure the Godot client

In the Godot Inspector on **MainScene**, set these exports:

| Export | Value |
|--------|-------|
| `NakamaHost` | Your VPS public IP or domain |
| `NakamaPort` | `7350` |
| `NakamaKey` | Must match `NAKAMA_SERVER_KEY` |
| `GameServerIp` | Same VPS public IP |
| `GameServerPort` | `7777` |

## 5 — Test the online flow

1. Start two clients pointing at the VPS.
2. Both open the Multiplayer Lobby (`N` key) → **Online** tab.
3. Enter email + password, click **Find Match** on both.
4. Nakama groups them → both auto-connect to port 7777.
5. Dedicated server sends `Hello(faction)` → both click Ready → match starts.

## Server-validated hero profiles (Story 9.12)

For **online** matches, a player's hero profile must be the *server's* source of truth — not a hand-editable
client save-file. The stock Nakama image ships no runtime code, so Project Chimera adds a small **TypeScript
runtime module** (in [`nakama-modules/`](nakama-modules/)) that Nakama loads on startup.

### What the module enforces

- The online hero profile is stored as a Nakama **storage object** at collection **`heroes`**, key **`profile`**,
  owned by the authenticated user. **One active profile per user** — a single key; a re-save upserts that one object.
- It is written with **`permissionRead = 1` (Owner-Read)** and **`permissionWrite = 0` (No-Client-Write)**. A client
  can read *its own* profile but can **never** write or edit it via a raw `WriteStorageObjects` call — Nakama rejects
  that. The **only** write path is the validating server RPC below.
- Every write and every attestation is validated server-side against the canonical rules in
  `nakama-modules/src/validation.ts`, which mirror the C# `HeroProfileValidator` rule-for-rule (identity → range →
  attributes → inventory, reject fail-closed — never a silent clamp). Both validators are driven off the single shared
  fixture `nakama-modules/test/fixtures/validation-cases.json`, so C# and TS cannot silently drift.

### The two RPCs

| RPC id | Purpose | Returns |
|--------|---------|---------|
| `rpc_write_hero_profile` | Parse the profile JSON payload, validate it, and — only if valid — write the owner-read/no-client-write object. | `{ ok: true, version }` on success; `{ ok: false, reason }` (nothing written) on rejection. |
| `rpc_attest_hero_profile` | Read the caller's stored object, re-validate it, and attest it before a match starts. Payload: `{ "profileId": "..." }`. | `{ attested: true }` for a present, valid, matching profile; otherwise `{ attested: false, reason }` (`not_found` when no object exists). |

The client's online hero picker (surfaced in the lobby before Ready, backed by `OnlineProfileSource`) gates its
launch/Ready on a successful `rpc_attest_hero_profile` result (`OnlineHeroLaunchGate.CanEnterMatch`), **fail-closed** —
a tampered, unattested, or unreachable-server profile can never enter online play.

> **Note (EA slice):** attestation gates the *client* launch via the Nakama RPC result. Byte-level host-enforced
> StartGame identity binding on the ENet `DedicatedServer` (and deterministic in-match deployment of the attested hero)
> is a documented post-1.0 fast-follow — see the `DW-` follow-up in `deferred-work.md`.

### ⚠️ REQUIRED: build the module BEFORE `docker compose up`

**`nakama-modules/build/` is gitignored and does not exist until you build it.** If you skip this step, Nakama comes up
with **no** hero-profile RPCs and *every* player is fail-closed out of online play. To make this a hard gate, the
`nakama` service entrypoint **refuses to start** (`FATAL … module not built`, exit 1) when `build/index.js` is absent —
so a forgotten build fails loudly in the logs instead of silently.

The module bundles to a single JS file with esbuild (no cgo / Go plugin — cross-platform for a solo Windows dev). The
test runner is **vitest** (Node ≥ 18, pinned via `engines`), not raw `.ts` execution. **Run this before the first
`docker compose up`, and again after any change to the module or its validation rules:**

```bash
cd docs/server-deploy/nakama-modules
npm install
npm test          # vitest: the shared-fixture validateHeroProfile parity tests + the RPC-handler tests
npm run build     # produces build/index.js  ← REQUIRED before `docker compose up`
```

`docker-compose.yml` mounts `./nakama-modules/build` into `/nakama/data/modules:ro`, so Nakama loads
`build/index.js` on startup (watch the logs for `Project Chimera hero-profile module loaded`; a missing build shows the
`FATAL … module not built` line instead).

## Security notes

- Change `NAKAMA_SERVER_KEY` from `defaultkey` before going live.
- Close the Nakama console port (7351) in your VPS firewall after setup.
- The dedicated server port (7777 UDP) must be open in the VPS firewall.
- Nakama port (7350 TCP) must be open for client connections.
- PostgreSQL (5432) stays internal — never expose it to the internet.

## Updating Nakama

```bash
# On the VPS — pulls latest image and recreates the container
docker compose pull nakama
docker compose up -d --no-deps nakama
```

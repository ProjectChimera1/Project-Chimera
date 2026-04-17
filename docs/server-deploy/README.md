# Project Chimera — VPS Server Deployment

## What runs on the VPS

| Service | Port | Description |
|---------|------|-------------|
| Nakama  | 7350 | Matchmaking / authentication HTTP API |
| PostgreSQL | 5432 | Nakama's backing store (internal only) |
| Godot dedicated server | 7777 | ENet game server (headless binary) |

The Nakama server and the Godot game server run on the same machine.  
Nakama groups players; once matched, both clients connect to the Godot server on port 7777.

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

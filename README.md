# Scale Reader Service

A .NET 8.0 cross-platform service that reads weight data from industrial scales via IP (SMA 8.1.2 protocol, Mettler Toledo Shared Data, or custom) and posts readings to web applications via SignalR.

## How It Works

1. The service connects to the BasicWeigh web app via SignalR
2. It polls one or more scales via TCP/IP at a configurable interval (default 750ms)
3. Weight readings are parsed using the SMA 8.1.2 standard and broadcast to all web clients in real-time
4. Supports automatic reconnection with exponential backoff on connection loss
5. Scales are configured via the built-in Swagger API and persisted to a local SQLite database

## Features

- **SMA 8.1.2 Protocol** — full parsing of status, range, gross/net, motion, weight, and units
- **Multi-scale support** — poll multiple scales simultaneously from one service instance
- **SignalR** — real-time weight data broadcast to web applications
- **Swagger API** — REST API for configuration, diagnostics, and scale management
- **SQLite persistence** — scale and service settings stored locally
- **Cross-platform** — runs on Windows, Linux, Raspberry Pi
- **Forever retry** — never gives up on SignalR or scale connections
- **Zero command** — send zero commands to scales via SignalR
- **Diagnostic endpoint** — view raw SMA responses for troubleshooting

## Supported Protocols

| Protocol | Description |
|----------|-------------|
| SMA 8.1.2 (Generic) | Standard SMA weight request/response over TCP. Supports Weigh-Tronix ZM-301 and compatible indicators. |
| Mettler Toledo Shared Data | Mettler Toledo Shared Data Services protocol via IP. |
| Custom | Any TCP-based scale protocol with configurable request/response parsing. |

Scale protocol definitions can be loaded from the [device-definitions](https://github.com/GTMichelli-Dev/device-definitions) repo.

## Installation

### Quick Install (Linux / Raspberry Pi)

SSH into the target machine and run one of:

```bash
git clone https://github.com/GTMichelli-Dev/scale-reader-service.git /tmp/srs

# Production: web app reachable as a public URL (port 80 or 443)
bash /tmp/srs/deploy/install.sh https://basicscale.scaledata.net

# LAN-only Pi: web app listening on port 80 on the same Pi
bash /tmp/srs/deploy/install.sh http://localhost

# Local dev: web app launched with `dotnet run` (port 5110)
bash /tmp/srs/deploy/install.sh http://localhost:5110

rm -rf /tmp/srs
```

The web-server URL must match the **actual listen port** of the web app. The
LAN-only Pi deploy binds Kestrel directly to port 80 — so the right URL is
`http://localhost`, not `http://localhost:5110` (that one is the dev default
and only applies when the web app is launched via `dotnet run`). A wrong URL
puts the service into an endless "Connection refused" reconnect loop:

```bash
sudo journalctl -u scale-reader-service -n 20 --no-pager | grep -E 'Connect|refused'
```

With options:
```bash
git clone https://github.com/GTMichelli-Dev/scale-reader-service.git /tmp/srs
bash /tmp/srs/deploy/install.sh https://basicscale.scaledata.net \
    --service-id plant-1 --port 5220
rm -rf /tmp/srs
```

For private repos, git will prompt for credentials. You can also use a deploy key or GitHub token.

Options:
| Option | Default | Description |
|--------|---------|-------------|
| `--service-id <id>` | `default` | Unique ID for this service instance |
| `--port <port>` | `5220` | Swagger API port |
| `--branch <branch>` | `master` | Git branch to install |
| `--install-dir <path>` | `/opt/scale-reader-service` | Install location |

The install script will:
1. Detect system architecture (ARM64, ARM, x64)
2. Install .NET 8 SDK and runtime permanently (skips download on future updates)
3. Clone and build the service from GitHub
4. Configure the web server URL
5. Set up a systemd service that starts on boot
6. Preserve existing database on updates

**Prerequisites:** Just internet access and `git`. No .NET needed — the script installs everything. The .NET SDK is installed permanently so future updates skip the download.

### Updating an Existing Install (Linux / Raspberry Pi)

`install.sh` is idempotent — re-running it on a machine that already has the service installed will:

1. Stop the running service (`systemctl stop scale-reader-service`).
2. Back up `scalereaderservice.db` (your scale configs and runtime settings).
3. Pull the latest `master` from GitHub.
4. Rebuild the binary for the local architecture.
5. Restore the database.
6. Reload + start the systemd unit.

So updating to the newest release is one block of commands on the Pi:

```bash
git clone https://github.com/GTMichelli-Dev/scale-reader-service.git /tmp/srs
bash /tmp/srs/deploy/install.sh http://localhost
rm -rf /tmp/srs
```

(Pass whatever web URL you originally used. `http://localhost` works when the BasicWeigh web app is on the same Pi listening on port 80; `https://yourdomain` for a cloud-hosted web app.)

**Watch the upgrade live**, optional but recommended:

```bash
# In one terminal — leave this running while you run install.sh in another
sudo journalctl -u scale-reader-service -f --no-pager
```

You should see the new version banner come through:

```
============================================
  Scale Reader Service v1.2.6
  Swagger: http://0.0.0.0:5220/swagger
============================================
```

**Confirm the new version is what's actually running:**

```bash
sudo journalctl -u scale-reader-service -n 100 --no-pager | grep "Scale Reader Service v" | tail -1
```

That line should match the [`<Version>` in `ScaleReaderService.csproj`](ScaleReaderService.csproj). If it shows an older version, the rebuild step was skipped (rare — usually a stale cache); force a clean and re-run:

```bash
sudo systemctl stop scale-reader-service
sudo rm -rf /opt/scale-reader-service/bin /opt/scale-reader-service/obj 2>/dev/null
bash /tmp/srs/deploy/install.sh http://localhost
```

**Confirm the service connected to the web hub** (so the Scale Management page can see it):

```bash
sudo journalctl -u scale-reader-service -n 30 --no-pager | grep -E 'Connect|refused' | tail -5
```

You want to see `Connected to http://.../scaleHub` with no follow-up `Connection refused`. If you see `Connection refused`, the `ServerUrl` in the service's settings table doesn't match the web app's actual listen port — see the next section for fixing that without re-running `install.sh`.

> **The database is preserved across reinstalls** — scale configs, retained tares, `BrandsUrl`, `ServerUrl`, and `ServiceId` all survive. Only the binary is replaced. To start from a clean DB, stop the service and delete `/opt/scale-reader-service/scalereaderservice.db` before running `install.sh`.

### Run as console app (Windows or Linux)

```bash
cd ScaleReaderService
dotnet run
```

### Install as Windows Service

```bash
dotnet publish -c Release -o C:\Services\ScaleReaderService
sc create "ScaleReaderService" binPath="C:\Services\ScaleReaderService\ScaleReaderService.exe"
sc start ScaleReaderService
```

## Configuration

All configuration is done via the Swagger API at `http://<your-ip>:<port>/swagger`.

### Service Settings (GET/PUT /api/settings)

| Setting | Description |
|---------|-------------|
| `serviceId` | Unique ID for this service instance (used by the web app to identify it) |
| `serverUrl` | BasicWeigh web server URL (e.g., `https://basicscale.scaledata.net`) |
| `signalRHub` | SignalR hub path (default: `/scaleHub`) |
| `brandsUrl` | URL to remote scale-models.json for protocol definitions |
| `brandsToken` | GitHub token for private repos (optional) |

### Scale Configuration (CRUD /api/scales)

| Field | Description |
|-------|-------------|
| `scaleId` | Unique ID (e.g., `scale-1`) |
| `displayName` | Human-readable name |
| `protocol` | `SMA`, `MettlerToledo`, or `Custom` |
| `ipAddress` | Scale IP address |
| `port` | Scale TCP port (default: 10001) |
| `requestCommand` | Command sent to request weight (default: `W\r\n`) |
| `pollingIntervalMs` | Poll frequency in milliseconds (default: 750) |
| `timeoutMs` | Socket timeout (default: 1000) |

### Diagnostic Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /api/status/health` | Service health check with scale count |
| `GET /api/status/weight` | Current weight readings from all scales |
| `GET /api/status/weight/{scaleId}` | Weight reading from a specific scale |
| `GET /api/status/diagnostic/{scaleId}` | Raw SMA response for troubleshooting |

## Service Management (Linux)

```bash
# Check status
sudo systemctl status scale-reader-service

# Restart
sudo systemctl restart scale-reader-service

# View logs
sudo journalctl -u scale-reader-service -f

# Stop
sudo systemctl stop scale-reader-service
```

## Service Management (Windows)

```bash
# Start/stop
sc start ScaleReaderService
sc stop ScaleReaderService

# View in Services app
services.msc
```

## Requirements

- .NET 8.0 Runtime (installed automatically on Linux via deploy script)
- Network access to the scale(s) and the BasicWeigh web application
- TCP connectivity to scale indicators (typically port 10001)

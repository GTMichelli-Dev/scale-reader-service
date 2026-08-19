# Scale Reader Service

A .NET 10.0 cross-platform service that reads weight data from industrial scales via IP (SMA 8.1.2 protocol, Mettler Toledo Shared Data, or custom) **or RS-232 serial** (continuous-stream or on-demand indicators) and posts readings to web applications via SignalR.

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
- **Auto-detect** — listen to a continuous-output indicator and work out its frame layout
- **Stream tokens** — point the parser at the weight and motion columns by hand when no brand definition fits

## Supported Protocols

| Protocol | Description |
|----------|-------------|
| SMA 8.1.2 (Generic) | Standard SMA weight request/response over TCP. Supports Weigh-Tronix ZM-301 and compatible indicators. |
| Mettler Toledo Shared Data | Mettler Toledo Shared Data Services protocol via IP. |
| Custom | Any TCP-based scale protocol with configurable request/response parsing. |
| Serial continuous stream | RS-232 indicators that stream weight frames constantly. Built-in parsers for the Rice Lake IQ plus 355 / 920i EDP-PRN format (also used by **Condec UMC**) and the Cardinal 225 Navigator token format; other streams can be onboarded with a `weightRegex` in the brand definition — no code change. |
| Serial on-demand | RS-232 indicators that answer a request command (e.g. Cardinal `Gross\r`). |

Scale protocol definitions can be loaded from the [device-definitions](https://github.com/GTMichelli-Dev/device-definitions) repo.

## Serial (RS-232) scales — e.g. Condec UMC

Typical hookup for a Condec UMC indicator streaming continuously at 9600,8,N,1:

1. **Hardware:** indicator's RS-232 port → USB-to-serial adapter → Pi. The
   adapter shows up as `/dev/ttyUSB0` (check with `ls /dev/ttyUSB*`).
2. **Indicator:** set `STREAM=EDP` (or `PRN`) in the SERIAL menu so it emits
   the continuous IQ plus 355-style frames (`  12000LG `, `-  11200LGM`).
3. **Configure the scale** via Swagger (`http://<pi>:5220/swagger`) or the
   web app's scale setup: `connectionType: "Serial"`,
   `serialPortName: "/dev/ttyUSB0"`, `baudRate: 9600`, `dataBits: 8`,
   `parity: "None"`, `stopBits: 1`, `scaleBrand: "Condec — UMC / Continuous"`,
   `protocol: "Continuous"`.
4. **Permissions:** the service user must be in the `dialout` group —
   `deploy/install.sh` handles this (plus `SupplementaryGroups=dialout` in
   the systemd unit). If you installed manually: `sudo usermod -aG dialout $USER`
   and restart the service.

**Troubleshooting:** raw frames are logged (rate-limited) —
`sudo journalctl -u scale-reader-service -f` shows `frame raw='...' hex=...`
for every parsed reading, plus loud warnings when the port is silent (wrong
port/baud) or frames don't parse (wrong protocol/brand).

## Auto-detect and stream tokens

Commissioning an indicator nobody has written a `weightRegex` for used to mean
guessing. **Auto-Detect**, on the web app's scale setup screen, opens a
temporary connection with the settings being typed in, listens for a few
seconds, and reports what the indicator actually streams: the weight
start/end columns, the motion column and character, and a separate sign
column for indicators that keep the sign in its own field. It proposes a
configuration — you review the captured frames and save.

Where no brand definition fits, set the columns by hand in the **Stream
Tokens** panel. They are stored per scale (`frameParseMode: "Positions"` plus
`frameWeightStart` / `frameWeightEnd` / `frameMotionIndex` / `frameMotionChar`
/ `frameSignIndex` / `frameSignNegChar`) and take priority over the brand
regex on every read path — serial and TCP, streaming and demand. They were
configured against frames that exact indicator sent, which is better evidence
than a shared pattern. Clearing them reverts the scale to brand parsing.

Detection is pure — no ports, no sockets, no database — so you can exercise it
against frames captured any other way, with nothing plugged in:

```bash
curl -X POST http://localhost:5220/api/detect -H "Content-Type: application/json" \
  -d '{"frames":["   8980 LB G    ","   8860 LB G MO "]}'
```

It reports *every* brand whose regex matches, not the first: several
definitions in the shared repo are loose enough to match any "number + lb"
frame, so a single match is not evidence of the right model. Confidence
therefore rests on the columns alone.

When a capture comes back empty the reason distinguishes the causes, because
each has a different fix:

| Reported | Usually means |
|---|---|
| No data arrived at all | Wrong port, baud, data bits or parity — or the indicator is not streaming. If it only replies when polled, put its command in **Request Command** and detect again. |
| Bytes arrived but no complete frame | Baud/parity mismatch. A hex sample of what arrived is included. |
| Port is busy | An active scale's reader already holds it — including the scale you are editing. Set **Active** to No, save, then detect. |

Capture reads raw bytes and splits on CR **or** LF rather than assuming the
terminator, since that is one of the things being discovered.

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
2. Install the .NET 10 SDK and runtime permanently (skips download on future updates)
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
  Scale Reader Service v1.3.0
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
dotnet publish -c Release -r win-x64 --self-contained true -o C:\Services\ScaleReaderService
sc create "ScaleReaderService" binPath="C:\Services\ScaleReaderService\ScaleReaderService.exe" start= auto
sc start ScaleReaderService
```

`--self-contained` bundles the .NET runtime into the output, so the target PC
needs no .NET install at all. Worth doing on a customer machine even when the
right runtime happens to be present — it removes a dependency you would
otherwise have to check on every future update.

### Updating an Existing Install (Windows)

There is no `install.sh` equivalent on Windows, and a production PC usually has
neither git nor the .NET SDK. So build the package on a machine that has the
source, and carry the folder over.

**On the build machine:**

```bash
dotnet publish -c Release -r win-x64 --self-contained true -o C:\Temp\scale-reader-update\app
```

Copy that folder to the target PC (USB, share, RDP drive), then from an
**admin** command prompt there:

```bash
sc stop ScaleReaderService
copy "C:\Services\ScaleReaderService\scalereaderservice.db" "%USERPROFILE%\Desktop\scalereaderservice.db.bak"
robocopy "C:\Temp\scale-reader-update\app" "C:\Services\ScaleReaderService" /E /XF scalereaderservice.db scalereaderservice.db-wal scalereaderservice.db-shm
sc start ScaleReaderService
```

Confirm the install path first if you are not sure of it — `sc qc ScaleReaderService`.

Three things bite on Windows, all avoidable:

- **The service locks its own `.exe`.** Copying over a running service fails with
  a file-lock error. Stop it first, and give Windows a few seconds to release the
  handle before copying.
- **The database lives in the application folder** — `AppContext.BaseDirectory`,
  i.e. `C:\Services\ScaleReaderService\scalereaderservice.db`. It holds the scale
  configuration, serial port, `ServerUrl`, `ServiceId` and retained tares, and is
  *not* part of the publish output. Publishing over the existing folder leaves it
  alone; copying the app folder to a **new** location and switching to that will
  lose it unless you bring the database across. Exclude its `-wal` and `-shm`
  companions from the copy too — dropping a stale write-ahead log next to a
  different database risks corrupting it.
- **Schema changes apply themselves on start.** New columns are added by the
  `AddColumnIfMissing` calls in `Program.cs` (this project uses `EnsureCreated`
  plus hand-written column adds, not EF migrations), so an older database
  upgrades in place with its rows intact. No manual migration step.

Verify afterwards:

```bash
sc query ScaleReaderService
curl http://localhost:5220/api/status/health
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
| `protocol` | `SMA`, `MettlerToledo`, `Custom`, or `Continuous` (hold the connection open and read streamed frames — serial and TCP) |
| `ipAddress` | Scale IP address |
| `port` | Scale TCP port (default: 10001) |
| `requestCommand` | Command sent to request weight (default: `W\r\n`) |
| `pollingIntervalMs` | Poll frequency in milliseconds (default: 750) |
| `timeoutMs` | Socket timeout (default: 1000) |
| `connectionType` | `TCP` or `Serial` |
| `serialPortName` | e.g. `COM4`, `/dev/ttyUSB0` — required when `connectionType` is `Serial` |
| `baudRate` / `dataBits` / `parity` / `stopBits` | Serial line settings (default 9600, 8, `None`, 1) |
| `frameParseMode` | `Brand` (use the brand regex / built-in parsers) or `Positions` |
| `frameWeightStart` / `frameWeightEnd` | 0-based inclusive column range holding the weight |
| `frameMotionIndex` / `frameMotionChar` | Column carrying motion, and the character meaning "in motion" |
| `frameSignIndex` / `frameSignNegChar` | Column holding the sign, for indicators that keep it separate |

### Diagnostic Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /api/status/health` | Service health check with scale count |
| `GET /api/weight/{scaleId}` | Weight reading from a specific scale (404 until it has been polled) |
| `GET /api/diagnostic` | Last raw response for every active scale — raw text, hex, parsed values, timing |
| `GET /api/diagnostic/{scaleId}` | The same for one scale |
| `GET /api/serialports` | Serial ports this machine offers (for the setup screen's port picker) |
| `POST /api/detect` | Run format detection against frames you already captured — no hardware needed |
| `GET /api/status/brands` | Current brand definitions, refreshed from the remote device-definitions repo |

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

- .NET 10.0 Runtime — installed automatically on Linux by `deploy/install.sh`, and
  not needed at all for a `--self-contained` Windows publish, which bundles it
- Network access to the scale(s) and the BasicWeigh web application
- TCP connectivity to scale indicators (typically port 10001), or a serial port
  for RS-232 indicators

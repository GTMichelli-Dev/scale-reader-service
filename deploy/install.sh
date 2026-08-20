#!/bin/bash
# =============================================================================
# Scale Reader Service - Self-Install Script for Raspberry Pi / Linux
# =============================================================================
# Run directly on the target machine:
#
#   git clone https://github.com/GTMichelli-Dev/scale-reader-service.git /tmp/srs
#   bash /tmp/srs/deploy/install.sh <web-server-url>
#   rm -rf /tmp/srs
#
# Examples:
#   git clone https://github.com/GTMichelli-Dev/scale-reader-service.git /tmp/srs
#
#   # Production: web app reachable as a real hostname / public URL (port 80 or 443)
#   bash /tmp/srs/deploy/install.sh https://basicscale.scaledata.net
#
#   # LAN Pi: web app on the same Pi listening on port 80
#   bash /tmp/srs/deploy/install.sh http://localhost
#
#   # Local dev: web app launched via `dotnet run` on port 5110
#   bash /tmp/srs/deploy/install.sh http://localhost:5110
#
#   bash /tmp/srs/deploy/install.sh http://localhost --service-id plant-1 --port 5220
#   rm -rf /tmp/srs
#
# Pick the URL to match the web app's *actual* listen port. The LAN-only Pi
# deploy binds Kestrel to :80 (see web README), so the right URL is
# http://localhost — not http://localhost:5110 (which is the dev default).
# A wrong URL puts the service into an endless "Connection refused" reconnect
# loop, visible via `journalctl -u scale-reader-service -f`.
#
# To update an existing install, run the same command again — it will
# stop the service, update files, preserve the database, and restart.
# =============================================================================

set -e

# ---- Defaults ----
SERVICE_ID=""      # prompted for below; defaults to $(hostname)
SERVICE_PORT="5220"
INSTALL_DIR="/opt/scale-reader-service"
SERVICE_NAME="scale-reader-service"
DOTNET_CHANNEL="10.0"
GITHUB_REPO="GTMichelli-Dev/scale-reader-service"
BRANCH="master"
WEB_URL=""

# ---- Parse arguments ----
while [[ $# -gt 0 ]]; do
    case "$1" in
        --service-id) SERVICE_ID="$2"; shift 2 ;;
        --port)       SERVICE_PORT="$2"; shift 2 ;;
        --branch)     BRANCH="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        --help|-h)
            echo "Usage: install.sh <web-server-url> [options]"
            echo ""
            echo "  <web-server-url>       Required. URL of the BasicWeigh web server."
            echo "                         Must match the web app's actual listen port."
            echo "                         Examples:"
            echo "                           https://basicscale.scaledata.net   (production)"
            echo "                           http://localhost                   (LAN Pi, web on :80)"
            echo "                           http://localhost:5110              (dev, dotnet run)"
            echo ""
            echo "Options:"
            echo "  --service-id <id>      Unique ID for this service (default: default)"
            echo "  --port <port>          API port (default: 5220)"
            echo "  --branch <branch>      Git branch to install (default: master)"
            echo "  --install-dir <path>   Install location (default: /opt/scale-reader-service)"
            echo "  --help                 Show this help"
            exit 0
            ;;
        -*)
            echo "Unknown option: $1 (use --help for usage)"
            exit 1
            ;;
        *)
            if [ -z "$WEB_URL" ]; then
                WEB_URL="$1"
            else
                echo "Unknown argument: $1"
                exit 1
            fi
            shift
            ;;
    esac
done

if [ -z "$WEB_URL" ]; then
    echo "ERROR: Web server URL is required."
    echo ""
    # Show the invocation that matches how this copy was obtained. Telling
    # someone running the release tarball to git clone sends them down the
    # build-from-source path they downloaded a prebuilt package to avoid.
    if [ -f "$(dirname "${BASH_SOURCE[0]}")/app/ScaleReaderService" ]; then
        echo "Usage:"
        echo "  bash $(dirname "${BASH_SOURCE[0]}")/install.sh <web-server-url>"
        echo ""
        echo "Examples:"
        echo "  bash $(dirname "${BASH_SOURCE[0]}")/install.sh https://basicscale.scaledata.net   # production"
        echo "  bash $(dirname "${BASH_SOURCE[0]}")/install.sh http://localhost                   # LAN Pi, web on :80"
    else
        echo "Usage:"
        echo "  git clone https://github.com/${GITHUB_REPO}.git /tmp/srs"
        echo "  bash /tmp/srs/deploy/install.sh <web-server-url>"
        echo ""
        echo "Examples:"
        echo "  bash /tmp/srs/deploy/install.sh https://basicscale.scaledata.net   # production"
        echo "  bash /tmp/srs/deploy/install.sh http://localhost                   # LAN Pi, web on :80"
    fi
    echo ""
    echo "Run with --help for all options."
    exit 1
fi

echo ""
echo "============================================"
echo "  Scale Reader Service - Install"
echo "============================================"
echo "  Web Server:   ${WEB_URL}"
echo "  Service ID:   ${SERVICE_ID}"
echo "  Port:         ${SERVICE_PORT}"
echo "  Install Dir:  ${INSTALL_DIR}"
echo "  Branch:       ${BRANCH}"
echo "============================================"
echo ""

# ---- Detect architecture ----
echo "[1/5] Detecting system..."
# ---- Service ID ----
# Default to the machine's hostname so every install lands on the web app's
# Scale Management page under a distinct, recognisable name - "default" on
# three boxes is indistinguishable. Mirrors the CameraService installers.
if [ -z "$SERVICE_ID" ]; then
    DEFAULT_SERVICE_ID="$(hostname)"
    if [ -t 0 ]; then
        echo ""
        echo "Enter a Service ID for this Scale Reader."
        echo "  Shown on the web app's Scale Management page so each box is identifiable."
        echo "  Press Enter to use this machine's name: ${DEFAULT_SERVICE_ID}"
        read -rp "ServiceId: " INPUT_SERVICE_ID
        if [ -z "$INPUT_SERVICE_ID" ]; then
            SERVICE_ID="$DEFAULT_SERVICE_ID"
            echo "  Using: ${SERVICE_ID}"
        else
            SERVICE_ID="$INPUT_SERVICE_ID"
        fi
        echo ""
    else
        # Unattended rollouts still land uniquely without passing --service-id.
        SERVICE_ID="$DEFAULT_SERVICE_ID"
    fi
fi

# A mistyped URL is the classic failure: the service installs cleanly and then
# reconnects forever against nothing. Say so now, while someone is watching.
#
# Probe the SignalR negotiate endpoint the service will actually use, with a
# POST and without following redirects. Both matter. Fetching the site root
# instead reports a happy "reachable" for a URL the service cannot use:
# negotiate is a POST, an http->https redirect downgrades it to GET, and the
# hub answers 405 Method Not Allowed forever.
HUB_PROBE=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' -X POST --max-time 10 \
    "${WEB_URL}/scaleHub/negotiate?negotiateVersion=1" 2>/dev/null || echo "000 ")
HUB_CODE=${HUB_PROBE%% *}
HUB_LOC=${HUB_PROBE#* }

case "$HUB_CODE" in
    200)
        echo "  ${WEB_URL} hub check: ok (negotiate answered 200)."
        ;;
    30[0-9])
        echo ""
        echo "  ERROR: ${WEB_URL} redirects to ${HUB_LOC}"
        echo ""
        echo "  SignalR negotiates with a POST, and a redirect turns that into a GET,"
        echo "  which the hub rejects with 405. The service would install cleanly and"
        echo "  then reconnect forever."
        echo ""
        SUGGEST=$(echo "$HUB_LOC" | sed -E 's#^(https?://[^/]+).*#\1#')
        [ -z "$SUGGEST" ] && SUGGEST=$(echo "$WEB_URL" | sed 's#^http://#https://#')
        echo "  Re-run with the URL the site actually serves:"
        echo "    bash install.sh ${SUGGEST}"
        echo ""
        if [ "${SKIP_URL_CHECK:-0}" = "1" ]; then
            echo "  SKIP_URL_CHECK=1 is set - installing anyway."
            echo ""
        else
            echo "  Set SKIP_URL_CHECK=1 to install anyway."
            echo ""
            exit 1
        fi
        ;;
    401|403)
        echo "  ${WEB_URL} hub check: reachable, negotiate returned ${HUB_CODE}."
        echo "           The hub is there; it refused this unauthenticated probe."
        ;;
    404)
        echo "  WARNING: no /scaleHub at ${WEB_URL} (HTTP 404)."
        echo "           Right server, wrong app? The service will retry forever."
        ;;
    000)
        echo "  WARNING: ${WEB_URL} did not answer within 10s."
        echo "           The service will install and retry forever. If that URL is"
        echo "           wrong, Ctrl+C now and re-run with the right one."
        ;;
    *)
        echo "  WARNING: negotiate at ${WEB_URL} returned HTTP ${HUB_CODE}."
        ;;
esac

ARCH=$(uname -m)
case "$ARCH" in
    aarch64) RID="linux-arm64" ;;
    armv7l)  RID="linux-arm" ;;
    x86_64)  RID="linux-x64" ;;
    *)       echo "WARNING: Unknown arch '${ARCH}', trying linux-x64"; RID="linux-x64" ;;
esac
echo "  OS: $(uname -s) $(uname -r)"
echo "  Architecture: ${ARCH} (${RID})"

# ---- Serial port access ----
# RS-232 scales (e.g. Condec UMC via a USB-serial adapter) show up as
# /dev/ttyUSB0 / /dev/ttyAMA0, owned by group 'dialout'. The systemd unit
# below runs as this user, so without dialout membership every serial open
# fails with "Access to the port ... is denied".
if ! id -nG "$USER" 2>/dev/null | tr ' ' '\n' | grep -qx 'dialout'; then
    sudo usermod -aG dialout "$USER"
    echo "  Added $USER to dialout group (serial port access)."
    echo "  NOTE: takes effect for the systemd service on next start (handled below);"
    echo "        interactive shells need a logout/login."
else
    echo "  $USER already in dialout group (serial port access)."
fi

# ---- Firewall ----
# Pi OS ships with no firewall, but a site-hardened image may have ufw or
# iptables rules that would block the Swagger/config API.
if command -v ufw &> /dev/null && sudo ufw status | grep -q "active"; then
    sudo ufw allow 22/tcp > /dev/null
    sudo ufw allow "${SERVICE_PORT}"/tcp > /dev/null
    echo "  Firewall: ufw — ports 22 and ${SERVICE_PORT} opened."
fi
if command -v iptables &> /dev/null; then
    sudo iptables -C INPUT -p tcp --dport "${SERVICE_PORT}" -j ACCEPT 2>/dev/null || \
        sudo iptables -I INPUT -p tcp --dport "${SERVICE_PORT}" -j ACCEPT
    if command -v netfilter-persistent &> /dev/null; then
        sudo netfilter-persistent save 2>/dev/null || true
    elif command -v iptables-save &> /dev/null; then
        sudo mkdir -p /etc/iptables
        sudo sh -c 'iptables-save > /etc/iptables/rules.v4' 2>/dev/null || true
    fi
    echo "  Firewall: iptables — port ${SERVICE_PORT} opened and persisted."
fi

# ---- Prebuilt release package? ----
#
# When this script sits next to an "app" folder - the layout of the release
# tarball - the binaries are already built for this architecture. That skips
# both the .NET download and the build, turning a multi-minute install on a Pi
# into seconds, and means the target needs neither git nor the .NET SDK.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREBUILT_DIR="${SCRIPT_DIR}/app"
if [ -d "${PREBUILT_DIR}" ] && [ -f "${PREBUILT_DIR}/ScaleReaderService" ]; then
    PREBUILT=true
else
    PREBUILT=false
fi

# ---- Install .NET ----
DOTNET_ROOT="$HOME/.dotnet"

if [ "$PREBUILT" = true ]; then
echo "[2/5] Prebuilt package - skipping .NET install (binaries are self-contained)."
else
echo "[2/5] Installing .NET runtime..."

if [ -x "$DOTNET_ROOT/dotnet" ]; then
    DOTNET_VER=$("$DOTNET_ROOT/dotnet" --version 2>/dev/null || echo "unknown")
    echo "  .NET already installed: ${DOTNET_VER}"
elif command -v dotnet &> /dev/null; then
    DOTNET_VER=$(dotnet --version 2>/dev/null || echo "unknown")
    DOTNET_ROOT=$(dirname "$(which dotnet)")
    echo "  .NET already installed: ${DOTNET_VER}"
else
    echo "  Downloading .NET ${DOTNET_CHANNEL} ASP.NET Core runtime..."
    sudo apt-get update -qq 2>/dev/null || true
    sudo apt-get install -y -qq curl libicu-dev 2>/dev/null || true
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin \
        --channel ${DOTNET_CHANNEL} \
        --runtime aspnetcore \
        --install-dir "$DOTNET_ROOT"
    echo "  .NET installed: $($DOTNET_ROOT/dotnet --version)"
fi

fi   # end of .NET install (skipped for prebuilt packages)

# Ensure dotnet is on PATH
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_ROOT

# Add to .bashrc if not already there
if ! grep -q 'DOTNET_ROOT' "$HOME/.bashrc" 2>/dev/null; then
    echo "" >> "$HOME/.bashrc"
    echo "# .NET" >> "$HOME/.bashrc"
    echo "export DOTNET_ROOT=$DOTNET_ROOT" >> "$HOME/.bashrc"
    echo 'export PATH=$DOTNET_ROOT:$PATH' >> "$HOME/.bashrc"
    echo "  Added .NET to PATH in .bashrc"
fi

# ---- Download and Build ----
echo "[3/5] Downloading and building Scale Reader Service..."

# Stop existing service if running
sudo systemctl stop ${SERVICE_NAME} 2>/dev/null || true

# Create install directory
sudo mkdir -p "${INSTALL_DIR}"
sudo chown "$USER:$USER" "${INSTALL_DIR}"

# Backup existing database
DB_BACKUP=""
if [ -f "${INSTALL_DIR}/scalereaderservice.db" ]; then
    DB_BACKUP="/tmp/scalereaderservice-db-backup.db"
    cp "${INSTALL_DIR}/scalereaderservice.db" "$DB_BACKUP"
    echo "  Backed up existing database."
fi

# Prebuilt release package?
#
# When this script sits next to an "app" folder - which is how the release
# tarball is laid out - the binaries are already built for this architecture.
# Copy them in and skip the SDK download and the build entirely. That turns a
# multi-minute build on a Pi into a few seconds, and means the target needs
# neither git nor the .NET SDK.
if [ "$PREBUILT" = true ]; then
    echo "  Copying prebuilt binaries..."
    cp -r "${PREBUILT_DIR}/." "${INSTALL_DIR}/"
    # A database must never come from a package - it would replace the site's
    # own scale configuration. Drop any stale write-ahead files too.
    rm -f "${INSTALL_DIR}/scalereaderservice.db-wal" "${INSTALL_DIR}/scalereaderservice.db-shm"
fi

if [ "$PREBUILT" = false ]; then

# Clone and build
CLONE_DIR=$(mktemp -d)
echo "  Cloning from GitHub: ${GITHUB_REPO} (${BRANCH})..."
sudo apt-get install -y -qq git 2>/dev/null || true
git clone --depth 1 --branch "${BRANCH}" "https://github.com/${GITHUB_REPO}.git" "${CLONE_DIR}"

# Check if SDK is available
# Match the MAJOR version of DOTNET_CHANNEL (e.g. "10" from "10.0") —
# `dotnet --list-sdks` prints lines like "10.0.301 [/path]". A hard-coded
# literal here silently skips the SDK install after a channel bump.
DOTNET_MAJOR="${DOTNET_CHANNEL%%.*}"
HAS_SDK=false
if dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_MAJOR}\."; then
    HAS_SDK=true
fi

if [ "$HAS_SDK" = true ]; then
    echo "  .NET SDK already installed."
else
    echo "  Installing .NET SDK permanently (reused on future updates)..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin \
        --channel ${DOTNET_CHANNEL} \
        --install-dir "$DOTNET_ROOT"
    echo "  .NET SDK installed to $DOTNET_ROOT"
fi

echo "  Building..."
dotnet publish "${CLONE_DIR}/ScaleReaderService.csproj" \
    -c Release \
    -r "${RID}" \
    --self-contained true \
    -o "${INSTALL_DIR}" \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false

rm -rf "${CLONE_DIR}"

fi   # end of build-from-source branch

# Restore database if it existed
if [ -n "$DB_BACKUP" ] && [ -f "$DB_BACKUP" ]; then
    cp "$DB_BACKUP" "${INSTALL_DIR}/scalereaderservice.db"
    rm "$DB_BACKUP"
    echo "  Restored existing database."
fi

# Set execute permission
chmod +x "${INSTALL_DIR}/ScaleReaderService" 2>/dev/null || true

# ---- Configure ----
echo "[4/5] Configuring..."

# Update appsettings.json
if [ -f "${INSTALL_DIR}/appsettings.json" ] && command -v python3 &> /dev/null; then
    python3 -c "
import json
with open('${INSTALL_DIR}/appsettings.json', 'r') as f:
    config = json.load(f)
config.setdefault('Scale', {})
config['Scale']['ServerUrl'] = '${WEB_URL}'
config['Scale']['Port'] = '${SERVICE_PORT}'
with open('${INSTALL_DIR}/appsettings.json', 'w') as f:
    json.dump(config, f, indent=2)
"
    echo "  Updated appsettings.json"
fi

# ---- Create systemd service ----
echo "[5/5] Setting up systemd service..."

# Find executable
if [ -f "${INSTALL_DIR}/ScaleReaderService" ]; then
    EXEC="${INSTALL_DIR}/ScaleReaderService"
else
    EXEC="${DOTNET_ROOT}/dotnet ${INSTALL_DIR}/ScaleReaderService.dll"
fi

sudo tee /etc/systemd/system/${SERVICE_NAME}.service > /dev/null << UNIT
[Unit]
Description=Scale Reader Service
After=network.target

[Service]
Type=simple
ExecStart=${EXEC}
WorkingDirectory=${INSTALL_DIR}
Restart=always
RestartSec=5
User=${USER}
# Explicit so serial (/dev/ttyUSB*) access never depends on when the user
# was added to dialout relative to this unit being (re)started.
SupplementaryGroups=dialout
Environment=DOTNET_ROOT=${DOTNET_ROOT}
Environment=ASPNETCORE_URLS=http://0.0.0.0:${SERVICE_PORT}
Environment=DOTNET_ENVIRONMENT=Production

NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable ${SERVICE_NAME}
sudo systemctl start ${SERVICE_NAME}

# Wait for startup
sleep 3

# Apply ServiceId and ServerUrl through the API. appsettings.json only seeds the
# database while ServerUrl is still the factory default, and ServiceId is not
# read from config at all - so on an existing install neither would otherwise
# take effect. The API triggers a soft reconnect, no restart needed.
for _ in $(seq 1 20); do
    if curl -fsS --max-time 2 -o /dev/null "http://localhost:${SERVICE_PORT}/api/status/health" 2>/dev/null; then
        curl -fsS -X PUT "http://localhost:${SERVICE_PORT}/api/settings"             -H 'Content-Type: application/json'             -d "{\"serviceId\": \"${SERVICE_ID}\", \"serverUrl\": \"${WEB_URL}\"}"             -o /dev/null 2>/dev/null && echo "  Applied ServiceId=${SERVICE_ID}, ServerUrl=${WEB_URL}"
        break
    fi
    sleep 1
done

echo ""
if sudo systemctl is-active --quiet ${SERVICE_NAME}; then
    echo "============================================"
    echo "  Install Complete!"
    echo "============================================"
    echo "  Service URL:  http://$(hostname -I | awk '{print $1}'):${SERVICE_PORT}"
    echo "  Swagger:      http://$(hostname -I | awk '{print $1}'):${SERVICE_PORT}/swagger"
    echo "  Web Server:   ${WEB_URL}"
    echo "  Service ID:   ${SERVICE_ID}"
    echo ""
    echo "  Commands:"
    echo "    sudo systemctl status ${SERVICE_NAME}"
    echo "    sudo systemctl restart ${SERVICE_NAME}"
    echo "    sudo journalctl -u ${SERVICE_NAME} -f"
    echo ""
    echo "  Configure scales via Swagger:"
    echo "    http://$(hostname -I | awk '{print $1}'):${SERVICE_PORT}/swagger"
    echo ""
    echo "  Update service ID or web URL later:"
    echo "    curl -X PUT http://localhost:${SERVICE_PORT}/api/settings \\"
    echo "      -H 'Content-Type: application/json' \\"
    echo "      -d '{\"serviceId\": \"${SERVICE_ID}\", \"serverUrl\": \"${WEB_URL}\"}'"
    echo ""
    echo "  To update the binary, re-run this install.sh — the DB is preserved."
    echo ""
    echo "  Verify the service connected to the web hub (no Connection refused):"
    echo "    sudo journalctl -u ${SERVICE_NAME} -n 20 --no-pager | grep -E 'Connect|refused'"
    echo "============================================"
else
    echo "============================================"
    echo "  WARNING: Service may not have started."
    echo "============================================"
    echo "  Check logs:"
    echo "    sudo journalctl -u ${SERVICE_NAME} -n 30 --no-pager"
    echo "============================================"
fi

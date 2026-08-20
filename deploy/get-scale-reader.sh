#!/usr/bin/env bash
# =============================================================================
# get-scale-reader.sh — download the latest prebuilt release and install it.
#
# One command on the target machine. No git, no .NET SDK, no build.
#
#   bash get-scale-reader.sh https://your-web-app-url
#
# Works whether the repo is public or private:
#
#   # public (no credentials needed)
#   bash get-scale-reader.sh https://valleyag.scaledata.net
#
#   # private, with a personal access token (needs the `repo` scope)
#   bash get-scale-reader.sh https://valleyag.scaledata.net --token ghp_xxxx
#
#   # private, using the Michelli GitHub App already set up on the Pi
#   bash get-scale-reader.sh https://valleyag.scaledata.net \
#        --token "$(michelli-github-app-token.sh)"
#
#   # or put it in the environment instead of the command line
#   GITHUB_TOKEN=ghp_xxxx bash get-scale-reader.sh https://valleyag.scaledata.net
#
# Any further options are passed straight through to install.sh
# (--service-id, --port, --install-dir, ...).
#
# Requires: curl, tar. That's it.
# =============================================================================

set -euo pipefail

REPO="${SCALE_READER_REPO:-GTMichelli-Dev/scale-reader-service}"
TOKEN="${GITHUB_TOKEN:-${GH_TOKEN:-}}"
VERSION="latest"
WEB_URL=""
PASSTHROUGH=()

# ---- arguments --------------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --token)   TOKEN="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        --repo)    REPO="$2"; shift 2 ;;
        -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
        http://*|https://*)
            if [ -z "$WEB_URL" ]; then WEB_URL="$1"; else PASSTHROUGH+=("$1"); fi
            shift ;;
        *) PASSTHROUGH+=("$1"); shift ;;
    esac
done

if [ -z "$WEB_URL" ]; then
    echo "ERROR: the web app URL is required."
    echo ""
    echo "  bash get-scale-reader.sh https://your-web-app-url [--token <pat>]"
    echo ""
    echo "Use the web app's real address — the same one you type in a browser,"
    echo "with the same scheme and port. A wrong URL leaves the service"
    echo "reconnecting forever."
    exit 1
fi

# ---- which asset for this machine ------------------------------------------
ARCH=$(uname -m)
case "$ARCH" in
    aarch64) ASSET="scale-reader-linux-arm64.tar.gz" ;;
    *)
        echo "No prebuilt release is published for '${ARCH}'."
        echo ""
        echo "Install from source instead — it builds for this architecture:"
        echo "  git clone https://github.com/${REPO}.git /tmp/srs"
        echo "  bash /tmp/srs/deploy/install.sh ${WEB_URL}"
        exit 1
        ;;
esac

echo "==> Scale Reader Service — download and install"
echo "    Repo    : ${REPO}"
echo "    Release : ${VERSION}"
echo "    Arch    : ${ARCH} -> ${ASSET}"
echo "    Web app : ${WEB_URL}"
[ -n "$TOKEN" ] && echo "    Auth    : token supplied" || echo "    Auth    : none (public repo)"
echo ""

AUTH=()
[ -n "$TOKEN" ] && AUTH=(-H "Authorization: Bearer ${TOKEN}")

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# ---- resolve the release ----------------------------------------------------
if [ "$VERSION" = "latest" ]; then
    API="https://api.github.com/repos/${REPO}/releases/latest"
else
    API="https://api.github.com/repos/${REPO}/releases/tags/${VERSION}"
fi

echo "==> Looking up the release..."
HTTP=$(curl -sS -w '%{http_code}' -o "$WORK/release.json" "${AUTH[@]}" \
       -H "Accept: application/vnd.github+json" "$API" || echo 000)

case "$HTTP" in
    200) : ;;
    401|403)
        echo "ERROR: GitHub rejected the credentials (HTTP ${HTTP})."
        echo "       A token for a private repo needs the 'repo' scope, and must"
        echo "       not be expired."
        exit 1 ;;
    404)
        echo "ERROR: no release found (HTTP 404)."
        if [ -z "$TOKEN" ]; then
            echo "       If ${REPO} is private, pass a token:"
            echo "         --token <pat>      (or set GITHUB_TOKEN)"
            echo "       A private repo returns 404 rather than 403 when"
            echo "       unauthenticated, so this looks the same as 'no releases yet'."
        else
            echo "       The token is valid but this repo/release is not visible to it."
        fi
        exit 1 ;;
    *)
        echo "ERROR: could not reach the GitHub API (HTTP ${HTTP})."
        exit 1 ;;
esac

TAG=$(grep -o '"tag_name"[[:space:]]*:[[:space:]]*"[^"]*"' "$WORK/release.json" | head -1 | sed 's/.*"\([^"]*\)"$/\1/')
echo "    Found ${TAG}"

# The asset's API id, not its browser URL. A private repo's
# /releases/download/... link is NOT token-authenticated — it redirects to a
# signed URL and answers 404 for anyone without a session. The API asset
# endpoint with Accept: application/octet-stream is the form that works with a
# token, and it works for public repos too, so there is one code path.
ASSET_ID=$(tr ',' '\n' < "$WORK/release.json" \
    | grep -B0 -A0 '"url"\|"name"' \
    | awk -v want="$ASSET" '
        /"url":[[:space:]]*"[^"]*releases\/assets\/[0-9]+"/ {
            match($0, /assets\/[0-9]+/); id = substr($0, RSTART+7, RLENGTH-7)
        }
        $0 ~ "\"name\":[[:space:]]*\"" want "\"" { print id; exit }
      ')

if [ -z "$ASSET_ID" ]; then
    echo "ERROR: release ${TAG} has no asset named '${ASSET}'."
    echo "       Assets present:"
    grep -o '"name"[[:space:]]*:[[:space:]]*"[^"]*"' "$WORK/release.json" \
        | sed 's/.*"\([^"]*\)"$/         \1/' | grep -v "^         ${TAG}$" || true
    exit 1
fi

# ---- download ---------------------------------------------------------------
echo "==> Downloading ${ASSET}..."
HTTP=$(curl -sSL -w '%{http_code}' -o "$WORK/${ASSET}" "${AUTH[@]}" \
       -H "Accept: application/octet-stream" \
       "https://api.github.com/repos/${REPO}/releases/assets/${ASSET_ID}" || echo 000)

if [ "$HTTP" != "200" ]; then
    echo "ERROR: download failed (HTTP ${HTTP})."
    exit 1
fi

SIZE=$(du -h "$WORK/${ASSET}" | cut -f1)
echo "    ${SIZE} downloaded"

# A truncated or HTML error page would otherwise fail confusingly inside tar.
if ! tar -tzf "$WORK/${ASSET}" > /dev/null 2>&1; then
    echo "ERROR: the downloaded file is not a valid archive."
    echo "       First bytes:"
    head -c 200 "$WORK/${ASSET}" | sed 's/^/         /'
    exit 1
fi

echo "==> Extracting..."
mkdir -p "$WORK/pkg"
tar -xzf "$WORK/${ASSET}" -C "$WORK/pkg"

if [ ! -f "$WORK/pkg/install.sh" ]; then
    echo "ERROR: the package has no install.sh — wrong asset?"
    exit 1
fi

echo "==> Running the installer..."
echo ""
chmod +x "$WORK/pkg/install.sh" "$WORK/pkg/app/ScaleReaderService" 2>/dev/null || true
bash "$WORK/pkg/install.sh" "$WEB_URL" ${PASSTHROUGH+"${PASSTHROUGH[@]}"}

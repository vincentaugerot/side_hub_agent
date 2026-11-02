#!/bin/bash
set -e

# SideHub Agent Installer for macOS/Linux
# Requires: Node.js (for PTY terminal support)
#
# Usage: curl -fsSL https://api.sidehub.io/agent/install.sh | SIDEHUB_SETUP_TOKEN=<token> bash -s -- [version]
#   SIDEHUB_SETUP_TOKEN (or --token <token>, visible in ps)  run from the project folder: after installing,
#   configure this folder for the agent and start it
#   --allow-root  configure and start it even as root (refused by default: the backend would control the machine)
#   --user        install in ~/.local (no sudo); also the fallback when /usr/local isn't writable and sudo is missing
#                 or refused (account not allowed to use it, wrong password, no terminal to ask it)
#
#   SIDEHUB_INSTALL_DIR  install folder (default /usr/local/lib/sidehub-agent, ~/.local/lib/sidehub-agent with --user);
#   an existing folder is only replaced if it holds a previous agent install
#   SIDEHUB_BIN_DIR      where the sidehub-agent and sidehub-cli links go (default /usr/local/bin, ~/.local/bin with --user)
#
# The archive is checked against the release's checksums.sha256, downloaded from GitHub Releases
# (SIDEHUB_GITHUB_REPO, default vincentaugerot/side_hub_agent); a missing or mismatching checksum aborts the install.
# checksums.sha256 itself must carry a valid signature (checksums.sha256.sig) from the release key below:
# write access to the GitHub release alone is not enough to ship a tampered archive.

# Downloads live under /agent at the API host root (REST routes are under /api): accept either form
SIDEHUB_API="${SIDEHUB_API:-https://api.sidehub.io}"
SIDEHUB_API="${SIDEHUB_API%/}"
SIDEHUB_API="${SIDEHUB_API%/api}"
# Checksums come straight from GitHub Releases, not through the SideHub API that hands out the archive (a redirect
# to the release asset):
# a compromised API cannot hand out both a tampered archive and a matching checksum.
GITHUB_REPO="${SIDEHUB_GITHUB_REPO:-vincentaugerot/side_hub_agent}"
# Resolved by resolve_install_target: system-wide (sudo when needed) or in the user's home
INSTALL_DIR=""
BIN_DIR=""
BIN_LINK=""
CLI_BIN_LINK=""
SUDO=""
# Written in every install folder: proves a folder is ours before it is wiped on reinstall
INSTALL_MARKER=".sidehub-agent-install"
PROJECT_DIR="$(pwd)"
# pty-helper's node-pty needs it (the onboarding asks for it too)
MIN_NODE_MAJOR=18

# Release signing key (RSA, PKCS#1 v1.5 / SHA-256 over checksums.sha256), the private half is the
# RELEASE_SIGNING_KEY secret of the release workflow. Keep in sync with install.ps1 (ReleaseSigningKeyTests).
RELEASE_SIGNING_PUBKEY='-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAt9EUPG9pOyOv9va4UK8v
I4mJA4nPN7GUiE+Cj7aek6OI3WSl1ma5Bdp7rPYj/aYX+XiM4FMGbzqpLwiYauZz
Cr4Xlf4Nka/3AKzvl7PorFXCnj1Y5aSw5A5t7loAPEwC6SIQoCvSFmMnDUcbOKje
t/jhe2aHFk8AoKIhtD1OPP1fYunv9+pmFNYV3RaoTW4KqIvKviJlPOYiJF+eFu1k
2yXQt9js+hzOBztXIlm3FKR9Te/mzEEbdrZo0SCh4r51yxR/n2Gn4SDlHBqSGxrH
NqGSMmXNdqsyQuzb6Pu6fYIweRD7gW4gd7bLRDe7bFXHEI2VGlT+5hUEaAfl8Fbo
oLPHu2P7i+UkiPrDzcehNCQ0LQyyyNEZIxEL63zHWCEsmiXBoRXpaJg94i1Y1Zzm
nh4Q2DlpTCeVzgUGqgLGUaR4CiJdJRnFQlB+9Rm9UsRIJV5RihAW4R/fEGvBX2Nv
0WKMxs9stdwcYmYrk19LJGLlFfpL9c5t9TF0KDB2SiIVAgMBAAE=
-----END PUBLIC KEY-----'
# Releases published before signing: installed on the checksum alone (with a warning). Any later release
# without a signature is refused, so deleting checksums.sha256.sig does not downgrade the check.
LAST_UNSIGNED_VERSION="1.0.61"
# First release whose archive bundles pty-helper's node_modules (npm ci from the lockfile, in the CI).
# Older ones need `npm install` from the registry at install time, running package scripts: refused.
MIN_VERSION="1.0.59"
# From this release `sidehub-agent setup` reports its own failure, with the error it printed
SETUP_REPORTS_VERSION="1.0.95"

# Failure reports: with a token, a failed install tells SideHub why (POST /api/agent/diagnostics), so a stuck
# account shows the cause in SideHub. Only the first 16 characters of the token, the stage that failed and a short
# detail without user paths, the version being installed and the platform are sent (see README "Failure reports").
FAIL_REASON="install-failed"
FAIL_DETAIL=""
TOKEN=""
TAG=""
TMP_DIR=""

# The stage being run: what is reported if the script exits with an error from here on
stage() { FAIL_REASON="$1"; FAIL_DETAIL="$2"; }

json_escape() { printf '%s' "$1" | tr -d '\000-\037' | sed 's/\\/\\\\/g; s/"/\\"/g'; }

report_failure() {
    [ -n "$FAIL_REASON" ] && [ "${#TOKEN}" -ge 16 ] || return 0
    command -v curl > /dev/null 2>&1 || return 0
    local body
    body=$(printf '{"tokenPrefix":"%s","reason":"%s","detail":"%s","agentVersion":"%s","os":"%s"}' \
        "$(json_escape "${TOKEN:0:16}")" "$(json_escape "$FAIL_REASON")" "$(json_escape "$FAIL_DETAIL")" \
        "$(json_escape "install.sh${TAG:+ $TAG}")" "$(json_escape "$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)")")
    # Best-effort and bounded: never hold the user's terminal, never fail the script a second time
    curl -fsS -m 5 -X POST -H 'Content-Type: application/json' --data "$body" \
        "${SIDEHUB_API}/api/agent/diagnostics" > /dev/null 2>&1 || true
}

on_exit() {
    local code=$?
    [ -n "$TMP_DIR" ] && rm -rf "$TMP_DIR"
    [ "$code" -ne 0 ] && report_failure
    exit "$code"
}
trap on_exit EXIT

# Succeeds if version $1 > version $2 (dotted numbers, e.g. 1.0.61)
version_gt() {
    local IFS=.
    local -a a=($1) b=($2)
    local i x y
    for i in 0 1 2 3; do
        x=$((10#${a[i]:-0})); y=$((10#${b[i]:-0}))
        [ "$x" -gt "$y" ] && return 0
        [ "$x" -lt "$y" ] && return 1
    done
    return 1
}

# Check Node.js, and its version: pty-helper does not start on an older one
check_nodejs() {
    if ! command -v node &> /dev/null; then
        echo "❌ Node.js is required but not installed."
        echo "   Install Node.js ${MIN_NODE_MAJOR} or later from https://nodejs.org or via your package manager."
        exit 1
    fi
    local version major
    version=$(node --version 2>/dev/null || true)
    major="${version#v}"
    major="${major%%.*}"
    if ! [[ "$major" =~ ^[0-9]+$ ]] || [ "$major" -lt "$MIN_NODE_MAJOR" ]; then
        FAIL_DETAIL="node ${version:-unknown} found, ${MIN_NODE_MAJOR} or later required"
        echo "❌ Node.js ${version:-(unknown version)} is too old: SideHub Agent needs Node.js ${MIN_NODE_MAJOR} or later."
        echo "   Update it from https://nodejs.org, via your package manager or nvm (nvm install --lts)."
        exit 1
    fi
    echo "✓ Node.js $version found"
}

# Detect platform. Prints "<os>-<arch>"; errors go to stderr, since stdout is captured by the caller.
detect_platform() {
    local os arch
    os=$(uname -s | tr '[:upper:]' '[:lower:]')
    arch=$(uname -m)

    case "$os" in
        darwin) os="osx" ;;
        linux) os="linux" ;;
        *) echo "❌ Unsupported OS: $os (SideHub Agent runs on Linux, macOS and Windows)" >&2; return 1 ;;
    esac

    case "$arch" in
        x86_64|amd64) arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        *) echo "❌ Unsupported architecture: $arch (x64 and arm64 only)" >&2; return 1 ;;
    esac

    echo "${os}-${arch}"
}

# Whether the current user can create $1: the nearest existing folder on its path is writable
can_create() {
    local dir="$1"
    while [ ! -e "$dir" ]; do dir=$(dirname "$dir"); done
    [ -w "$dir" ]
}

# Whether sudo can be used: asks for the password now (through the terminal, even under `curl | bash`), before the
# download, rather than failing at the copy. Fails for an account not allowed to use sudo (a standard account on a
# managed Mac), a wrong password, or no terminal to ask it in.
sudo_usable() {
    sudo -n true 2> /dev/null && return 0
    echo "🔐 $INSTALL_DIR isn't writable: sudo asks for your password."
    sudo -v
}

# System-wide in /usr/local (through sudo when it isn't writable), or in ~/.local with --user. Without a usable sudo,
# and with no folder chosen by the user, fall back to ~/.local rather than fail.
resolve_install_target() {
    local user_install="$1"
    local default_lib="/usr/local/lib/sidehub-agent" default_bin="/usr/local/bin"
    if [ -n "$user_install" ]; then
        default_lib="$HOME/.local/lib/sidehub-agent"
        default_bin="$HOME/.local/bin"
    fi
    INSTALL_DIR="${SIDEHUB_INSTALL_DIR:-$default_lib}"
    BIN_DIR="${SIDEHUB_BIN_DIR:-$default_bin}"

    SUDO=""
    if ! can_create "$INSTALL_DIR" || ! can_create "$BIN_DIR"; then
        if command -v sudo &> /dev/null && sudo_usable; then
            SUDO="sudo"
        elif [ -z "$user_install" ] && [ -z "${SIDEHUB_INSTALL_DIR:-}" ] && [ -z "${SIDEHUB_BIN_DIR:-}" ]; then
            echo "ℹ️  /usr/local isn't writable and sudo isn't available to this account: installing in ~/.local instead."
            resolve_install_target user
            return
        else
            echo "❌ Can't write to $INSTALL_DIR or $BIN_DIR, and sudo isn't available to this account."
            echo "   Install in your home folder instead: replace the final bash of the command with bash -s -- --user."
            return 1
        fi
    fi
    BIN_LINK="$BIN_DIR/sidehub-agent"
    CLI_BIN_LINK="$BIN_DIR/sidehub-cli"
}

# Resolve the latest release tag from GitHub (redirect of /releases/latest to /releases/tag/<tag>)
resolve_latest_tag() {
    local effective
    effective=$(curl -fsSL -o /dev/null -w '%{url_effective}' "https://github.com/${GITHUB_REPO}/releases/latest") || return 1
    local tag="${effective##*/}"
    case "$tag" in
        v[0-9]*) echo "$tag" ;;
        *) return 1 ;;
    esac
}

sha256_of() {
    if command -v sha256sum &> /dev/null; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum &> /dev/null; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        return 1
    fi
}

# Verify checksums.sha256 against its detached signature with the embedded release key
verify_signature() {
    local checksums_file="$1" tag="$2" tmp_dir="$3"
    local sig_file="$tmp_dir/checksums.sha256.sig" pubkey_file="$tmp_dir/release-signing-key.pem"
    local sig_url="https://github.com/${GITHUB_REPO}/releases/download/${tag}/checksums.sha256.sig"

    local status
    status=$(curl -sSL -o "$sig_file" -w '%{http_code}' "$sig_url") || status="000"
    if [ "$status" = "404" ] && ! version_gt "${tag#v}" "$LAST_UNSIGNED_VERSION"; then
        echo "⚠️  $tag predates signed releases: only the SHA256 checksum is verified."
        return 0
    fi
    if [ "$status" != "200" ]; then
        echo "❌ Checksums signature not found ($sig_url, HTTP $status)"
        return 1
    fi

    if ! command -v openssl &> /dev/null; then
        echo "❌ openssl is required to verify the release signature"
        return 1
    fi
    printf '%s\n' "$RELEASE_SIGNING_PUBKEY" > "$pubkey_file"
    if ! openssl dgst -sha256 -verify "$pubkey_file" -signature "$sig_file" "$checksums_file" > /dev/null 2>&1; then
        echo "❌ Invalid signature for checksums.sha256 ($tag): release not published by SideHub"
        return 1
    fi
    echo "✓ Release signature verified ($tag)"
}

# Refuse to wipe a folder that is not a previous agent install (a mistyped SIDEHUB_INSTALL_DIR, /usr/local/lib…)
check_install_dir() {
    case "$INSTALL_DIR" in
        /*) ;;
        *) echo "❌ The install folder must be an absolute path: $INSTALL_DIR"; return 1 ;;
    esac
    case "$INSTALL_DIR" in
        */../*|*/..|*/./*|*/.) echo "❌ The install folder must not contain . or ..: $INSTALL_DIR"; return 1 ;;
    esac
    [ -e "$INSTALL_DIR" ] || return 0
    if [ -L "$INSTALL_DIR" ] || [ ! -d "$INSTALL_DIR" ]; then
        echo "❌ $INSTALL_DIR exists and is not a folder"
        return 1
    fi
    # Empty, ours (marker), or an install from before the marker (agent binary + pty-helper)
    if [ -z "$(ls -A "$INSTALL_DIR")" ] || [ -f "$INSTALL_DIR/$INSTALL_MARKER" ] \
        || { [ -f "$INSTALL_DIR/sidehub-agent" ] && [ -d "$INSTALL_DIR/pty-helper" ]; }; then
        return 0
    fi
    echo "❌ $INSTALL_DIR is not empty and holds no SideHub Agent install: aborting."
    echo "   Pick another folder (SIDEHUB_INSTALL_DIR) or empty it yourself."
    return 1
}

# Verify the archive against the release's checksums.sha256; any failure aborts the install
verify_checksum() {
    local archive_file="$1" asset_name="$2" tag="$3" tmp_dir="$4"
    local checksums_file="$tmp_dir/checksums.sha256"
    local checksums_url="https://github.com/${GITHUB_REPO}/releases/download/${tag}/checksums.sha256"

    stage install-download-failed "checksums.sha256 download from GitHub failed ($tag)"
    if ! curl -fsSL "$checksums_url" -o "$checksums_file"; then
        echo "❌ Unable to download the checksums from $checksums_url"
        return 1
    fi

    stage install-verification-failed "release signature or archive checksum invalid ($tag)"
    verify_signature "$checksums_file" "$tag" "$tmp_dir" || return 1

    # sha256sum format: "<hash>  <name>" (or "<hash> *<name>" in binary mode)
    local expected
    expected=$(awk -v name="$asset_name" '{ f = $2; sub(/^\*/, "", f); if (f == name) { print tolower($1); exit } }' "$checksums_file")
    if [ -z "$expected" ]; then
        echo "❌ No checksum for $asset_name in checksums.sha256 ($tag)"
        return 1
    fi

    local actual
    if ! actual=$(sha256_of "$archive_file"); then
        echo "❌ sha256sum or shasum is required to verify the archive"
        return 1
    fi

    if [ "$actual" != "$expected" ]; then
        echo "❌ Checksum mismatch for $asset_name: expected $expected, got $actual"
        return 1
    fi

    echo "✓ SHA256 checksum verified ($asset_name, $tag)"
}

# Download and install
install() {
    local version="latest"
    local token="${SIDEHUB_SETUP_TOKEN:-}"
    local allow_root="" user_install=""
    case "${SIDEHUB_ALLOW_ROOT:-}" in 1|true|TRUE|True) allow_root="--allow-root" ;; esac
    while [ $# -gt 0 ]; do
        case "$1" in
            --allow-root) allow_root="--allow-root"; shift ;;
            --user) user_install="user"; shift ;;
            --token) token="$2"; shift 2 ;;
            --token=*) token="${1#--token=}"; shift ;;
            *) version="$1"; shift ;;
        esac
    done
    TOKEN="$token"

    stage install-node-missing "node not found in PATH"
    check_nodejs

    stage install-failed "unsupported platform: $(uname -s)-$(uname -m)"
    local platform
    platform=$(detect_platform) || exit 1

    # Pin "latest" to a tag so the archive and its checksum come from the same release
    local tag
    stage install-download-failed "couldn't resolve the latest release from GitHub"
    if [ "$version" = "latest" ]; then
        if ! tag=$(resolve_latest_tag); then
            echo "❌ Unable to resolve the latest version from https://github.com/${GITHUB_REPO}/releases"
            exit 1
        fi
    else
        tag="v${version#v}"
    fi
    # The tag goes into URLs and messages: a plain version only
    stage install-failed "invalid or too old version requested"
    if ! [[ "$tag" =~ ^v[0-9]+(\.[0-9]+){1,3}$ ]]; then
        echo "❌ Invalid version: $version (expected 1.0.61 or v1.0.61)"
        exit 1
    fi
    if version_gt "$MIN_VERSION" "${tag#v}"; then
        echo "❌ $tag can no longer be installed: releases before v${MIN_VERSION} install their Node.js"
        echo "   dependencies from the npm registry at install time. Install v${MIN_VERSION} or later."
        exit 1
    fi

    TAG="$tag"

    stage install-permission-denied "install folder not writable, and sudo missing or refused"
    resolve_install_target "$user_install" || exit 1

    stage install-failed "install folder exists and holds something else than an agent install"
    check_install_dir || exit 1

    local asset_name="sidehub-agent-${platform}.tar.gz"
    local url="${SIDEHUB_API}/agent/download/${platform}/${tag}"

    if [ "$version" = "latest" ]; then
        echo "📦 Downloading SideHub Agent ${tag} (latest, ${platform})..."
    else
        echo "📦 Downloading SideHub Agent ${tag} (${platform})..."
    fi

    # Global (not local) so the EXIT trap still sees it once install() has returned
    TMP_DIR=$(mktemp -d)
    local archive_file="$TMP_DIR/agent.tar.gz"
    local extract_dir="$TMP_DIR/package"

    stage install-download-failed "archive download failed (${SIDEHUB_API}/agent/download/${platform}/${tag})"
    if ! curl -fsSL "$url" -o "$archive_file"; then
        echo "❌ Unable to download from $url"
        exit 1
    fi

    if ! verify_checksum "$archive_file" "$asset_name" "$tag" "$TMP_DIR"; then
        echo "   Installation aborted: the archive was not extracted."
        exit 1
    fi

    stage install-failed "archive extraction failed, or pty-helper dependencies missing from it"
    echo "📁 Extracting..."
    mkdir -p "$extract_dir"
    tar -xzf "$archive_file" -C "$extract_dir"

    # node_modules ships prebuilt in the verified archive (npm ci from the lockfile, in the release CI):
    # the install never runs npm.
    if [ ! -d "$extract_dir/pty-helper/node_modules/node-pty" ]; then
        echo "❌ The $tag archive does not bundle pty-helper's Node.js dependencies: aborting."
        exit 1
    fi
    echo "✓ Node.js dependencies bundled in the archive"
    touch "$extract_dir/$INSTALL_MARKER"

    stage install-permission-denied "couldn't write the install folder or the links in the bin folder ($([ -n "$SUDO" ] && echo with || echo without) sudo)"
    echo "🔧 Installing to ${INSTALL_DIR}${SUDO:+ (with sudo)}..."
    # $SUDO is empty when both folders are writable: the commands then run as is
    $SUDO rm -rf "$INSTALL_DIR"
    $SUDO mkdir -p "$INSTALL_DIR"
    $SUDO cp -R "$extract_dir/." "$INSTALL_DIR/"
    # /usr/local/bin can be missing on a fresh Apple Silicon Mac (Homebrew lives in /opt/homebrew)
    $SUDO mkdir -p "$BIN_DIR"
    $SUDO rm -f "$BIN_LINK"
    $SUDO ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
    if [ -f "$INSTALL_DIR/sidehub-cli" ]; then
        $SUDO rm -f "$CLI_BIN_LINK"
        $SUDO ln -s "$INSTALL_DIR/sidehub-cli" "$CLI_BIN_LINK"
    fi

    echo ""
    echo "✅ SideHub Agent ${tag} installed successfully!"
    echo ""
    case ":$PATH:" in
        *":$BIN_DIR:"*) ;;
        *)
            echo "⚠️  $BIN_DIR is not in your PATH: add it to use sidehub-agent from any folder, e.g."
            echo "     echo 'export PATH=\"$BIN_DIR:\$PATH\"' >> ~/.bashrc   (or ~/.zshrc), then open a new terminal"
            echo ""
            ;;
    esac

    if [ -n "$token" ] && [ "$(id -u)" -eq 0 ] && [ -z "$allow_root" ]; then
        # `curl … | sudo bash` with a token: installing system-wide needs root, running the agent must not.
        stage root-refused "install.sh run as root without --allow-root: agent installed, not configured"
        echo "⛔ Agent installed but not configured: this script runs as root, and the agent refuses to run as root"
        echo "   (SideHub drives its terminals: as root, it would control the whole machine)."
        echo ""
        echo "   Recommended, an unprivileged user that owns the project:"
        echo "     adduser sidehub                     # once (useradd -m -s /bin/bash sidehub on some distros)"
        echo "     loginctl enable-linger sidehub      # its services start at boot, without a login"
        echo "     su - sidehub                        # then, as sidehub (claude / codex installed and logged in for it):"
        echo "     git clone <your repository> && cd <it>"
        echo "     sidehub-agent setup --token-stdin   # then paste the token copied from SideHub"
        echo "     sidehub-agent service install       # start it again after a reboot"
        echo ""
        echo "   Or keep root anyway (SideHub then controls this machine): run the same command again"
        echo "   with --allow-root after the token (or SIDEHUB_ALLOW_ROOT=1)."
        exit 1
    fi

    if [ -n "$token" ]; then
        # Configure the folder the command was run from (the project), then start the agent in the background.
        cd "$PROJECT_DIR"
        echo "🔗 Configuring the agent in ${PROJECT_DIR}..."
        # Token through the environment, not argv: argv is visible to every user in ps.
        stage install-setup-failed "sidehub-agent setup failed after the install"
        local code=0
        SIDEHUB_API="$SIDEHUB_API" SIDEHUB_SETUP_TOKEN="$token" "$BIN_LINK" setup $allow_root || code=$?
        if [ "$code" -ne 0 ]; then
            # Exit code 1: setup ran and reported why, this generic report would replace its detail in SideHub
            if [ "$code" -eq 1 ] && ! version_gt "$SETUP_REPORTS_VERSION" "${TAG#v}"; then FAIL_REASON=""; fi
            exit "$code"
        fi
        echo ""
        echo "To start it again after a reboot: sidehub-agent service install $allow_root"
        echo "Useful commands: sidehub-agent status · sidehub-agent doctor · sidehub-agent logs · sidehub-agent stop"
    else
        echo "To get started, from your project folder:"
        echo "  sidehub-agent setup --token-stdin   (then paste the token copied from SideHub)"
        echo "  (as an unprivileged user: the agent refuses to run as root)"
        echo "  sidehub-agent service install       (start it again after a reboot)"
        echo ""
    fi
}

install "$@"

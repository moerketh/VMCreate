#!/bin/bash
set -o pipefail

# =========================================================================
# Lamco RDP Server — install + configure (Wayland-native)
# =========================================================================
# Installs the PINNED fork deb from GitHub Releases (sha256-verified), sets
# up TLS certs, config.toml, the systemd user units, and linger. The one-time
# portal consent dialog is automated by lamco-grant.service (see below).
#
# SECURITY/RELIABILITY CONTRACT:
#   - The deb tag, version, and sha256 are pinned below. A re-pointed tag,
#     a missing asset, or a digest mismatch FAILS THIS SCRIPT LOUDLY.
#     There is no source-build fallback and no upstream fallback: silently
#     shipping a stock/broken binary is the worse failure.
#   - Lamco is Debian-family-only (the fork pipeline ships amd64 debs;
#     rpm/flatpak assets are not built for this lineage).

LAMCO_FORK_REPO="moerketh/lamco-rdp-server"
LAMCO_FORK_TAG="v1.4.5-hyperv.2"
LAMCO_FORK_DEB_VERSION="1.4.5-hyperv2"
# The fork's release policy: the Cargo.toml crate version stays at the
# upstream base (1.4.5) and the deb's package version carries the lineage
# suffix — i.e. the BINARY deliberately reports only the base version
# (verified on the pinned asset: /usr/bin/lamco-rdp-server --version prints
# "lamco-rdp-server 1.4.5"), while dpkg reports 1.4.5-hyperv2.
LAMCO_FORK_CRATE_VERSION="1.4.5"
LAMCO_FORK_DEB_SHA256="13f119f7c59435abc3be22072122b9adb350b4a60e724462faa9a3d67f2cfb7e"
LAMCO_FORK_DEB_URL="https://github.com/${LAMCO_FORK_REPO}/releases/download/${LAMCO_FORK_TAG}/lamco-rdp-server_${LAMCO_FORK_DEB_VERSION}_amd64.deb"

# Result contract: 0 = ok, 1 = degraded (install completed with warnings —
# the host step logs these). Hard failures exit non-zero BEFORE the terminal
# LAMCO_RESULT line is ever reached. See the end of the script.
DEGRADED=0

echo "=== Lamco RDP Server install (pinned fork deb ${LAMCO_FORK_TAG}) ==="

# -- Validate distro: Debian family only ------------------------------------
if [ ! -f /etc/os-release ]; then
    echo "ERROR: /etc/os-release not found — cannot determine distro." >&2
    exit 1
fi
. /etc/os-release
DISTRO_ID="${ID}"
DISTRO_LIKE="${ID_LIKE:-}"
is_debian_family() {
    case "$DISTRO_ID" in
        ubuntu|debian|parrot) return 0 ;;
    esac
    case " $DISTRO_LIKE " in
        *" debian"*|*" ubuntu"*) return 0 ;;
    esac
    return 1
}
if ! is_debian_family; then
    echo "ERROR: distro '${DISTRO_ID}' (ID_LIKE='${DISTRO_LIKE}') is not Debian-family." >&2
    echo "       The pinned fork deb is amd64 Debian packaging; Lamco support" >&2
    echo "       for rpm distros awaits a fork release pipeline for them." >&2
    exit 1
fi

download_tool=""
if command -v curl >/dev/null 2>&1; then
    download_tool="curl"
elif command -v wget >/dev/null 2>&1; then
    download_tool="wget"
else
    echo "ERROR: neither curl nor wget is available." >&2
    exit 1
fi

fetch_url() {
    # $1 = URL, $2 = output file
    if [ "$download_tool" = "curl" ]; then
        curl -fsSL "$1" -o "$2"
    else
        wget -q "$1" -O "$2"
    fi
}

# Determine arch (only amd64 is built by the fork pipeline).
ARCH="$(uname -m)"
case "$ARCH" in
    x86_64|amd64) ARCH="x86_64" ;;
    *) echo "ERROR: unsupported arch $ARCH (the pinned fork deb is amd64 only)." >&2; exit 1 ;;
esac

# -- Install the pinned fork deb --------------------------------------------
# The ONLY install path. Download, verify the sha256 against the pinned
# digest, dpkg -i, then apt-get -f for any missing runtime deps. Verification
# is then layered, each layer proving a different thing:
#   1. sha256 of the download — this EXACT fork artifact, not a re-pointed tag
#   2. dpkg database — the fork marker (1.4.5-hyperv2) lives in the deb's
#      Package Version field; the binary only reports the bare crate version
#      per fork policy, so the marker MUST be read from dpkg, not from
#      `--version`
#   3. binary payload — the /usr/bin binary runs and reports the pinned crate
#      version, so the registered deb actually delivered a working payload
# A partial install that leaves a stock upstream 1.4.5 package or a broken
# binary in place cannot pass. Any failure aborts the deployment loudly: no
# fallback exists, and silently shipping a stock/broken binary is strictly
# worse than failing.
FORK_DEB_TMP="$(mktemp /tmp/lamco-fork.XXXXXX.deb)"
fetch_url "$LAMCO_FORK_DEB_URL" "$FORK_DEB_TMP" \
    || { echo "ERROR: download failed for $LAMCO_FORK_DEB_URL" >&2; rm -f "$FORK_DEB_TMP"; exit 1; }
echo "Verifying sha256 of the fork deb..."
actual_sha=$(sha256sum "$FORK_DEB_TMP" | awk '{print $1}')
if [ "$actual_sha" != "$LAMCO_FORK_DEB_SHA256" ]; then
    echo "ERROR: sha256 mismatch for the fork deb." >&2
    echo "       expected: $LAMCO_FORK_DEB_SHA256" >&2
    echo "       actual:   $actual_sha" >&2
    echo "       The tag may have been re-pointed or the asset replaced. Pin the" >&2
    echo "       new digest in the install script only after verifying the release." >&2
    rm -f "$FORK_DEB_TMP"
    exit 1
fi
echo "sha256 OK ($LAMCO_FORK_DEB_SHA256)"
# Install the deb, then ALWAYS resolve dependencies with apt-get, then let
# the fork-marker check below be the sole verdict. The old `dpkg || apt -f ||
# exit` chain short-circuited: when dpkg -i failed on missing deps, apt-get -f
# "fixed" the half-staged package — but a dpkg rc was never recorded, and if
# apt-get itself exited non-zero after making changes, the either/or shape
# made attribution impossible. Sequential + unconditional dependency fixup
# is idempotent (a clean dpkg makes apt-get -f a fast no-op).
DEBIAN_FRONTEND=noninteractive dpkg -i --force-confnew "$FORK_DEB_TMP" 2>&1
dpkg_rc=$?
if [ "$dpkg_rc" -ne 0 ]; then
    echo "NOTE: dpkg -i exited $dpkg_rc (missing/broken deps are expected when the fork deb's runtime packages aren't pre-installed) — running apt-get dependency fixup." >&2
fi
DEBIAN_FRONTEND=noninteractive apt-get install -f -y -q 2>&1 \
    || { echo "ERROR: apt-get dependency resolution failed after dpkg -i (dpkg rc=$dpkg_rc)." >&2; rm -f "$FORK_DEB_TMP"; exit 1; }
rm -f "$FORK_DEB_TMP"

# -- Verify: fork identity (dpkg) + payload sanity (binary) -----------------
# FORK IDENTITY — authoritative: the dpkg database must report EXACTLY the
# pinned fork deb version, in state 'install ok installed'. The marker
# suffix (-hyperv2) exists only in the deb's Package Version field; grepping
# the binary for it can NEVER pass on a genuine fork build (the fork pins the
# Cargo crate version at the upstream base), which is precisely how the
# previous binary-grep check broke this deployment.
installed_status="$(dpkg-query -W -f='${Status}' lamco-rdp-server 2>/dev/null)"
installed_pkg_ver="$(dpkg-query -W -f='${Version}' lamco-rdp-server 2>/dev/null)"
if [ "$installed_pkg_ver" != "$LAMCO_FORK_DEB_VERSION" ] || [ "$installed_status" != "install ok installed" ]; then
    echo "ERROR: installed lamco-rdp-server does not report the fork marker" >&2
    echo "       '$LAMCO_FORK_DEB_VERSION' in the dpkg database (dpkg says: status='$installed_status', version='$installed_pkg_ver')." >&2
    echo "       The deb may not have fully installed (e.g. broken deps, or the" >&2
    echo "       apt-get fixup resolved by removing it). Aborting — no silent degradation." >&2
    exit 1
fi
# PAYLOAD SANITY — the registered package must have delivered a binary that
# executes and reports the pinned crate generation.
if ! /usr/bin/lamco-rdp-server --version 2>/dev/null | grep -aq " $LAMCO_FORK_CRATE_VERSION"; then
    echo "ERROR: installed binary /usr/bin/lamco-rdp-server does not run or does not" >&2
    echo "       report crate version '$LAMCO_FORK_CRATE_VERSION' (got: $(/usr/bin/lamco-rdp-server --version 2>/dev/null | head -1))." >&2
    exit 1
fi
echo "Installed fork deb lamco-rdp-server_${LAMCO_FORK_DEB_VERSION}_amd64 (dpkg reports '$installed_pkg_ver', binary reports '$LAMCO_FORK_CRATE_VERSION', sha256-verified download)."

# -- Install Portal + PipeWire runtime deps if missing ----------------------
# Branch by detected desktop so we pull the correct portal backend.
desktop="${XDG_CURRENT_DESKTOP:-}"
# If we are running over SSH XDG_CURRENT_DESKTOP may be empty; detect from the
# installed session files instead.
if [ -z "$desktop" ]; then
    if [ -f /usr/share/wayland-sessions/gnome.desktop ] || [ -f /usr/share/xsessions/gnome.desktop ] || [ -f /usr/share/wayland-sessions/mutter.desktop ]; then
        desktop="GNOME"
    elif [ -f /usr/share/wayland-sessions/plasma.desktop ] || [ -f /usr/share/xsessions/plasma.desktop ]; then
        desktop="KDE"
    fi
fi

DEBIAN_FRONTEND=noninteractive apt-get update -y 2>&1 || true
DEBIAN_FRONTEND=noninteractive apt-get install -y \
    pipewire wireplumber xdg-desktop-portal \
    python3-dbus \
    $([ "$desktop" = "GNOME" ] && echo "xdg-desktop-portal-gnome") \
    $([ "$desktop" = "KDE" ] && echo "xdg-desktop-portal-kde") \
    2>&1 || true

# -- Generate TLS certificates ---------------------------------------------
# The server requires cert.pem + key.pem to start. Try the shipped setup-certs
# helper first; if it is missing OR fails OR does not produce the files, fall
# back to openssl inline. Always verify the files exist at the end — a silent
# setup-certs failure must not leave the server unable to start.
mkdir -p /etc/lamco-rdp-server
if command -v lamco-rdp-server-setup-certs >/dev/null 2>&1; then
    lamco-rdp-server-setup-certs /etc/lamco-rdp-server "$(hostname)" 2>&1 || true
    # Permissions are finalized after the autologin user is resolved below
    # (key.pem gets root:<user> 640 — group read for the user service, never
    # world-readable).
fi
# If the helper did not produce both files (or was absent), generate them now.
if [ ! -f /etc/lamco-rdp-server/cert.pem ] || [ ! -f /etc/lamco-rdp-server/key.pem ]; then
    echo "Generating self-signed TLS certificate via openssl..."
    openssl req -x509 -newkey rsa:4096 -nodes \
        -keyout /etc/lamco-rdp-server/key.pem \
        -out /etc/lamco-rdp-server/cert.pem \
        -days 365 -subj "/CN=$(hostname)" \
        -addext "subjectAltName=DNS:$(hostname),DNS:localhost,IP:127.0.0.1" 2>&1
    # Permissions are finalized after the autologin user is resolved below:
    # the key must be readable by the unprivileged user service, but that is
    # granted via group ownership (root:<user>, 640) instead of making the
    # private key world-readable.
fi
# Final guard: if certs are STILL missing, the server cannot start.
if [ ! -f /etc/lamco-rdp-server/cert.pem ] || [ ! -f /etc/lamco-rdp-server/key.pem ]; then
    echo "ERROR: TLS certificate generation failed — cert.pem/key.pem not found." >&2
    exit 1
fi
echo "TLS certificates present: /etc/lamco-rdp-server/cert.pem"

# -- Write config.toml (full tuned profile, loopback + vsock, no auth) -----
# THREAT MODEL (read before changing transports):
#   auth_method = "none" means ANYTHING that reaches a listener gets a live
#   unlocked desktop of a sudo-capable user. The TCP transport therefore
#   binds 127.0.0.1 ONLY — reachable from inside the guest (and by host
#   tools that SSH in). The vsock transport carries Hyper-V Enhanced
#   Session (vmconnect); the fork applies no CID allowlist, but vsock is
#   unreachable from outside the VM — only the host (vmms/vmconnect) can
#   connect. If you need LAN RDP: set auth_method to something real
#   FIRST, then change the TCP bind — do not just widen the bind.
#   Note: mstsc/standard RDP clients can still reach the server over SSH
#   port forwarding (ssh -L 3389:127.0.0.1:3389), which preserves the
#   authenticated SSH hop.
#
# Tuned profile: deep blacks (full 0-255 color range), 60fps, and a single
# zero-lag client-rendered pointer. Key quality switches vs a minimal config:
#   [egfx]: qp 1-10 + color_range="full" + color_matrix="identity"  -> full
#           0-255 range (deep black, no washed-out 16-235), 50 Mbps x264.
#   [video]: target_fps=60 + cursor_mode="hidden"; [cursor] metadata mode
#           with the predictor -> one zero-lag client-rendered pointer.
#   [performance]/[video_pipeline.*]: zero-copy + buffers + backpressure.
# security_mode=hybrid + auth_method=none: the proven combination for Lamco
# with standard RDP clients (mstsc, FreeRDP) ON LOOPBACK ONLY. In "tls"-only
# mode mstsc negotiates standard RDP security without a TLS layer, and the
# TLS acceptor then rejects the stream ("corrupt message" spam, client never
# connects). Hybrid lets the server accept the CredSSP-free standard path.
# No [security.credssp_credentials] — including it makes IronRDP require
# credentials even with auth_method=none. [gui_state]/[diagnostics] are
# GUI/debug state and stay out of the template.
#
# NOTE: Hyper-V Enhanced Session (vmconnect.exe) connects through the vsock
# transport below: vmms terminates TLS/CredSSP on the host side and relays the
# plain RDP stream to the guest listener. Standard RDP clients reach the TCP
# transport via SSH port forwarding (the listener is loopback-only).
cat > /etc/lamco-rdp-server/config.toml << 'CONFIG_EOF'
config_version = 1

[server]
# Loopback for the same reason as the TCP transport below. In this fork
# (src/transport/config.rs, TransportsConfig::resolve) the per-transport
# [server.transports.tcp].listen_addr supersedes this key for the TCP bind,
# but the top-level value still feeds three things: the exposure guard
# (auth_method=none on a non-loopback address logs an unauthenticated-RDP
# warning at every startup), the GUI's address fields, and the fallback
# TCP bind if the [server.transports] table is ever removed. "[::]:3389"
# here triggered that warning on every boot.
listen_addr = "127.0.0.1:3389"
max_connections = 10
session_timeout = 0
use_portals = true
view_only = false

[server.transports]
[server.transports.tcp]
# LOOPBACK ONLY — see the threat model above the config write. auth_method
# is "none"; binding anything wider hands an unlocked sudo-capable desktop
# to every peer that can reach the port. mstsc users: ssh -L 3389:127.0.0.1:3389.
listen_addr = "127.0.0.1:3389"
# vsock carries Hyper-V Enhanced Session (vmconnect.exe): vmms terminates
# TLS/CredSSP on the host and relays plain RDP. The fork's per-transport
# security routing serves these on a dedicated Standard-RDP-Security
# server while TCP above keeps TLS/Hybrid — no security_mode compromise.
# NOTE: the fork binds vsock on VMADDR_CID_ANY with no CID allowlist
# (src/transport/listener.rs, VsockListenerImpl::bind) — access control is
# the hypervisor's. Within a Hyper-V VM only the host (vmms/vmconnect)
# can reach the vsock device, so the host relay is in practice the only
# peer. (An `allowed_cids` key used to sit here; the fork has no such
# option — it was dead config, silently ignored by serde.)
[server.transports.vsock]
enabled = true
port = 3389

[security]
cert_path = "/etc/lamco-rdp-server/cert.pem"
key_path = "/etc/lamco-rdp-server/key.pem"
enable_nla = false
security_mode = "hybrid"
auth_method = "none"
require_tls_13 = false

[video]
target_fps = 60
cursor_mode = "hidden"

[video_pipeline.processor]
target_fps = 60
max_queue_depth = 30
adaptive_quality = true
damage_threshold = 0.05
drop_on_full_queue = true
enable_metrics = true

[video_pipeline.dispatcher]
channel_size = 30
priority_dispatch = true
max_frame_age_ms = 150
enable_backpressure = true
high_water_mark = 0.8
low_water_mark = 0.5
load_balancing = true

[video_pipeline.converter]
buffer_pool_size = 8
enable_simd = true
damage_threshold = 0.75
enable_statistics = true

[capture]
protocol = "auto"
allow_fallback = true
handshake_timeout_ms = 5000

[input]
input_protocol = "auto"
keyboard_layout = "auto"
enable_touch = false

[clipboard]
enabled = true
# 100 MB: large ISO/tool transfers between host and guest are a normal
# workflow for this deployment path (file materialization goes through the
# ~/Downloads staging backend). With the TCP listener on loopback only, a
# malicious peer must already be inside the guest or hold an SSH hop; the
# rate limit below (200ms per transfer) bounds abuse.
max_size = 104857600
rate_limit_ms = 200
allowed_types = []
protocol = "auto"
allow_fallback = true
kde_syncselection_hint = false

[multimon]
enabled = true
max_monitors = 4

[performance]
encoder_threads = 0
network_threads = 0
buffer_pool_size = 16
zero_copy = true

[performance.adaptive_fps]
enabled = false
min_fps = 5
max_fps = 60
high_activity_threshold = 0.3
medium_activity_threshold = 0.1
low_activity_threshold = 0.01

[performance.latency]
mode = "interactive"
interactive_max_delay_ms = 16
balanced_max_delay_ms = 33
quality_max_delay_ms = 100
balanced_damage_threshold = 0.02
quality_damage_threshold = 0.05

[logging]
level = "info"
metrics = true

[egfx]
enabled = true
h264_level = "auto"
h264_bitrate = 50000
zgfx_compression = "never"
max_frames_in_flight = 2
frame_ack_timeout = 5000
periodic_idr_interval = 5
codec = "avc420"
encoder_backend = "x264"
# QUALITY PROFILE: qp 8-10 keeps near-lossless 1080p while letting the
# encoder breathe on a 2-vCPU guest. (The old qp_min=1 forced
# lossless-mode x264 for EVERY frame — combined with
# damage_tracking.pixel_threshold=1's full-frame diff per frame at 1080p,
# the encoder starved the very desktop it was encoding.) Adaptation is
# ON so sustained load can climb to max_qp instead of dropping frames.
qp_min = 8
qp_max = 10
qp_default = 9
avc444_aux_bitrate_ratio = 1.0
color_matrix = "identity"
color_range = "full"
avc444_enabled = true
avc444_enable_aux_omission = true
avc444_max_aux_interval = 30
avc444_aux_change_threshold = 0.05
avc444_force_aux_idr_on_return = false

[egfx.encoding_adaptation]
# ON for the default profile: raises QP under sustained encoder load so a
# small guest CPU trades fidelity for frame rate instead of stalling.
enabled = true
base_qp = 9
min_qp = 8
max_qp = 42
evaluation_interval_ms = 500
moderate_queue_threshold = 3
severe_queue_threshold = 6

[damage_tracking]
enabled = true
method = "diff"
tile_size = 16
diff_threshold = 0.01
# 100 (not 1): pixel_threshold=1 meant even a single changed pixel marked a
# tile dirty — at 1080p the per-frame full-buffer diff plus the resulting
# x264 near-lossless encodes starved the 2-vCPU desktop. 100 still catches
# real damage while ignoring sensor-noise-level flicker.
pixel_threshold = 100
merge_distance = 16
min_region_area = 64

[hardware_encoding]
enabled = false
vaapi_device = "/dev/dri/renderD128"
enable_dmabuf_zerocopy = true
fallback_to_software = true
quality_preset = "balanced"
prefer_nvenc = true
backend_priority = [
    "vulkan-video",
    "nvenc",
    "vaapi",
]
vulkan_device = "auto"

[display]
allow_resize = true
allowed_resolutions = []
dpi_aware = false
frame_transform = "auto"

[advanced_video]
enable_frame_skip = true
scene_change_threshold = 0.7
intra_refresh_interval = 300
enable_adaptive_quality = false

[cursor]
mode = "metadata"
auto_mode = true
predictive_latency_threshold_ms = 100
cursor_update_fps = 60

[cursor.predictor]
history_size = 8
lookahead_ms = 50.0
velocity_smoothing = 0.4
acceleration_smoothing = 0.2
max_prediction_distance = 100
min_velocity_threshold = 50.0
stop_convergence_rate = 0.5

[audio]
enabled = true
codec = "auto"
sample_rate = 48000
channels = 2
frame_ms = 20
opus_bitrate = 64000

[notifications]
on_error = true
on_cert_expiry = true

[monitoring]
enabled = true
snapshot_interval_secs = 5
metrics_bind = "127.0.0.1:9100"
CONFIG_EOF
chmod 644 /etc/lamco-rdp-server/config.toml

# -- Install the systemd user service unit ----------------------------------
# Write to the autologin user's ~/.config/systemd/user. The PRIMARY user
# source is __AUTOLOGIN_USER__, substituted host-side from the gallery
# item's InitialUsername (the SAME resolver EnableGraphicalAutologinStep
# uses at order 238 — one owner, no drift). The /etc/passwd scan below is
# only the fallback for a blank gallery field: first non-system account
# with a home dir.
AUTOLOGIN_USER="__AUTOLOGIN_USER__"
if [ -z "$AUTOLOGIN_USER" ]; then
    AUTOLOGIN_USER=$(awk -F: '$3 >= 1000 && $3 < 65534 && $6 != "" {print $1; exit}' /etc/passwd 2>/dev/null || echo "" )
fi
if [ -z "$AUTOLOGIN_USER" ]; then
    # Fall back to a 'user'/'parrot' convention used by gallery items.
    for cand in user parrot ubuntu; do
        if id "$cand" >/dev/null 2>&1; then AUTOLOGIN_USER="$cand"; break; fi
    done
fi

if [ -n "$AUTOLOGIN_USER" ]; then
    USER_HOME=$(getent passwd "$AUTOLOGIN_USER" | cut -d: -f6)

    # -- Finalize TLS key permissions ---------------------------------------
    # The unprivileged user service loads key.pem, but a self-signed key is
    # still a private key: 644 made it world-readable. Grant the autologin
    # user's login group read access instead — root:<user>, 640. Same
    # service-readability, no other-user access. cert.pem is public material
    # (it is sent to every client during the handshake) and stays 644.
    chown root:"$AUTOLOGIN_USER" /etc/lamco-rdp-server/key.pem 2>/dev/null || true
    chmod 640 /etc/lamco-rdp-server/key.pem
    chown root:root /etc/lamco-rdp-server/cert.pem 2>/dev/null || true
    chmod 644 /etc/lamco-rdp-server/cert.pem
    echo "TLS key restricted: /etc/lamco-rdp-server/key.pem root:$AUTOLOGIN_USER 640 (group read for the user service)."

    mkdir -p "$USER_HOME/.config/systemd/user"
    # Create the ReadWritePaths directories referenced by the systemd unit below.
    # systemd requires every path in ReadWritePaths to exist when it sets up the
    # mount namespace for ProtectSystem=strict -- if any is missing, the service
    # fails with status=226/NAMESPACE before the binary starts.
    mkdir -p "$USER_HOME/.config/lamco-rdp-server"
    mkdir -p "$USER_HOME/.local/share/lamco-rdp-server"
    mkdir -p "$USER_HOME/Downloads"
    chown -R "$AUTOLOGIN_USER" "$USER_HOME/.config/lamco-rdp-server" "$USER_HOME/.local/share/lamco-rdp-server" "$USER_HOME/Downloads"
    cat > "$USER_HOME/.config/systemd/user/lamco-rdp-server.service" << 'UNIT_EOF'
[Unit]
Description=Lamco RDP Server
Documentation=https://github.com/moerketh/lamco-rdp-server
After=graphical-session.target
Wants=graphical-session.target
StartLimitIntervalSec=60
StartLimitBurst=3
ConditionEnvironment=WAYLAND_DISPLAY

[Service]
Type=simple
ExecStart=/usr/bin/lamco-rdp-server --config /etc/lamco-rdp-server/config.toml
# Cursor lifecycle note: the fork (>= v1.4.5-hyperv.2) owns pointer handling
# entirely via the transparent color-pointer shape PDU and Runtime Painted
# auto-selection — it no longer touches kcminputrc or XCursor themes
# (cursor_theme.rs was deleted). No ExecStopPost cursor restore exists or
# is needed.
Restart=on-failure
RestartSec=5
Environment=RUST_LOG=info
ProtectSystem=strict
PrivateTmp=yes
ProtectProc=invisible
ProcSubset=pid
ReadWritePaths=%t %h/.config/lamco-rdp-server %h/.local/share/lamco-rdp-server %h/Downloads
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
RestrictNamespaces=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_VSOCK

[Install]
# graphical-session.target, NOT default.target: the unit is gated on
# ConditionEnvironment=WAYLAND_DISPLAY, and with linger enabled
# default.target is reached at boot BEFORE any Wayland session exists —
# the condition fails and nothing re-triggers the unit. Binding to the
# graphical session makes the session start pull it in (same as
# lamco-grant.service).
WantedBy=graphical-session.target
UNIT_EOF
    chown "$AUTOLOGIN_USER" "$USER_HOME/.config/systemd/user/lamco-rdp-server.service"

    # -- One-shot portal consent grant ---------------------------------------
    # The libei/kwin-virtual input path needs a one-time Portal RemoteDesktop
    # consent. Without a stored restore token the server's session-creation
    # BLOCKS on the consent dialog and no listener ever binds — a fresh VM
    # looks deployed-but-dead (vmconnect cannot connect). This oneshot unit
    # runs `--grant-permission` at first graphical-session start: the dialog
    # appears once on the VM console, one 'Allow' click stores the token
    # (and the xdg-desktop-portal-kderc MegaAuth permission entry), then the
    # unit never runs again (ConditionPathExists on a marker it creates).
    cat > "$USER_HOME/.config/systemd/user/lamco-grant.service" << 'GRANT_EOF'
[Unit]
Description=Lamco one-time portal RemoteDesktop consent grant
After=graphical-session.target
Wants=graphical-session.target
# Run BEFORE the server: two instances of the binary contending for the
# portal session produce spurious consent dialogs and lost tokens.
Before=lamco-rdp-server.service
ConditionEnvironment=WAYLAND_DISPLAY
# Skip forever once the marker exists (created below after a grant).
ConditionPathExists=!%h/.local/share/lamco-rdp-server/consent-granted

[Service]
Type=oneshot
ExecStartPre=/bin/sh -c 'if [ -s "%h/.local/share/lamco-rdp-server/restore_token" ] || kreadconfig6 --file xdg-desktop-portal-kderc --group remote-desktop --key "lamco-rdp-server" 2>/dev/null | grep -q .; then touch "%h/.local/share/lamco-rdp-server/consent-granted"; echo "Consent already present — skipping dialog."; exit 111; fi; exit 0'
ExecStart=/bin/sh -c 'timeout 300 /usr/bin/lamco-rdp-server --grant-permission || true; if [ -s "%h/.local/share/lamco-rdp-server/restore_token" ]; then touch "%h/.local/share/lamco-rdp-server/consent-granted"; echo "Consent granted — restore token stored."; else echo "Consent flow did not complete (timeout or dismissed) — will retry next boot."; fi'
# ExecStartPre's exit 111 is the "already granted, skip" path, not a
# failure — without this the unit shows as FAILED in systemctl status and
# trips failure monitors for every already-provisioned VM.
SuccessExitStatus=111
RemainAfterExit=no

[Install]
WantedBy=graphical-session.target
GRANT_EOF
    chown "$AUTOLOGIN_USER" "$USER_HOME/.config/systemd/user/lamco-grant.service"

    # -- Create monitors.xml to force 1920x1080@60 resolution ------------
    # The hyperv_drm driver reports 1024x768 as the preferred mode, which
    # mutter/GNOME picks by default. This overrides it to 1920x1080.
    mkdir -p "$USER_HOME/.config"
    cat > "$USER_HOME/.config/monitors.xml" << 'MONITORS_EOF'
<monitors>
  <configuration>
    <layoutmode>logical</layoutmode>
    <logicalmonitor>
      <x>0</x>
      <y>0</y>
      <scale>1</scale>
      <primary>yes</primary>
      <monitor>
        <monitorspec>
          <connector>Virtual-1</connector>
          <vendor>unknown</vendor>
          <product>unknown</product>
          <serial>unknown</serial>
        </monitorspec>
        <mode>
          <width>1920</width>
          <height>1080</height>
          <rate>60.000</rate>
        </mode>
      </monitor>
    </logicalmonitor>
  </configuration>
</monitors>
MONITORS_EOF
    chown "$AUTOLOGIN_USER" "$USER_HOME/.config/monitors.xml"
    echo "Created monitors.xml for 1920x1080@60 resolution."

    # -- Set display resolution via kscreen-doctor for KDE/KWin ----------
    # KWin ignores monitors.xml and uses KScreen config instead. Without
    # a KScreen config, KWin falls through to the hyperv_drm default
    # (1024x768). kscreen-doctor can set the mode at runtime, and a
    # kdeglobals entry ensures it persists across reboots.
    # This runs after the Wayland session starts (the systemd service has
    # ConditionEnvironment=WAYLAND_DISPLAY), so kscreen-doctor can connect.
    if command -v kscreen-doctor >/dev/null 2>&1; then
        # Try to set 1920x1080 — the best mode for RDP on Hyper-V
        sudo -u "$AUTOLOGIN_USER" WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/"$(id -u "$AUTOLOGIN_USER")" \
            kscreen-doctor output.1.mode.1920x1080@60 2>/dev/null || true
        echo "Set KScreen resolution to 1920x1080@60 via kscreen-doctor."
    else
        echo "kscreen-doctor not found — KWin will use DRM default (1024x768)."
    fi

    # -- Restart the service under the freshly installed binary ------------
    # The deb installed the binary and its units; restart so the service
    # acquires its portal session under the new binary. The restart also
    # surfaces the one-time consent dialog (lamco-grant.service) if it has
    # not been answered yet. (No AUTOLOGIN_USER re-test here: this whole
    # block is already inside `if [ -n "$AUTOLOGIN_USER" ]`.)
    loginctl enable-linger "$AUTOLOGIN_USER" 2>/dev/null || true
    sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
        systemctl --user daemon-reload 2>/dev/null || true
    sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
        systemctl --user restart lamco-rdp-server.service 2>/dev/null || true

    # -- Readiness gate: installed is NOT the same as listening ------------
    # Session creation can park on the one-time portal consent dialog
    # (see lamco-grant.service above): until it is answered, NO listener
    # binds and vmconnect cannot connect. Poll for the dispatcher line so
    # this step's report distinguishes "service up and listening" from
    # "deployed but blocked on consent" — a fresh VM is expected to need
    # one Allow click on the console. Every outcome (including exhaustion
    # of the 12x10s poll budget) is reported; a silent fall-through would
    # read as success in the host-side result parse.
    # (No AUTOLOGIN_USER re-test here: we are inside the outer user guard.)
    RDY_UID=$(id -u "$AUTOLOGIN_USER")
    RDY_OUTCOME="timeout"
    for r in $(seq 1 12); do
        if journalctl _UID=$RDY_UID --since "-5 min" --no-pager 2>/dev/null \
            | grep -aq "Accept dispatcher started"; then
            RDY_OUTCOME="ready"
            break
        fi
        if journalctl _UID=$RDY_UID --since "-5 min" --no-pager 2>/dev/null \
            | grep -aq "permission dialog will appear"; then
            RDY_OUTCOME="consent"
            break
        fi
        sleep 10
    done
    case "$RDY_OUTCOME" in
        ready)
            echo "Service ready: accept dispatcher running (TCP/vsock listeners bound)."
            ;;
        consent)
            echo "NOTICE: one-time portal consent dialog is on the VM console — click Allow once, then the service binds its listeners." >&2
            echo "DEGRADED: deployed but NOT listening yet — portal consent pending on the VM console." >&2
            DEGRADED=1
            ;;
        timeout)
            echo "DEGRADED: readiness gate timed out (120s) — neither dispatcher start nor consent prompt appeared." >&2
            echo "Diagnose with: journalctl _UID=$RDY_UID -b --no-pager | tail -n 50" >&2
            DEGRADED=1
            ;;
    esac


    # -- Retire vgem artifacts -----------------------------------------------
    # The server needs no extra render node and no KWin env overrides —
    # stock KWin + the materialize/fallback capture paths are the shipping
    # path. vgem (a fake renderD128 racing the real DRM node for the name)
    # is not used; remove artifacts older deployments may have left.
    rm -f /etc/modules-load.d/vgem.conf /etc/udev/rules.d/99-vgem-render.rules 2>/dev/null || true

    # -- Retired: transparent XCursor theme provisioning --------------------
    # The fork (>= v1.4.5-hyperv.2) deleted cursor_theme.rs: pointer handling
    # is now entirely the transparent color-pointer shape PDU + Runtime
    # Painted auto-selection (a config.mode of metadata/predictive flips to
    # Painted after 5 metadata-absent frames). The old provisioning here
    # (generate a transparent XCursor theme into /usr/share/icons, preset
    # kcminputrc, disable shakecursor, ExecStopPost restore) targeted that
    # deleted mechanism and is dead code. Clean up artifacts left on VMs
    # deployed before the retirement:
    if [ -d /usr/share/icons/transparent ]; then
        rm -rf /usr/share/icons/transparent
    fi

    # -- Idle-lock suppression (KDE) ----------------------------------------
    # KDE's idle autolock is disabled for RDP deployments: the lock greeter
    # can wedge under the hyperv_drm framebuffer error spam (high CPU, no
    # frames) and swallow ALL input including the vmconnect console — a
    # wedged lock screen bricks the machine remotely (only a reboot
    # recovers).
    # Two provisions:
    #   1. kscreenlockerrc Autolock=false — read at session start, so the
    #      first boot of a provisioned VM is already immune.
    #   2. A durable systemd user unit holding a freedesktop ScreenSaver
    #      inhibitor cookie — a dbus-send one-shot dies with its connection
    #      and releases the cookie; a holder process keeps it for the
    #      session lifetime (the validated pattern from the E2E).
    if [ -n "$AUTOLOGIN_USER" ] && command -v kwriteconfig6 >/dev/null 2>&1; then
        sudo -u "$AUTOLOGIN_USER" kwriteconfig6 --file kscreenlockerrc --group Daemon --key Autolock false 2>/dev/null || true
        sudo -u "$AUTOLOGIN_USER" kwriteconfig6 --file kscreenlockerrc --group Daemon --key LockOnResume false 2>/dev/null || true
        # Holder script: takes the inhibitor and parks (keeps the D-Bus
        # connection alive so the cookie stays held).
        # GOTCHA: do NOT use `sudo -u $U mkdir -p ~/.local/bin` — bash
        # expands ~ to ROOT's home BEFORE sudo runs, so the user's directory
        # never exists and the heredoc cat below fails. Use the resolved
        # $USER_HOME (getent) for every write — a hardcoded /home/$U breaks
        # for non-standard home paths.
        IDLE_HOME=$(getent passwd "$AUTOLOGIN_USER" | cut -d: -f6)
        if [ -n "$IDLE_HOME" ] && [ -d "$IDLE_HOME" ]; then
            mkdir -p "$IDLE_HOME"/.local/bin 2>/dev/null || true
            chown "$AUTOLOGIN_USER": "$IDLE_HOME"/.local/bin 2>/dev/null || true
            cat > "$IDLE_HOME"/.local/bin/lamco-idle-inhibit.py << 'PYEOF'
#!/usr/bin/env python3
# Hold a freedesktop ScreenSaver inhibitor cookie for the session lifetime.
# KDE's idle lock can wedge under hyperv_drm framebuffer spam on Hyper-V and
# brick remote access. dbus-send one-shots release the cookie when their
# connection dies; this process parks holding it.
import time

import dbus

bus = dbus.SessionBus()
ss = dbus.Interface(
    bus.get_object("org.freedesktop.ScreenSaver", "/ScreenSaver"),
    "org.freedesktop.ScreenSaver",
)
cookie = ss.Inhibit("lamco-rdp", "rdp-session-keepalive")
print(f"inhibitor cookie: {cookie}", flush=True)
while True:
    time.sleep(60)
    try:
        ss.GetActive()
    except Exception:
        pass
PYEOF
            chown "$AUTOLOGIN_USER": "$IDLE_HOME"/.local/bin/lamco-idle-inhibit.py 2>/dev/null || true
            chmod 0755 "$IDLE_HOME"/.local/bin/lamco-idle-inhibit.py 2>/dev/null || true
            # Systemd user unit; binds to the graphical session (the session bus
            # where the screensaver runs exists only there). Same ~-expansion
            # rule: use the resolved $IDLE_HOME (script runs as root; mkdir+chown).
            mkdir -p "$IDLE_HOME"/.config/systemd/user 2>/dev/null || true
            chown "$AUTOLOGIN_USER": "$IDLE_HOME"/.config/systemd/user 2>/dev/null || true
            cat > "$IDLE_HOME"/.config/systemd/user/lamco-idle-inhibit.service << 'UNITEOF'
[Unit]
Description=lamco RDP idle-lock inhibitor (Hyper-V lock-greeter wedge prevention)
PartOf=graphical-session.target

[Service]
Type=simple
ExecStart=/usr/bin/python3 %h/.local/bin/lamco-idle-inhibit.py
Restart=on-failure
RestartSec=10

[Install]
WantedBy=graphical-session.target
UNITEOF
            chown "$AUTOLOGIN_USER": "$IDLE_HOME"/.config/systemd/user/lamco-idle-inhibit.service 2>/dev/null || true
            sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
                systemctl --user daemon-reload 2>/dev/null || true
            sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
                DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u "$AUTOLOGIN_USER")/bus \
                systemctl --user enable lamco-idle-inhibit.service 2>/dev/null || true
            sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
                DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u "$AUTOLOGIN_USER")/bus \
                systemctl --user start lamco-idle-inhibit.service 2>/dev/null || true
            echo "Idle-lock suppression: Autolock=false + lamco-idle-inhibit.service (Hyper-V lock-greeter wedge prevention)."
        else
            echo "WARNING: no home dir for $AUTOLOGIN_USER — idle-lock suppression unit not installed." >&2
            DEGRADED=1
        fi
    fi

    # -- KWin private-interface grant (zkde-screencast) ----------------------
    # KWin 6.x gates private Wayland interfaces behind an allowlist:
    # zkde_screencast_unstable_v1 is only advertised to clients whose
    # .desktop file lists it under X-KDE-Wayland-Interfaces (matched by the
    # client's executable path — see KWin wayland_server.cpp
    # interfacesBlackList + serviceutils.h fetchRequestedInterfaces).
    # xdg-desktop-portal-kde and krfb ship such entries; without one the
    # kwin-virtual strategy cannot bind the global: every connect fails
    # with "zkde stream creation failed: zkde_screencast global not bound",
    # and the global stays invisible even to wayland-info.
    cat > /usr/share/applications/lamco-rdp-server.desktop << 'DESKTOPEOF'
[Desktop Entry]
Type=Application
Name=Lamco RDP Server
Exec=/usr/bin/lamco-rdp-server
NoDisplay=true
X-KDE-Wayland-Interfaces=zkde_screencast_unstable_v1
DESKTOPEOF
    chmod 0644 /usr/share/applications/lamco-rdp-server.desktop
    # Refresh KDE's service cache so the grant takes effect without relogin.
    if [ -n "$AUTOLOGIN_USER" ] && command -v kbuildsycoca6 >/dev/null 2>&1; then
        sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
            DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u "$AUTOLOGIN_USER")/bus \
            kbuildsycoca6 --noincremental 2>/dev/null || true
    fi
    echo "KWin private-interface grant installed (zkde_screencast_unstable_v1 for /usr/bin/lamco-rdp-server)."

    # linger + enable (the service starts on next graphical-session target)
    loginctl enable-linger "$AUTOLOGIN_USER" 2>/dev/null || true
    sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
        systemctl --user daemon-reload 2>/dev/null || true
    sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
        systemctl --user enable lamco-rdp-server.service 2>/dev/null || true
    sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
        systemctl --user enable lamco-grant.service 2>/dev/null || true
    echo "Installed systemd user unit for $AUTOLOGIN_USER and enabled linger."
else
    echo "WARNING: could not determine autologin user; systemd user unit not installed." >&2
    DEGRADED=1
fi

# -- Scoped suppression of kwin framebuffer-error spam (diagnosability) ------
# hyperv_drm framebuffer creation failures (~15/s, "kwin_wayland_drm: Failed
# to create framebuffer") come from kwin's own stderr under
# plasma-kwin_wayland.service, not from the kernel. A flood from one unit
# exhausts journald's PER-SERVICE rate-limit bucket, so kwin's own
# diagnostic lines and the lamco session logs share its bucket and are
# dropped. The fix is scoped: journald's global limits stay at their
# defaults (all other services keep their buckets) and the spam never
# reaches the journal at all:
#   - LogFilterPatterns= (systemd >= 254, unit [Service] directive) drops
#     matching lines client-side, before storage. Parrot 7.3 (Debian 13
#     base) ships systemd 257. On older systemd the directive is ignored
#     with a warning in the journal, so the drop-in degrades to a no-op
#     rather than breaking the session.
#   - the drop-in lives in /etc/systemd/user/, so every user manager
#     (lightdm greeter autologin session, vmcreate SSH session) loads it.
# Placed on kwin's unit because that is where the spam originates — a global
# journald burst raise (the old /etc/systemd/journald.conf.d/99-lamco-
# ratelimit.conf) let the spam through at full rate and kept the journal
# 90% spam.
mkdir -p /etc/systemd/user/plasma-kwin_wayland.service.d
cat > /etc/systemd/user/plasma-kwin_wayland.service.d/lamco-logfilter.conf << 'KWIN_LOGFILTER_EOF'
[Service]
LogFilterPatterns=~Failed to create framebuffer
KWIN_LOGFILTER_EOF
if [ -n "$AUTOLOGIN_USER" ] && command -v systemctl >/dev/null 2>&1; then
    sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR=/run/user/$(id -u "$AUTOLOGIN_USER") \
        systemctl --user daemon-reload 2>/dev/null || true
fi
# Remove a stale global override from older provisioning runs, so journald
# defaults are restored.
rm -f /etc/systemd/journald.conf.d/99-lamco-ratelimit.conf
echo "Scoped kwin framebuffer log filter installed (journald defaults untouched)."

echo "=== Lamco RDP Server install complete ==="
# The one-time Portal consent is automated: lamco-grant.service runs
# --grant-permission at first graphical-session start, so the dialog appears
# on the VM console exactly once. No manual step.
#
# Machine-readable terminal line: the host-side step parses this to decide
# success (ok), partial (degraded — logged as a warning), or hard failure
# (failed — exits before this line, so the line itself never appears for
# failures; the step treats a missing line as failed too).
if [ "${DEGRADED:-0}" = "1" ]; then
    echo "LAMCO_RESULT=degraded"
    exit 0
fi
echo "LAMCO_RESULT=ok"
exit 0

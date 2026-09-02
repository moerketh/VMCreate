using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Installs and configures the Lamco RDP Server (Wayland-native RDP) on the
    /// guest VM via native deb/rpm packages from GitHub Releases.
    /// <para>
    /// Unlike xrdp (which installs via the external cloning-ISO chroot), Lamco
    /// installs post-boot over SSH. It is a Wayland-native server built on IronRDP
    /// with XDG Desktop Portal + PipeWire screen capture, so Wayland is kept
    /// enabled (the <see cref="ForceX11Step"/>/<see cref="DisableWaylandSessionsStep"/>
    /// steps are skipped via <see cref="VmCustomizations.RdpBackend"/> gating).
    /// </para>
    /// <para>
    /// This step downloads the matching release asset for the detected distro
    /// (deb for Debian/Ubuntu/Parrot, rpm for Fedora, rpm for openSUSE) from
    /// <c>github.com/lamco-admin/lamco-rdp-server/releases</c>, installs it via
    /// the native package manager, installs Portal/PipeWire runtime deps, generates
    /// TLS certificates, writes <c>/etc/lamco-rdp-server/config.toml</c> with PAM
    /// auth, installs the systemd <b>user</b> service unit, and enables linger
    /// so the user service starts at boot. The one-time Portal permission grant
    /// (<c>--grant-permission</c>) is an interactive GUI dialog and is left as a
    /// manual post-deploy step — <see cref="EnableGraphicalAutologinStep"/>
    /// ensures a Wayland session exists for the user to grant into.
    /// </para>
    /// <para>
    /// PoC gating: only runs when <see cref="VmCustomizations.RdpBackend"/> is
    /// <see cref="RdpBackend.Lamco"/> and the gallery item's
    /// <see cref="GalleryItem.LinuxDistro"/> is one of the supported distributions
    /// (Ubuntu, Fedora, Debian, openSUSE, Parrot). <see cref="DistroDetector"/>
    /// re-verifies at runtime as a defensive check.
    /// </para>
    /// <para>
    /// Runs at Order 235, before <see cref="EnableGraphicalAutologinStep"/> (238)
    /// and before the xrdp block (240-270, skipped for Lamco).
    /// </para>
    /// </summary>
    public class InstallLamcoRdpStep : ICustomizationStep
    {
        public string Name => "Install Lamco RDP Server";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 235;
        public string? ProgressPhaseId => "Sub_InstallLamcoRdp";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.RdpBackend == RdpBackend.Lamco && item.SupportsLamco();

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Installing Lamco RDP Server on VM {VMName}", shell.VmName);

            string script = InstallScript.Replace("\r\n", "\n");
            await shell.CopyContentAsync(script, "/tmp/install_lamco.sh", ct);

            // The install pulls apt packages, downloads the upstream deb and
            // polls the detached fork build — far beyond the transport's
            // default command timeout. Allow 20 minutes.
            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/install_lamco.sh && sudo rm -f /tmp/install_lamco.sh",
                TimeSpan.FromMinutes(20), ct);

            logger.LogInformation("Lamco RDP Server install result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }

        // NOTE: The release tag is resolved at runtime via the GitHub API
        // (/releases/latest). If the VM has no network access or the API is
        // unreachable, the script falls back to the hardcoded LAMCO_FALLBACK_TAG
        // below and constructs the asset URL from the template. Update this
        // constant when bumping the pinned fallback version.
        private const string InstallScript = @"#!/bin/bash
set -o pipefail

# =========================================================================
# Lamco RDP Server — install + configure (Wayland-native)
# =========================================================================
# Downloads the matching deb/rpm from GitHub Releases, installs it, sets up
# TLS certs, config.toml (PAM auth), the systemd user unit, and linger.
# The one-time --grant-permission Portal dialog is NOT run here (interactive).

LAMCO_REPO=""lamco-admin/lamco-rdp-server""
LAMCO_FALLBACK_TAG=""v1.4.4""
API_URL=""https://api.github.com/repos/${LAMCO_REPO}/releases/latest""

echo ""=== Lamco RDP Server install ===""

# -- Detect distro from /etc/os-release ------------------------------------
if [ ! -f /etc/os-release ]; then
    echo ""ERROR: /etc/os-release not found — cannot determine distro."" >&2
    exit 1
fi
. /etc/os-release
DISTRO_ID=""${ID}""
DISTRO_LIKE=""${ID_LIKE:-}""

download_tool=""""
if command -v curl >/dev/null 2>&1; then
    download_tool=""curl""
elif command -v wget >/dev/null 2>&1; then
    download_tool=""wget""
else
    echo ""ERROR: neither curl nor wget is available."" >&2
    exit 1
fi

fetch_url() {
    # $1 = URL, $2 = output file
    if [ ""$download_tool"" = ""curl"" ]; then
        curl -fsSL ""$1"" -o ""$2""
    else
        wget -q ""$1"" -O ""$2""
    fi
}

fetch_text() {
    # $1 = URL, prints body to stdout
    if [ ""$download_tool"" = ""curl"" ]; then
        curl -fsSL ""$1""
    else
        wget -q ""$1"" -O -
    fi
}

# -- Resolve the latest release tag + asset URLs ---------------------------
# Try the GitHub API first; fall back to a hardcoded tag if offline/API-limited.
# The API response lists the ACTUAL asset filenames (which include release-
# revision suffixes like -1, -4 and distro suffixes like fc42/fc43 that we
# cannot guess reliably), so we parse the assets array rather than templating.
RELEASE_TAG=""$LAMCO_FALLBACK_TAG""
ASSET_DEB=""""
ASSET_RPM_FC=""""
ASSET_RPM_SUSE=""""
ASSET_FLATPAK=""""

api_body=$(fetch_text ""$API_URL"" 2>/dev/null || true)
if [ -n ""$api_body"" ]; then
    parsed_tag=$(printf '%s' ""$api_body"" | sed -n 's/.*""tag_name""[[:space:]]*:[[:space:]]*""\([^""]*\)"".*/\1/p' | head -1)
    [ -n ""$parsed_tag"" ] && RELEASE_TAG=""$parsed_tag""
    echo ""Resolved latest Lamco release: $RELEASE_TAG (via GitHub API)""
    # Extract every browser_download_url from the assets array. Each line of
    # api_urls will be a full https://github.com/.../releases/download/<tag>/<filename>.
    api_urls=$(printf '%s' ""$api_body"" | sed -n 's/.*""browser_download_url""[[:space:]]*:[[:space:]]*""\([^""]*\)"".*/\1/p')
    # Pick the asset for each family by filename pattern.
    ASSET_DEB=$(printf '%s\n' ""$api_urls"" | grep -E '_amd64\.deb$' | head -1 || true)
    ASSET_RPM_FC=$(printf '%s\n' ""$api_urls"" | grep -E '\.fc[0-9]+\.x86_64\.rpm$' | head -1 || true)
    ASSET_RPM_SUSE=$(printf '%s\n' ""$api_urls"" | grep -E '\.suse[a-z0-9.-]*\.x86_64\.rpm$' | head -1 || true)
    ASSET_FLATPAK=$(printf '%s\n' ""$api_urls"" | grep -E '\.flatpak$' | head -1 || true)
else
    echo ""GitHub API unreachable — using fallback tag $RELEASE_TAG""
fi

DOWNLOAD_BASE=""https://github.com/${LAMCO_REPO}/releases/download/${RELEASE_TAG}""

# Determine arch (only x86_64 is shipped per the README matrix).
ARCH=""$(uname -m)""
case ""$ARCH"" in
    x86_64|amd64) ARCH=""x86_64"" ;;
    *) echo ""ERROR: unsupported arch $ARCH (only x86_64 packages are published)."" >&2; exit 1 ;;
esac

# -- Select the package for this distro -------------------------------------
# Debian family (ID=ubuntu/debian/parrot, or ID_LIKE contains debian/ubuntu)
is_debian_family() {
    case ""$DISTRO_ID"" in
        ubuntu|debian|parrot) return 0 ;;
    esac
    case "" $DISTRO_LIKE "" in
        *"" debian""*|*"" ubuntu""*) return 0 ;;
    esac
    return 1
}
is_fedora_family() {
    case ""$DISTRO_ID"" in
        fedora) return 0 ;;
    esac
    case "" $DISTRO_LIKE "" in
        *"" fedora""*|*"" rhel""*) return 0 ;;
    esac
    return 1
}
is_opensuse_family() {
    case ""$DISTRO_ID"" in
        opensuse-tumbleweed|opensuse-leap|opensuse|suse|sles) return 0 ;;
    esac
    case "" $DISTRO_LIKE "" in
        *"" opensuse""*|*"" suse""*) return 0 ;;
    esac
    return 1
}

# resolve_pkg_url: picks the best asset URL for this distro.
# Prefers a native package (deb/rpm) parsed from the API; falls back to a
# templated URL using the release tag; finally falls back to the Flatpak
# (universal, works on any distro with flatpak installed).
PKG_URL=""""
PKG_KIND=""""
if is_debian_family; then
    if [ -n ""$ASSET_DEB"" ]; then
        PKG_URL=""$ASSET_DEB""; PKG_KIND=""deb""
    else
        # Fallback template: try the common naming convention.
        PKG_URL=""${DOWNLOAD_BASE}/lamco-rdp-server_${RELEASE_TAG#v}-1_amd64.deb""; PKG_KIND=""deb""
    fi
elif is_fedora_family; then
    if [ -n ""$ASSET_RPM_FC"" ]; then
        PKG_URL=""$ASSET_RPM_FC""; PKG_KIND=""rpm""
    elif [ -n ""$ASSET_FLATPAK"" ]; then
        PKG_URL=""$ASSET_FLATPAK""; PKG_KIND=""flatpak""
    else
        echo ""ERROR: no Fedora rpm or Flatpak asset in release $RELEASE_TAG."" >&2; exit 1
    fi
elif is_opensuse_family; then
    if [ -n ""$ASSET_RPM_SUSE"" ]; then
        PKG_URL=""$ASSET_RPM_SUSE""; PKG_KIND=""rpm""
    elif [ -n ""$ASSET_FLATPAK"" ]; then
        PKG_URL=""$ASSET_FLATPAK""; PKG_KIND=""flatpak""
    else
        echo ""ERROR: no openSUSE rpm or Flatpak asset in release $RELEASE_TAG."" >&2; exit 1
    fi
else
    echo ""ERROR: distro $DISTRO_ID (likes='$DISTRO_LIKE') is not in the Lamco PoC supported set."" >&2
    exit 1
fi

echo ""Selected Lamco package: $PKG_URL (kind=$PKG_KIND)""

echo ""Downloading $PKG_URL""
case ""$PKG_KIND"" in
    deb)      TMP_PKG=""/tmp/lamco-rdp-server.deb"" ;;
    rpm)      TMP_PKG=""/tmp/lamco-rdp-server.rpm"" ;;
    flatpak)  TMP_PKG=""/tmp/lamco-rdp-server.flatpak"" ;;
esac
fetch_url ""$PKG_URL"" ""$TMP_PKG"" || { echo ""ERROR: download failed for $PKG_URL"" >&2; exit 1; }

# -- Install the package ----------------------------------------------------
case ""$PKG_KIND"" in
    deb)
        DEBIAN_FRONTEND=noninteractive apt-get update -y 2>&1 || true
        DEBIAN_FRONTEND=noninteractive apt-get install -y ""$TMP_PKG"" 2>&1
        ;;
    rpm)
        if command -v dnf >/dev/null 2>&1; then
            dnf install -y ""$TMP_PKG"" 2>&1
        elif command -v zypper >/dev/null 2>&1; then
            zypper --non-interactive install ""$TMP_PKG"" 2>&1
        elif command -v rpm >/dev/null 2>&1; then
            rpm -ivh ""$TMP_PKG"" 2>&1
        else
            echo ""ERROR: no dnf/zypper/rpm available to install the rpm."" >&2
            exit 1
        fi
        ;;
    flatpak)
        # Flatpak is the universal fallback when no native binary package exists.
        # Install flatpak if missing, then install the bundle.
        if command -v apt-get >/dev/null 2>&1; then
            DEBIAN_FRONTEND=noninteractive apt-get install -y flatpak 2>&1 || true
        elif command -v dnf >/dev/null 2>&1; then
            dnf install -y flatpak 2>&1 || true
        elif command -v zypper >/dev/null 2>&1; then
            zypper --non-interactive install flatpak 2>&1 || true
        fi
        flatpak install --user -y --bundle ""$TMP_PKG"" 2>&1
        # The Flatpak app ID is io.lamco.rdp-server per the INSTALL.md.
        # Note: Flatpak runs sandboxed; the systemd user unit below is for the
        # native binary and will be a no-op under Flatpak. The user runs the
        # Flatpak via 'flatpak run io.lamco.rdp-server' instead.
        ;;
esac
rm -f ""$TMP_PKG""

# -- Install Portal + PipeWire runtime deps if missing ----------------------
# Branch by detected desktop so we pull the correct portal backend.
desktop=""${XDG_CURRENT_DESKTOP:-}""
# If we are running over SSH XDG_CURRENT_DESKTOP may be empty; detect from the
# installed session files instead.
if [ -z ""$desktop"" ]; then
    if [ -f /usr/share/wayland-sessions/gnome.desktop ] || [ -f /usr/share/xsessions/gnome.desktop ] || [ -f /usr/share/wayland-sessions/mutter.desktop ]; then
        desktop=""GNOME""
    elif [ -f /usr/share/wayland-sessions/plasma.desktop ] || [ -f /usr/share/xsessions/plasma.desktop ]; then
        desktop=""KDE""
    fi
fi

if command -v apt-get >/dev/null 2>&1; then
    DEBIAN_FRONTEND=noninteractive apt-get install -y \
        pipewire wireplumber xdg-desktop-portal \
        $([ ""$desktop"" = ""GNOME"" ] && echo ""xdg-desktop-portal-gnome"") \
        $([ ""$desktop"" = ""KDE"" ] && echo ""xdg-desktop-portal-kde"") \
        2>&1 || true
elif command -v dnf >/dev/null 2>&1; then
    dnf install -y pipewire wireplumber xdg-desktop-portal \
        $([ ""$desktop"" = ""GNOME"" ] && echo ""xdg-desktop-portal-gnome"") \
        $([ ""$desktop"" = ""KDE"" ] && echo ""xdg-desktop-portal-kde"") \
        2>&1 || true
elif command -v zypper >/dev/null 2>&1; then
    zypper --non-interactive install pipewire wireplumber xdg-desktop-portal \
        $([ ""$desktop"" = ""GNOME"" ] && echo ""xdg-desktop-portal-gnome"") \
        $([ ""$desktop"" = ""KDE"" ] && echo ""xdg-desktop-portal-kde"") \
        2>&1 || true
fi

# -- Generate TLS certificates ---------------------------------------------
# The server requires cert.pem + key.pem to start. Try the shipped setup-certs
# helper first; if it is missing OR fails OR does not produce the files, fall
# back to openssl inline. Always verify the files exist at the end — a silent
# setup-certs failure must not leave the server unable to start.
mkdir -p /etc/lamco-rdp-server
if command -v lamco-rdp-server-setup-certs >/dev/null 2>&1; then
    lamco-rdp-server-setup-certs /etc/lamco-rdp-server ""$(hostname)"" 2>&1 || true
    # Permissions are finalized after the autologin user is resolved below
    # (key.pem gets root:<user> 640 — group read for the user service, never
    # world-readable).
fi
# If the helper did not produce both files (or was absent), generate them now.
if [ ! -f /etc/lamco-rdp-server/cert.pem ] || [ ! -f /etc/lamco-rdp-server/key.pem ]; then
    echo ""Generating self-signed TLS certificate via openssl...""
    openssl req -x509 -newkey rsa:4096 -nodes \
        -keyout /etc/lamco-rdp-server/key.pem \
        -out /etc/lamco-rdp-server/cert.pem \
        -days 365 -subj ""/CN=$(hostname)"" \
        -addext ""subjectAltName=DNS:$(hostname),DNS:localhost,IP:127.0.0.1"" 2>&1
    # Permissions are finalized after the autologin user is resolved below:
    # the key must be readable by the unprivileged user service, but that is
    # granted via group ownership (root:<user>, 640) instead of making the
    # private key world-readable.
fi
# Final guard: if certs are STILL missing, the server cannot start.
if [ ! -f /etc/lamco-rdp-server/cert.pem ] || [ ! -f /etc/lamco-rdp-server/key.pem ]; then
    echo ""ERROR: TLS certificate generation failed — cert.pem/key.pem not found."" >&2
    exit 1
fi
echo ""TLS certificates present: /etc/lamco-rdp-server/cert.pem""

# -- Write config.toml (full tuned profile, hybrid security, no auth) ------
# Verbatim port of the hand-tuned config from the reference test VM
# (TEST_20260817213920) — the profile with verified deep blacks, 60fps and
# a single client-side pointer. Key quality switches vs the naive minimum:
#   [egfx]: qp 1-10 + color_range=""full"" + color_matrix=""identity""  -> full
#           0-255 range (deep black, no washed-out 16-235), 50 Mbps x264.
#   [video]: target_fps=60 + cursor_mode=""hidden""; [cursor] metadata mode
#           with the predictor -> one zero-lag client-rendered pointer.
#   [performance]/[video_pipeline.*]: zero-copy + buffers + backpressure.
# security_mode=hybrid + auth_method=none: the proven combination for Lamco
# with standard RDP clients (mstsc, FreeRDP). In ""tls""-only mode mstsc
# negotiates standard RDP security without a TLS layer, and the TLS
# acceptor then rejects the stream (""corrupt message"" spam, client never
# connects). Hybrid lets the server accept the CredSSP-free standard path.
# No [security.credssp_credentials] — including it makes IronRDP require
# credentials even with auth_method=none. [gui_state]/[diagnostics] from
# the reference VM are GUI/debug state and stay out of the template.
#
# NOTE: Hyper-V Enhanced Session (vmconnect.exe with HvSocket/VMBus transport)
# does NOT work with Lamco/IronRDP because Hyper-V uses a proprietary pre-RDP
# greeting protocol that IronRDP does not implement. Users must connect via
# standard RDP (mstsc to VM-IP:3389) instead of Enhanced Session.
cat > /etc/lamco-rdp-server/config.toml << 'CONFIG_EOF'
config_version = 1

[server]
listen_addr = ""[::]:3389""
max_connections = 10
session_timeout = 0
use_portals = true
view_only = false

[server.transports]
[server.transports.tcp]
listen_addr = ""0.0.0.0:3389""
# vsock is opt-in since the fork's vsock-hardening commits: the transport is
# unauthenticated by construction (vmms authenticates on the host side), so
# the server will NOT enable it from the Hyper-V autodetect alone — it only
# logs a suggestion. This deployment is a single-user lab VM behind the
# Default Switch, so opt in explicitly; the built-in allowlist accepts only
# VMADDR_CID_HOST (2) and refuses the in-guest loopback CID (1) regardless.
[server.transports.vsock]
enabled = true
port = 3389

[security]
cert_path = ""/etc/lamco-rdp-server/cert.pem""
key_path = ""/etc/lamco-rdp-server/key.pem""
enable_nla = false
security_mode = ""hybrid""
auth_method = ""none""
require_tls_13 = false

[video]
target_fps = 60
cursor_mode = ""hidden""

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
protocol = ""auto""
allow_fallback = true
handshake_timeout_ms = 5000

[input]
input_protocol = ""auto""
keyboard_layout = ""auto""
enable_touch = false

[clipboard]
enabled = true
max_size = 10485760
rate_limit_ms = 200
allowed_types = []
protocol = ""auto""
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
mode = ""interactive""
interactive_max_delay_ms = 16
balanced_max_delay_ms = 33
quality_max_delay_ms = 100
balanced_damage_threshold = 0.02
quality_damage_threshold = 0.05

[logging]
level = ""info""
metrics = true

[egfx]
enabled = true
h264_level = ""auto""
h264_bitrate = 50000
zgfx_compression = ""never""
max_frames_in_flight = 2
frame_ack_timeout = 5000
periodic_idr_interval = 5
codec = ""avc420""
encoder_backend = ""x264""
qp_min = 1
qp_max = 10
qp_default = 1
avc444_aux_bitrate_ratio = 1.0
color_matrix = ""identity""
color_range = ""full""
avc444_enabled = true
avc444_enable_aux_omission = true
avc444_max_aux_interval = 30
avc444_aux_change_threshold = 0.05
avc444_force_aux_idr_on_return = false

[egfx.encoding_adaptation]
enabled = false
base_qp = 22
min_qp = 18
max_qp = 42
evaluation_interval_ms = 500
moderate_queue_threshold = 3
severe_queue_threshold = 6

[damage_tracking]
enabled = true
method = ""diff""
tile_size = 16
diff_threshold = 0.01
pixel_threshold = 1
merge_distance = 16
min_region_area = 64

[hardware_encoding]
enabled = false
vaapi_device = ""/dev/dri/renderD128""
enable_dmabuf_zerocopy = true
fallback_to_software = true
quality_preset = ""balanced""
prefer_nvenc = true
backend_priority = [
    ""vulkan-video"",
    ""nvenc"",
    ""vaapi"",
]
vulkan_device = ""auto""

[display]
allow_resize = true
allowed_resolutions = []
dpi_aware = false
frame_transform = ""auto""

[advanced_video]
enable_frame_skip = true
scene_change_threshold = 0.7
intra_refresh_interval = 300
enable_adaptive_quality = false

[cursor]
mode = ""metadata""
auto_mode = true
predictive_latency_threshold_ms = 0
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
codec = ""auto""
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
metrics_bind = ""127.0.0.1:9100""
CONFIG_EOF
chmod 644 /etc/lamco-rdp-server/config.toml

# -- Install the systemd user service unit ----------------------------------
# Write to the autologin user's ~/.config/systemd/user. The autologin user is
# resolved from /etc/passwd by selecting the first non-system account with a
# home dir that also has a graphical session. We rely on EnableGraphicalAutologinStep
# (runs next, order 238) to set the autologin user; here we install the unit for
# the most likely user. The unit is identical for all users.
AUTOLOGIN_USER=$(awk -F: '$3 >= 1000 && $3 < 65534 && $6 != """" {print $1; exit}' /etc/passwd 2>/dev/null || echo """" )
if [ -z ""$AUTOLOGIN_USER"" ]; then
    # Fall back to a 'user'/'parrot' convention used by gallery items.
    for cand in user parrot ubuntu; do
        if id ""$cand"" >/dev/null 2>&1; then AUTOLOGIN_USER=""$cand""; break; fi
    done
fi

if [ -n ""$AUTOLOGIN_USER"" ]; then
    USER_HOME=$(getent passwd ""$AUTOLOGIN_USER"" | cut -d: -f6)

    # -- Finalize TLS key permissions ---------------------------------------
    # The unprivileged user service loads key.pem, but a self-signed key is
    # still a private key: 644 made it world-readable. Grant the autologin
    # user's login group read access instead — root:<user>, 640. Same
    # service-readability, no other-user access. cert.pem is public material
    # (it is sent to every client during the handshake) and stays 644.
    chown root:""$AUTOLOGIN_USER"" /etc/lamco-rdp-server/key.pem 2>/dev/null || true
    chmod 640 /etc/lamco-rdp-server/key.pem
    chown root:root /etc/lamco-rdp-server/cert.pem 2>/dev/null || true
    chmod 644 /etc/lamco-rdp-server/cert.pem
    echo ""TLS key restricted: /etc/lamco-rdp-server/key.pem root:$AUTOLOGIN_USER 640 (group read for the user service).""

    mkdir -p ""$USER_HOME/.config/systemd/user""
    # Create the ReadWritePaths directories referenced by the systemd unit below.
    # systemd requires every path in ReadWritePaths to exist when it sets up the
    # mount namespace for ProtectSystem=strict -- if any is missing, the service
    # fails with status=226/NAMESPACE before the binary starts.
    mkdir -p ""$USER_HOME/.config/lamco-rdp-server""
    mkdir -p ""$USER_HOME/.local/share/lamco-rdp-server""
    mkdir -p ""$USER_HOME/Downloads""
    chown -R ""$AUTOLOGIN_USER"" ""$USER_HOME/.config/lamco-rdp-server"" ""$USER_HOME/.local/share/lamco-rdp-server"" ""$USER_HOME/Downloads""
    cat > ""$USER_HOME/.config/systemd/user/lamco-rdp-server.service"" << 'UNIT_EOF'
[Unit]
Description=Lamco RDP Server
Documentation=https://github.com/lamco-admin/lamco-rdp-server
After=graphical-session.target
Wants=graphical-session.target
StartLimitIntervalSec=60
StartLimitBurst=3
ConditionEnvironment=WAYLAND_DISPLAY

[Service]
Type=simple
ExecStart=/usr/bin/lamco-rdp-server --config /etc/lamco-rdp-server/config.toml
# Safety net: if the service dies while the guest cursor is transparent
# (RDP session active), restore the visible console cursor on the way
# down so the console is never left pointerless after a crash.
ExecStopPost=bash -c 'kwriteconfig6 --file kcminputrc --group Mouse --key cursorTheme breeze_cursors && DISPLAY=:0 WAYLAND_DISPLAY=wayland-0 plasma-apply-cursortheme breeze_cursors || true'
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
WantedBy=default.target
UNIT_EOF
    chown ""$AUTOLOGIN_USER"" ""$USER_HOME/.config/systemd/user/lamco-rdp-server.service""

    # -- Create monitors.xml to force 1920x1080@60 resolution ------------
    # The hyperv_drm driver reports 1024x768 as the preferred mode, which
    # mutter/GNOME picks by default. This overrides it to 1920x1080.
    mkdir -p ""$USER_HOME/.config""
    cat > ""$USER_HOME/.config/monitors.xml"" << 'MONITORS_EOF'
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
    chown ""$AUTOLOGIN_USER"" ""$USER_HOME/.config/monitors.xml""
    echo ""Created monitors.xml for 1920x1080@60 resolution.""

    # -- Set display resolution via kscreen-doctor for KDE/KWin ----------
    # KWin ignores monitors.xml and uses KScreen config instead. Without
    # a KScreen config, KWin falls through to the hyperv_drm default
    # (1024x768). kscreen-doctor can set the mode at runtime, and a
    # kdeglobals entry ensures it persists across reboots.
    # This runs after the Wayland session starts (the systemd service has
    # ConditionEnvironment=WAYLAND_DISPLAY), so kscreen-doctor can connect.
    if command -v kscreen-doctor >/dev/null 2>&1; then
        # Try to set 1920x1080 — the best mode for RDP on Hyper-V
        sudo -u ""$AUTOLOGIN_USER"" WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/""$(id -u ""$AUTOLOGIN_USER"")"" \
            kscreen-doctor output.1.mode.1920x1080@60 2>/dev/null || true
        echo ""Set KScreen resolution to 1920x1080@60 via kscreen-doctor.""
    else
        echo ""kscreen-doctor not found — KWin will use DRM default (1024x768).""
    fi

    # -- Build lamco-rdp-server from the vmcreate fork --------------------
    # The stock release binary black-screens on virtual GPUs (hyperv_drm /
    # virtio + software GL): PipeWire negotiates DMA-BUF, the capture never
    # delivers a frame on that path, and the software EGFX paths drop
    # FrameBuffer::DmaBuf anyway. The fork (feature/hyperv-enhanced-session)
    # fixes it entirely server-side:
    #   1. DMA-BUF CPU reads use the DMA_BUF_IOCTL_SYNC bracket, correct
    #      mmap pgoff, and reject non-linear modifiers.
    #   2. DmaBuf frames are materialized to CPU memory before caching.
    #   3. One-shot probe: DmaBuf negotiated but zero frames for 10s ->
    #      flip to MemFd and rebind the stream (measurement-driven, no
    #      driver-name allowlist).
    # No KWin patch is required: stock KWin works once the consumer side
    # handles the negotiated buffer types correctly.
    LAMCO_FORK_REPO=""moerketh/lamco-rdp-server""
    LAMCO_FORK_BRANCH=""feature/hyperv-enhanced-session""
    LAMCO_FORK_COMMIT=""""   # empty = branch head; pin a SHA for reproducible builds
    FORK_DIR=""/opt/lamco-fork""
    FORK_BUILD_LOG=""/tmp/lamco-fork-build.log""
    FORK_DONE_MARKER=""/opt/lamco-fork/.fork-installed""
    FORK_WAIT_INTERVAL=10
    FORK_WAIT_LOOPS=45   # 7.5 min poll inside this step; build self-completes if longer
    if [ ! -d ""$FORK_DIR/.git"" ]; then
        git clone --depth 1 --branch ""$LAMCO_FORK_BRANCH"" \
            ""https://github.com/${LAMCO_FORK_REPO}.git"" ""$FORK_DIR"" 2>/dev/null || true
    fi
    if [ -d ""$FORK_DIR"" ]; then
        if ! command -v cargo >/dev/null 2>&1; then
            echo ""Installing Rust toolchain (minimal profile)...""
            curl -fsSL https://sh.rustup.rs | sh -s -- -y --profile minimal 2>/dev/null || true
        fi
        export PATH=""$HOME/.cargo/bin:$PATH""
        if command -v cargo >/dev/null 2>&1; then
            # Build toolchain for the crates with native build scripts:
            # cmake (libopus_sys), pkg-config -dev headers probed by
            # libspa-sys/libpipewire (pipewire/spa), libopus_sys (opus),
            # zbus (dbus), the x264 feature, and the link-stage libs for
            # pam-auth (libpam) and the gui dev (libxkbcommon). A vanilla
            # server install only pulls runtime libs, not these.
            apt-get install -y -q build-essential pkg-config cmake ninja-build \
                libpipewire-0.3-dev libspa-0.2-dev libopus-dev \
                libdbus-1-dev libudev-dev libx264-dev \
                libpam0g-dev libxkbcommon-dev \
                python3-dbus python3-gi 2>/dev/null || true
            cd ""$FORK_DIR"" || exit 1
            if [ -n ""$LAMCO_FORK_COMMIT"" ]; then
                git fetch --depth 1 origin ""$LAMCO_FORK_COMMIT"" 2>/dev/null || true
                git checkout ""$LAMCO_FORK_COMMIT"" 2>/dev/null || true
            else
                git fetch --depth 1 origin ""$LAMCO_FORK_BRANCH"" 2>/dev/null || true
                git reset --hard FETCH_HEAD 2>/dev/null || true
            fi
            # The fork's licenses/ dir is not in git (packaging artifact), but
            # third_party.rs include_str!()s OpenH264 license texts at build
            # time. Fetch the genuine Cisco texts into the checkout.
            if [ ! -s ""$FORK_DIR/licenses/OpenH264-BINARY_LICENSE.txt"" ]; then
                mkdir -p ""$FORK_DIR/licenses""
                curl -fsSL https://raw.githubusercontent.com/cisco/openh264/master/LICENSE \
                    -o ""$FORK_DIR/licenses/OpenH264-BINARY_LICENSE.txt"" 2>/dev/null || true
                # The repo has no standalone PATENTS file; the LICENSE text
                # carries the patent grant. Use it for both required files.
                cp ""$FORK_DIR/licenses/OpenH264-BINARY_LICENSE.txt"" \
                   ""$FORK_DIR/licenses/OpenH264-PATENT.txt"" 2>/dev/null || true
            fi
            # A cold release build (LTO) takes longer than the deployment
            # step's command timeout, so run it DETACHED (survives this
            # shell) and poll here instead of blocking. The marker file
            # makes the build idempotent: a re-run of this script (or a
            # retry after a timeout kill) picks up where it left off —
            # cargo reuses target/ artifacts, so only the final link runs.
            rm -f ""$FORK_BUILD_LOG"" ""$FORK_DONE_MARKER""
            # The service restart is chained INTO the detached build: the
            # restart below (as the autologin user) makes the freshly built
            # server acquire its portal session — which throws the
            # ""accept screencast"" consent dialog on the VM console. So the
            # whole flow is: deployment finishes -> dialog appears -> click
            # Allow once. No manual commands. AUTOLOGIN_USER is exported so
            # the detached shell inherits it (it recomputes nothing).
            export AUTOLOGIN_USER
            nohup bash -c ""cd '$FORK_DIR' && PATH='$HOME/.cargo/bin':\$PATH \
                cargo build --release --features x264,vsock,kwin-virtual,libei \
                && install -m 0755 target/release/lamco-rdp-server /usr/bin/lamco-rdp-server \
                && touch '$FORK_DONE_MARKER' \
                && [ -n \$AUTOLOGIN_USER ] \
                && { loginctl enable-linger \$AUTOLOGIN_USER 2>/dev/null || true; \
                     sudo -u \$AUTOLOGIN_USER XDG_RUNTIME_DIR=/run/user/\$(id -u \$AUTOLOGIN_USER) \
                         systemctl --user enable lamco-rdp-server.service 2>/dev/null || true; \
                     sudo -u \$AUTOLOGIN_USER XDG_RUNTIME_DIR=/run/user/\$(id -u \$AUTOLOGIN_USER) \
                         systemctl --user restart lamco-rdp-server.service; }"" >""$FORK_BUILD_LOG"" 2>&1 &
            echo ""Fork build detached (log: $FORK_BUILD_LOG); waiting...""
            for i in $(seq 1 $FORK_WAIT_LOOPS); do
                if [ -f ""$FORK_DONE_MARKER"" ]; then
                    echo ""Installed fork-built lamco-rdp-server (DMA-BUF capture fixes included).""
                    break
                fi
                if ! pgrep -f ""cargo build --release"" >/dev/null 2>&1; then
                    # Build process died without the marker: real failure.
                    echo ""WARNING: fork build failed — keeping the release binary (expect a black screen on virtual GPUs)."" >&2
                    tail -5 ""$FORK_BUILD_LOG"" >&2 || true
                    break
                fi
                sleep $FORK_WAIT_INTERVAL
            done
            [ -f ""$FORK_DONE_MARKER"" ] || echo ""WARNING: fork build still running after ${FORK_WAIT_LOOPS}x${FORK_WAIT_INTERVAL}s — it installs itself on completion; rerun this step (or wait) to pick up the fixed binary."" >&2
            cd / || exit 1
        else
            echo ""WARNING: Rust toolchain unavailable — keeping the release binary."" >&2
        fi
    else
        echo ""WARNING: could not clone the fork — keeping the release binary."" >&2
    fi

    # -- Retire vgem artifacts from older deployments ---------------------
    # vgem was a DMA-BUF experiment leftover (a fake renderD128 racing the
    # real DRM node for the name). The fork needs no extra render node and
    # no KWin env overrides — stock KWin + materialize/fallback in lamco is
    # the shipping path. Clean up any artifacts old deployments left.
    rm -f /etc/modules-load.d/vgem.conf /etc/udev/rules.d/99-vgem-render.rules 2>/dev/null || true

    # -- Transparent cursor theme (KDE) ------------------------------------
    # KWin composites the cursor sprite into the framebuffer on Hyper-V
    # (hyperv_drm has no GPU cursor plane), which bakes the guest cursor
    # into the RDP video stream a frame or two behind the client-side
    # pointer — a lagging ghost arrow. Installing a fully transparent
    # XCursor theme removes it from the capture; the pointer shape PDU
    # (lamco cursor_pdu.rs reads breeze_cursors directly, not the active
    # theme) still delivers the real arrow to mstsc, so exactly one
    # client-rendered zero-lag pointer remains (xrdp parity).
    # XCursor binary layout was verified against a genuine breeze file
    # (magic 0x72756358 LE, version 0x00010000, 36-byte image chunks).
    # KDE/KWin only — GNOME/mutter deferred pending Hidden-mode
    # verification.
    if command -v kwriteconfig6 >/dev/null 2>&1 || command -v kwriteconfig5 >/dev/null 2>&1; then
        KW=kwriteconfig6
        command -v kwriteconfig6 >/dev/null 2>&1 || KW=kwriteconfig5
        if command -v python3 >/dev/null 2>&1; then
            python3 - << 'PYEOF' || echo ""WARNING: transparent cursor theme generation failed.""
import os, struct

work = '/tmp/lamco-transparent-theme'
d = os.path.join(work, 'transparent', 'cursors')
os.makedirs(d, exist_ok=True)

def make_xcursor():
    # 1x1 fully transparent image, nominal size 24 (libXcursor picks nearest)
    HEADER = 16; TOC = 12; CHUNK = 36; PIXELS = 4
    SUBTYPE = 24
    pos = HEADER + TOC
    header = struct.pack('<IIII', 0x72756358, HEADER, 0x00010000, 1)
    toc = struct.pack('<III', 0xFFFD0002, SUBTYPE, pos)
    chunk = struct.pack('<IIIIIIIII', CHUNK, 0xFFFD0002, SUBTYPE, 1, 1, 1, 0, 0, 1)
    data = header + toc + chunk + b'\x00\x00\x00\x00'
    assert len(data) == HEADER + TOC + CHUNK + PIXELS, len(data)
    return data

blob = make_xcursor()
fallback_names = [
    'left_ptr', 'right_ptr', 'cross', 'circle', 'xxx_authentication',
    'wait', 'left_ptr_watch', 'sb_h_double_arrow', 'sb_v_double_arrow',
    'bottom_left_corner', 'bottom_right_corner', 'top_left_corner',
    'top_right_corner', 'grab', 'grabbing', 'hand', 'hand2', 'pointer',
    'question_arrow', 'text', 'watch', 'half-busy', 'openhand',
    'closedhand', 'fcfz', 'left_side', 'right_side', 'top_side',
    'bottom_side', 'center_ptr', 'crosshair', 'dot', 'dot_box_mask',
    'icon', 'menu', 'pencil', 'pirate', 'plus', 'trek', 'ul_angle',
    'ur_angle', 'll_angle', 'lr_angle', 'move', 'all-scroll',
    'vertical-text', 'context-menu', 'copy', 'progress', 'not-allowed',
    'no-drop', 'col-resize', 'row-resize', 'nesw-resize', 'nwse-resize',
    'ew-resize', 'ns-resize', 'cell', 'color-picker', 'zoom-in',
    'zoom-out',
]
# CRITICAL (2026-08-22 wallpaper-ghost fix): shadow EVERY cursor name of
# an installed real theme, not just the list above. XCursor themes
# INHERIT the parent theme for any name they lack - and Plasma's desktop
# background uses the ""default"" role, which is NOT in the fallback list.
# Result was: transparent cursor over windows (text role covered) but a
# visible lagging breeze arrow over the desktop background only.
names = set(fallback_names)
for theme_dir in ('/usr/share/icons/breeze_cursors/cursors',
                  '/usr/share/icons/Adwaita/cursors',
                  '/usr/share/icons/whiteglass/cursors',
                  '/usr/share/icons/default/cursors'):
    try:
        names.update(os.listdir(theme_dir))
    except OSError:
        continue
for n in sorted(names):
    with open(os.path.join(d, n), 'wb') as f:
        f.write(blob)
with open(os.path.join(work, 'transparent', 'index.theme'), 'w') as f:
    f.write('[Icon Theme]\nInherits=breeze_cursors\n')
print('generated', len(names), 'transparent cursor files')
PYEOF
            if [ -d /tmp/lamco-transparent-theme/transparent ]; then
                rm -rf /usr/share/icons/transparent
                cp -r /tmp/lamco-transparent-theme/transparent /usr/share/icons/transparent
                rm -rf /tmp/lamco-transparent-theme
                echo ""Installed transparent cursor theme to /usr/share/icons/transparent.""
            fi
        else
            echo ""python3 not found - skipping transparent cursor theme install.""
        fi
        # Activate for the autologin user — SESSION-SCOPED from here on.
        # The lamco server makes the cursor transparent only while an RDP
        # client is connected and restores it on disconnect + ExecStopPost
        # (see cursor_theme.rs in the lamco fork; needs the transparent
        # theme INSTALLED but not active). Provisioning therefore leaves
        # kcminputrc on a VISIBLE theme (breeze_cursors) so the console
        # always has a pointer at boot — xrdp-parity console behavior.
        # GOTCHA (verified 2026-08-22): plasma-apply-cursortheme only
        # swaps the live sprite when config differs — lamco's apply uses
        # the breeze_cursors→transparent toggle to force a real reload.
        sudo -u ""$AUTOLOGIN_USER"" ""$KW"" --file kcminputrc --group Mouse --key cursorTheme breeze_cursors 2>/dev/null || true
        # Disable the Shake Cursor effect: pointless compositing churn on
        # an invisible sprite, and wiggle-scaling was the visual tell of
        # the wallpaper ghost. Runtime-unloaded by lamco per session.
        sudo -u ""$AUTOLOGIN_USER"" ""$KW"" --file kwinrc --group Plugins --key shakecursorEnabled false 2>/dev/null || true
        sudo -u ""$AUTOLOGIN_USER"" XDG_RUNTIME_DIR=/run/user/$(id -u ""$AUTOLOGIN_USER"") DISPLAY=:0 WAYLAND_DISPLAY=wayland-0 qdbus6 org.kde.KWin /KWin reconfigure 2>/dev/null || true
        echo ""Cursor setup: transparent theme installed, console on breeze_cursors (lamco toggles per RDP session).""
    fi

    # -- Idle-lock suppression (KDE) ----------------------------------------
    # E2E finding (2026-09-01, TEST_20260901180150): KDE's idle autolock
    # engaged during RDP sessions on Hyper-V, and the LOCK GREETER WEDGED
    # under the hyperv_drm framebuffer error spam (44% CPU, no frames) —
    # swallowing ALL input including the vmconnect console. A wedged lock
    # screen bricks the machine remotely (only a reboot recovers).
    # Two provisions:
    #   1. kscreenlockerrc Autolock=false — read at session start, so the
    #      first boot of a provisioned VM is already immune.
    #   2. A durable systemd user unit holding a freedesktop ScreenSaver
    #      inhibitor cookie — a dbus-send one-shot dies with its connection
    #      and releases the cookie; a holder process keeps it for the
    #      session lifetime (the validated pattern from the E2E).
    if [ -n ""$AUTOLOGIN_USER"" ] && command -v kwriteconfig6 >/dev/null 2>&1; then
        sudo -u ""$AUTOLOGIN_USER"" kwriteconfig6 --file kscreenlockerrc --group Daemon --key Autolock false 2>/dev/null || true
        sudo -u ""$AUTOLOGIN_USER"" kwriteconfig6 --file kscreenlockerrc --group Daemon --key LockOnResume false 2>/dev/null || true
        # Holder script: takes the inhibitor and parks (keeps the D-Bus
        # connection alive so the cookie stays held).
        # GOTCHA: do NOT use `sudo -u $U mkdir -p ~/.local/bin` — bash
        # expands ~ to ROOT's home BEFORE sudo runs, so the user's directory
        # never exists and the heredoc cat below fails (fresh-VM boot
        # 2026-09-02: unit crash-looped on the missing file). Use the
        # literal /home/$AUTOLOGIN_USER path for every write.
        mkdir -p /home/""$AUTOLOGIN_USER""/.local/bin 2>/dev/null || true
        chown ""$AUTOLOGIN_USER"": /home/""$AUTOLOGIN_USER""/.local/bin 2>/dev/null || true
        cat > /home/""$AUTOLOGIN_USER""/.local/bin/lamco-idle-inhibit.py << 'PYEOF'
#!/usr/bin/env python3
# Hold a freedesktop ScreenSaver inhibitor cookie for the session lifetime.
# Installed by VMCreate: KDE's idle lock wedges under hyperv_drm framebuffer
# spam on Hyper-V and bricks remote access. dbus-send one-shots release the
# cookie when their connection dies; this process parks holding it.
import time

import dbus

bus = dbus.SessionBus()
ss = dbus.Interface(
    bus.get_object(""org.freedesktop.ScreenSaver"", ""/ScreenSaver""),
    ""org.freedesktop.ScreenSaver"",
)
cookie = ss.Inhibit(""lamco-rdp"", ""rdp-session-keepalive"")
print(f""inhibitor cookie: {cookie}"", flush=True)
while True:
    time.sleep(60)
    try:
        ss.GetActive()
    except Exception:
        pass
PYEOF
        chown ""$AUTOLOGIN_USER"": /home/""$AUTOLOGIN_USER""/.local/bin/lamco-idle-inhibit.py 2>/dev/null || true
        chmod 0755 /home/""$AUTOLOGIN_USER""/.local/bin/lamco-idle-inhibit.py 2>/dev/null || true
        # Systemd user unit; binds to the graphical session (the session bus
        # where the screensaver runs exists only there). Same ~-expansion
        # rule: literal /home path (script runs as root; mkdir+chown).
        mkdir -p /home/""$AUTOLOGIN_USER""/.config/systemd/user 2>/dev/null || true
        chown ""$AUTOLOGIN_USER"": /home/""$AUTOLOGIN_USER""/.config/systemd/user 2>/dev/null || true
        cat > /home/""$AUTOLOGIN_USER""/.config/systemd/user/lamco-idle-inhibit.service << 'UNITEOF'
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
        chown ""$AUTOLOGIN_USER"": /home/""$AUTOLOGIN_USER""/.config/systemd/user/lamco-idle-inhibit.service 2>/dev/null || true
        sudo -u ""$AUTOLOGIN_USER"" XDG_RUNTIME_DIR=/run/user/$(id -u ""$AUTOLOGIN_USER"") \
            systemctl --user daemon-reload 2>/dev/null || true
        sudo -u ""$AUTOLOGIN_USER"" XDG_RUNTIME_DIR=/run/user/$(id -u ""$AUTOLOGIN_USER"") \
            DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u ""$AUTOLOGIN_USER"")/bus \
            systemctl --user enable lamco-idle-inhibit.service 2>/dev/null || true
        sudo -u ""$AUTOLOGIN_USER"" XDG_RUNTIME_DIR=/run/user/$(id -u ""$AUTOLOGIN_USER"") \
            DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u ""$AUTOLOGIN_USER"")/bus \
            systemctl --user start lamco-idle-inhibit.service 2>/dev/null || true
        echo ""Idle-lock suppression: Autolock=false + lamco-idle-inhibit.service (Hyper-V lock-greeter wedge prevention).""
    fi

    # -- KWin private-interface grant (zkde-screencast) ----------------------
    # KWin 6.x gates private Wayland interfaces behind an allowlist:
    # zkde_screencast_unstable_v1 is only advertised to clients whose
    # .desktop file lists it under X-KDE-Wayland-Interfaces (matched by the
    # client's executable path — see KWin wayland_server.cpp
    # interfacesBlackList + serviceutils.h fetchRequestedInterfaces).
    # xdg-desktop-portal-kde and krfb ship such entries; without one the
    # lamco fork's kwin-virtual strategy cannot bind the global (fresh-VM
    # finding TEST_20260902110104: ""zkde stream creation failed: zkde
    # _screencast global not bound"" on every connect, and the global is
    # invisible even to wayland-info).
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
    if [ -n ""$AUTOLOGIN_USER"" ] && command -v kbuildsycoca6 >/dev/null 2>&1; then
        sudo -u ""$AUTOLOGIN_USER"" XDG_RUNTIME_DIR=/run/user/$(id -u ""$AUTOLOGIN_USER"") \
            DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u ""$AUTOLOGIN_USER"")/bus \
            kbuildsycoca6 --noincremental 2>/dev/null || true
    fi
    echo ""KWin private-interface grant installed (zkde_screencast_unstable_v1 for /usr/bin/lamco-rdp-server).""

    # linger + enable (the service starts on next graphical-session target)
    loginctl enable-linger ""$AUTOLOGIN_USER"" 2>/dev/null || true
    sudo -u ""$AUTOLOGIN_USER"" XDG_RUNTIME_DIR=/run/user/$(id -u ""$AUTOLOGIN_USER"") \
        systemctl --user daemon-reload 2>/dev/null || true
    sudo -u ""$AUTOLOGIN_USER"" XDG_RUNTIME_DIR=/run/user/$(id -u ""$AUTOLOGIN_USER"") \
        systemctl --user enable lamco-rdp-server.service 2>/dev/null || true
    echo ""Installed systemd user unit for $AUTOLOGIN_USER and enabled linger.""
else
    echo ""WARNING: could not determine autologin user; systemd user unit not installed."" >&2
fi

# -- Open firewall port 3389 (best-effort) ---------------------------------
if command -v ufw >/dev/null 2>&1; then
    ufw allow 3389/tcp 2>/dev/null || true
elif command -v firewall-cmd >/dev/null 2>&1; then
    firewall-cmd --add-port=3389/tcp --permanent 2>/dev/null || true
    firewall-cmd --reload 2>/dev/null || true
fi

# -- Raise journald rate limit (diagnosability) ----------------------------
# E2E finding (2026-09-01, TEST_20260901180150): hyperv_drm framebuffer
# error spam (100+/s) exhausts journald's default rate limit (RateLimitBurst
# =100 per 30s) within seconds, after which journald silently DROPS all
# further user-session logs - including the very lamco/kwin-virtual lines
# needed to diagnose live sessions (""no events"" in journalctl does not
# mean ""no activity""). Raise the burst so session logs always survive.
mkdir -p /etc/systemd/journald.conf.d
cat > /etc/systemd/journald.conf.d/99-lamco-ratelimit.conf << 'JOURNALD_EOF'
[Journal]
RateLimitIntervalSec=30s
RateLimitBurst=100000
JOURNALD_EOF
systemctl restart systemd-journald 2>/dev/null || true
echo ""Raised journald rate limit (framebuffer spam must not drown session logs).""

echo ""=== Lamco RDP Server install complete ===""
echo ""NOTE: After first graphical login, run 'lamco-rdp-server --grant-permission'""
echo ""      and click Allow to authorize screen sharing (one-time, interactive).""
exit 0
";
    }
}

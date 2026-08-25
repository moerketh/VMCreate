using Microsoft.Extensions.Logging;
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

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/install_lamco.sh && sudo rm -f /tmp/install_lamco.sh", ct);

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
    # Ensure the key is readable by the unprivileged user service (the helper
    # may default to 600 root-only, which blocks the user service from loading it).
    chmod 644 /etc/lamco-rdp-server/cert.pem 2>/dev/null || true
    chmod 644 /etc/lamco-rdp-server/key.pem 2>/dev/null || true
fi
# If the helper did not produce both files (or was absent), generate them now.
if [ ! -f /etc/lamco-rdp-server/cert.pem ] || [ ! -f /etc/lamco-rdp-server/key.pem ]; then
    echo ""Generating self-signed TLS certificate via openssl...""
    openssl req -x509 -newkey rsa:4096 -nodes \
        -keyout /etc/lamco-rdp-server/key.pem \
        -out /etc/lamco-rdp-server/cert.pem \
        -days 365 -subj ""/CN=$(hostname)"" \
        -addext ""subjectAltName=DNS:$(hostname),DNS:localhost,IP:127.0.0.1"" 2>&1
    # The cert + key must be readable by the unprivileged autologin user whose
    # systemd user service loads them. This is a local single-user VM, so 644
    # on the key is acceptable (the cert is self-signed, not a production
    # secret). Without this, the user service fails with Failed to load TLS
    # certificates because a root-owned 600 key.pem is inaccessible.
    chmod 644 /etc/lamco-rdp-server/cert.pem
    chmod 644 /etc/lamco-rdp-server/key.pem
fi
# Final guard: if certs are STILL missing, the server cannot start.
if [ ! -f /etc/lamco-rdp-server/cert.pem ] || [ ! -f /etc/lamco-rdp-server/key.pem ]; then
    echo ""ERROR: TLS certificate generation failed — cert.pem/key.pem not found."" >&2
    exit 1
fi
echo ""TLS certificates present: /etc/lamco-rdp-server/cert.pem""

# -- Write config.toml (TLS security, no auth, listen on all interfaces) ----
# security_mode=tls + auth_method=none: the working combination for Lamco
# with standard RDP clients (mstsc, FreeRDP). No credssp_credentials --
# including that section causes IronRDP acceptor to reject connections with
# invalid credentials even when auth_method=none.
#
# NOTE: Hyper-V Enhanced Session (vmconnect.exe with HvSocket/VMBus transport)
# does NOT work with Lamco/IronRDP because Hyper-V uses a proprietary pre-RDP
# greeting protocol that IronRDP does not implement. Users must connect via
# standard RDP (mstsc to VM-IP:3389) instead of Enhanced Session.
cat > /etc/lamco-rdp-server/config.toml << 'CONFIG_EOF'
[server]
listen_addr = ""0.0.0.0:3389""
max_connections = 10
session_timeout = 0
use_portals = true

[security]
cert_path = ""/etc/lamco-rdp-server/cert.pem""
key_path = ""/etc/lamco-rdp-server/key.pem""
security_mode = ""tls""
auth_method = ""none""
require_tls_13 = false

[video]
target_fps = 30
cursor_mode = ""embedded""

[audio]
enabled = true
codec = ""auto""
sample_rate = 48000
channels = 2
frame_ms = 20
opus_bitrate = 64000

[display]
allow_resize = true
allowed_resolutions = []
dpi_aware = false
frame_transform = ""auto""

[capture]
protocol = ""auto""
allow_fallback = true
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
            apt-get install -y -q build-essential pkg-config 2>/dev/null || true
            cd ""$FORK_DIR"" || exit 1
            if [ -n ""$LAMCO_FORK_COMMIT"" ]; then
                git fetch --depth 1 origin ""$LAMCO_FORK_COMMIT"" 2>/dev/null || true
                git checkout ""$LAMCO_FORK_COMMIT"" 2>/dev/null || true
            else
                git fetch --depth 1 origin ""$LAMCO_FORK_BRANCH"" 2>/dev/null || true
                git reset --hard FETCH_HEAD 2>/dev/null || true
            fi
            # A cold release build (LTO) takes longer than the deployment
            # step's command timeout, so run it DETACHED (survives this
            # shell) and poll here instead of blocking. The marker file
            # makes the build idempotent: a re-run of this script (or a
            # retry after a timeout kill) picks up where it left off —
            # cargo reuses target/ artifacts, so only the final link runs.
            rm -f ""$FORK_BUILD_LOG"" ""$FORK_DONE_MARKER""
            nohup bash -c ""cd '$FORK_DIR' && PATH='$HOME/.cargo/bin':\$PATH \
                cargo build --release --features x264,vsock \
                && install -m 0755 target/release/lamco-rdp-server /usr/bin/lamco-rdp-server \
                && touch '$FORK_DONE_MARKER'"" >""$FORK_BUILD_LOG"" 2>&1 &
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

echo ""=== Lamco RDP Server install complete ===""
echo ""NOTE: After first graphical login, run 'lamco-rdp-server --grant-permission'""
echo ""      and click Allow to authorize screen sharing (one-time, interactive).""
exit 0
";
    }
}

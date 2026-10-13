#!/bin/bash
set -o pipefail

# =========================================================================
# wayrdp — install + configure (Wayland-native, MIT, FreeRDP 3)
# =========================================================================
# wayrdp is the replacement for the Lamco fork: same high-level shape
# (post-boot SSH install, systemd USER unit, kept-Wayland headless capture)
# but on the project's own protocol stack (libfreerdp-server) with
# compositor-specific backends — the wlroots backend drives Hyprland via
# `hyprctl output create headless` + ext-image-copy-capture, no portal
# consent required.
#
# SECURITY/RELIABILITY CONTRACT (same as install_lamco.sh):
#   - FAIL LOUDLY: no fallback path, no partial install silently accepted.
#   - STREAM RULE: the SSH transport returns STDOUT ONLY on zero-exit runs
#     (stderr is dropped to a debug log), so every DEGRADED/WARNING/NOTICE
#     reason line prints to stdout; only hard-failure ERROR lines use stderr
#     (there stderr IS captured into the thrown SSH exception).
#   - Result contract: prints exactly one terminal line
#     WAYRDP_RESULT=ok|degraded — hard failures exit non-zero BEFORE that
#     line is reached.
#
# PACKAGE DELIVERY (current): wayrdp lives in a PRIVATE repo with no public
# release asset, so the guest cannot fetch a package over the network. The
# host step (InstallWayrdpRdpStep) scp's the portable tarball
# (wayrdp-<ver>-linux-x86_64.tar.gz, built by scripts/build-package.sh) to
# /tmp/wayrdp-pkg.tar.gz alongside this script, and its sha256 into
# /tmp/wayrdp-pkg.sha256. This script verifies the digest, extracts the
# bundle, and runs its install.sh. When wayrdp ships a public release this
# becomes a pinned curl of $WAYRDP_PKG_URL — mirror install_lamco.sh.
#
# The tarball layout (scripts/build-package.sh):
#   wayrdp-<ver>-linux-x86_64/
#     bin/wayrdp
#     lib/systemd/user/wayrdp.service
#     share/doc/wayrdp/LICENSE
#     install.sh            — installs to /usr/local (default prefix)

DEGRADED=0

# Pinned digest of the tarball the host uploads. Injected by
# InstallWayrdpRdpStep via the __WAYRDP_PKG_SHA256__ placeholder; when the
# placeholder is left un-replaced the digest is read from the sidecar file
# /tmp/wayrdp-pkg.sha256 (manual/local deploys).
WAYRDP_PKG_TARBALL="/tmp/wayrdp-pkg.tar.gz"
WAYRDP_PKG_SHA256_FILE="/tmp/wayrdp-pkg.sha256"
WAYRDP_PKG_SHA256="__WAYRDP_PKG_SHA256__"

echo "=== wayrdp install (Wayland-native, FreeRDP 3) ==="
echo "NOTICE: wayrdp is MIT-licensed (c) moerketh — the project's own"
echo "        Wayland-native RDP server replacing the BSL-licensed Lamco fork."

# -- Validate distro: Debian family or pacman family (freerdp3 available) ---
if [ ! -f /etc/os-release ]; then
    echo "ERROR: /etc/os-release not found — cannot determine distro." >&2
    exit 1
fi
. /etc/os-release
DISTRO_ID="${ID}"
DISTRO_LIKE="${ID_LIKE:-}"
is_debian_family() {
    case "$DISTRO_ID" in
        ubuntu|debian|parrot|kali) return 0 ;;
    esac
    case " $DISTRO_LIKE " in
        *" debian"*|*" ubuntu"*) return 0 ;;
    esac
    return 1
}
is_arch_family() {
    case "$DISTRO_ID" in
        arch|omarchy|athenaos) return 0 ;;
    esac
    case " $DISTRO_LIKE " in
        *" arch"*) return 0 ;;
    esac
    return 1
}
if ! is_debian_family && ! is_arch_family; then
    echo "ERROR: distro '${DISTRO_ID}' (ID_LIKE='${DISTRO_LIKE}') is neither Debian-family nor pacman-family." >&2
    echo "       wayrdp needs FreeRDP 3 from the distro packages; rpm distros await" >&2
    echo "       a packaging line." >&2
    exit 1
fi

# -- Runtime dependency: FreeRDP 3 server libs ------------------------------
# wayrdp links libfreerdp-server3 / libfreerdp3 / libwinpr3 at runtime.
# Debian: trixie (13, stable) has freerdp3; bookworm (12) needs backports.
# Arch ships it as the 'freerdp' package.
install_freerdp_deps() {
    if is_arch_family; then
        echo "Installing runtime deps via pacman: freerdp openssl libxkbcommon wayland"
        pacman -Sy --noconfirm --needed freerdp openssl libxkbcommon wayland 2>&1 || {
            echo "ERROR: pacman failed to install freerdp runtime deps." >&2
            exit 1
        }
    elif is_debian_family; then
        echo "Installing runtime deps via apt: libfreerdp-server3-3 libfreerdp3-3 libwinpr3-3 openssl"
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq 2>&1 || true
        if ! apt-get install -y -qq libfreerdp-server3-3 libfreerdp3-3 libwinpr3-3 openssl 2>&1; then
            echo "ERROR: apt failed to install freerdp3 runtime libs." >&2
            echo "       wayrdp needs FreeRDP 3; on Debian bookworm enable backports first." >&2
            exit 1
        fi
    fi
}
install_freerdp_deps

# -- Locate + verify the uploaded tarball -----------------------------------
if [ ! -f "$WAYRDP_PKG_TARBALL" ]; then
    echo "ERROR: wayrdp package tarball not found at $WAYRDP_PKG_TARBALL." >&2
    echo "       The host step scp's dist/release/wayrdp-<ver>-linux-x86_64.tar.gz" >&2
    echo "       there before running this script; it is missing." >&2
    exit 1
fi

# Resolve the expected digest: injected constant, else sidecar file.
if [ "$WAYRDP_PKG_SHA256" = "__WAYRDP_PKG_SHA256__" ]; then
    if [ -f "$WAYRDP_PKG_SHA256_FILE" ]; then
        WAYRDP_PKG_SHA256="$(awk '{print $1}' "$WAYRDP_PKG_SHA256_FILE" | head -1)"
    else
        echo "ERROR: no pinned sha256 for the wayrdp tarball (placeholder not" >&2
        echo "       substituted and $WAYRDP_PKG_SHA256_FILE missing)." >&2
        exit 1
    fi
fi

echo "Verifying sha256 of $(basename "$WAYRDP_PKG_TARBALL") ..."
ACTUAL_SHA256="$(sha256sum "$WAYRDP_PKG_TARBALL" | awk '{print $1}')"
if [ "$ACTUAL_SHA256" != "$WAYRDP_PKG_SHA256" ]; then
    echo "ERROR: sha256 mismatch for the wayrdp tarball." >&2
    echo "       expected: $WAYRDP_PKG_SHA256" >&2
    echo "       actual:   $ACTUAL_SHA256" >&2
    echo "       Refusing to install an unverified/re-pointed package." >&2
    exit 1
fi
echo "sha256 OK."

# -- Extract + run the bundle's install.sh ----------------------------------
# NOTE on the mktemp name: do NOT use the /tmp/wayrdp-pkg.XXXXXX template
# here — its basename starts with 'wayrdp-', so
# `find "$EXTRACT_DIR" -maxdepth 1 -type d -name 'wayrdp-*'` would match the
# extract dir ITSELF (basename wayrdp-pkg.*) and `head -1` could pick it over
# the real nested bundle dir (that exact false-match caused "extracted bundle
# has no executable install.sh"). Use a non-matching template instead.
EXTRACT_DIR="$(mktemp -d /tmp/wrdp-extract.XXXXXX)"
trap 'rm -rf "$EXTRACT_DIR"' EXIT
if ! tar -xzf "$WAYRDP_PKG_TARBALL" -C "$EXTRACT_DIR" 2>&1; then
    echo "ERROR: failed to extract the wayrdp tarball." >&2
    exit 1
fi
# The bundle root is the single top-level directory the tarball creates
# (wayrdp-<ver>-linux-x86_64). Match it explicitly and require install.sh.
BUNDLE_DIR=""
for d in "$EXTRACT_DIR"/wayrdp-*/; do
    if [ -d "$d" ] && [ -e "${d}install.sh" ]; then
        BUNDLE_DIR="${d%/}"
        break
    fi
done
if [ -z "$BUNDLE_DIR" ] || [ ! -x "$BUNDLE_DIR/install.sh" ]; then
    echo "ERROR: extracted bundle has no executable install.sh ($BUNDLE_DIR)." >&2
    exit 1
fi
if ! bash "$BUNDLE_DIR/install.sh" --prefix /usr/local 2>&1; then
    echo "ERROR: wayrdp install.sh failed." >&2
    exit 1
fi
echo "wayrdp installed to /usr/local."

# Post-install sanity: the binary must at least launch far enough to print
# its banner/listener line. wayrdp has no --version flag (Phase 0 spike), so
# the check is "exists + is executable + links resolve".
if [ ! -x /usr/local/bin/wayrdp ]; then
    echo "ERROR: /usr/local/bin/wayrdp missing after install." >&2
    exit 1
fi
if ldd /usr/local/bin/wayrdp 2>/dev/null | grep -q "not found"; then
    echo "ERROR: /usr/local/bin/wayrdp has unresolved shared libraries:" >&2
    ldd /usr/local/bin/wayrdp | grep "not found" >&2
    exit 1
fi
echo "Binary linkage OK."

# -- Hyprland shm-capture plugin (optional, best-effort) ---------------------
# Hyprland guests get the zero-repaint capture path: the compositor plugin
# ships SOURCE-ONLY (Hyprland has no stable C++ ABI) and must be built on the
# guest against its own headers. When the build works we also enable the
# shm-plugin capture drop-in and the autostart plugin-load hook; on any
# failure wayrdp falls back to ext-image-copy-capture, which still works but
# costs a full re-render + readPixels per frame (~40ms at 1080p on llvmpipe).
PLUGIN_SRC_DIR="/usr/local/share/wayrdp/plugins/hyprland-capture"
if [ ! -d "$PLUGIN_SRC_DIR" ] && command -v hyprctl >/dev/null 2>&1; then
    echo "NOTE: Hyprland shm-capture plugin source not at $PLUGIN_SRC_DIR;"
    echo "      capture uses ext-image-copy (slower, still functional)."
fi
if [ -d "$PLUGIN_SRC_DIR" ] && command -v hyprctl >/dev/null 2>&1; then
    # Resolve the desktop user early for plugin paths (AUTOLOGIN_USER is
    # re-resolved below for the unit; same logic).
    PLUGIN_USER="__AUTOLOGIN_USER__"
    if [ "$PLUGIN_USER" = "__AUTOLOGIN_USER__" ] || [ -z "$PLUGIN_USER" ]; then
        PLUGIN_USER="$(awk -F: '$3 >= 1000 && $3 < 65534 && $6 != "" {print $1; exit}' /etc/passwd 2>/dev/null || echo "")"
    fi
    if [ -n "$PLUGIN_USER" ] && id "$PLUGIN_USER" >/dev/null 2>&1; then
        PLUGIN_HOME="$(getent passwd "$PLUGIN_USER" | cut -d: -f6)"
        PLUGIN_DIR="$PLUGIN_HOME/.local/share/wayrdp"
        PLUGIN_SO="$PLUGIN_DIR/hyprland-capture.so"
        mkdir -p "$PLUGIN_DIR" 2>/dev/null || true
        chown "$PLUGIN_USER":"$PLUGIN_USER" "$PLUGIN_DIR" 2>/dev/null || true
        BUILD_OK=0
        if command -v g++ >/dev/null 2>&1 && pkg-config --exists hyprland 2>/dev/null; then
            # Build in a staging dir as the desktop user so no root-owned
            # artifacts land in the user's home.
            PLUGIN_BUILD="$(mktemp -d /tmp/wayrdp-plugin.XXXXXX)"
            cp "$PLUGIN_SRC_DIR/main.cpp" "$PLUGIN_SRC_DIR/Makefile" "$PLUGIN_BUILD/"
            chown -R "$PLUGIN_USER":"$PLUGIN_USER" "$PLUGIN_BUILD" 2>/dev/null || true
            if sudo -u "$PLUGIN_USER" make -C "$PLUGIN_BUILD" >/dev/null 2>&1 \
               && [ -f "$PLUGIN_BUILD/wayrdp-capture.so" ]; then
                # Canonical name: hyprland-capture.so. An earlier rev shipped
                # main-v2/v2 names; a boot hook that loads a stale -v2 path
                # leaves two plugin instances fighting over the shm.
                install -m 644 "$PLUGIN_BUILD/wayrdp-capture.so" "$PLUGIN_SO"
                chown "$PLUGIN_USER":"$PLUGIN_USER" "$PLUGIN_SO" 2>/dev/null || true
                BUILD_OK=1
                echo "Hyprland shm-capture plugin built: $PLUGIN_SO"
            fi
            rm -rf "$PLUGIN_BUILD" 2>/dev/null || true
            # Remove stale plugin .so files from earlier revs so no boot hook
            # (or manual load) can pick an old build — hyprctl dedupes plugin
            # unloads by exact load-path string, so mixed paths cause
            # double-instances that race the capture shm.
            find "$PLUGIN_DIR" -maxdepth 1 -name '*.so' ! -name 'hyprland-capture.so' \
                -delete 2>/dev/null || true
        else
            echo "NOTE: skipping Hyprland plugin build (need g++ + hyprland pkg-config);"
            echo "      capture falls back to ext-image-copy (slower, still functional)."
        fi

        if [ "$BUILD_OK" = "1" ]; then
            # Enable the shm capture path via a user drop-in.
            DROPIN_DIR="/home/$PLUGIN_USER/.config/systemd/user/wayrdp.service.d"
            if [ -d "$PLUGIN_HOME" ]; then
                DROPIN_DIR="$PLUGIN_HOME/.config/systemd/user/wayrdp.service.d"
            fi
            mkdir -p "$DROPIN_DIR" 2>/dev/null || true
            cat > "$DROPIN_DIR/shm.conf" <<'SHM_EOF'
# Enable the Hyprland plugin shm capture path (wayrdp backend-wlroots).
# The plugin is loaded separately via the Hyprland autostart hook. The shm
# is per-monitor (/dev/shm/wayrdp-capture-<output>) so concurrent sessions
# cannot race one buffer; if the plugin is missing the pump waits for its
# output's shm. Only install this drop-in with a successfully built plugin.
[Service]
Environment=WAYRDP_CAPTURE=shm-plugin
SHM_EOF
            chown -R "$PLUGIN_USER":"$PLUGIN_USER" "$DROPIN_DIR" 2>/dev/null || true
            echo "shm-plugin drop-in written ($DROPIN_DIR/shm.conf)."

            # Plugin autoload: Hyprland Lua hook, same file as the unit hook
            # below. hyprctl plugin load is keyed by absolute path, so the
            # filename must match across boots.
            AUTO_LUA="$PLUGIN_HOME/.config/hypr/autostart.lua"
            if [ -d "$PLUGIN_HOME/.config/hypr" ] || command -v hyprctl >/dev/null 2>&1; then
                mkdir -p "$PLUGIN_HOME/.config/hypr" 2>/dev/null || true
                if ! grep -q "hyprland-capture.so" "$AUTO_LUA" 2>/dev/null; then
                    cat >> "$AUTO_LUA" <<'PLUG_EOF'
-- wayrdp shm-capture plugin (added by VMCreate install_wayrdp.sh): loads the
-- compositor plugin that feeds /dev/shm/wayrdp-capture-<output>. Runs during
-- config parse, before the Hyprland IPC socket necessarily exists, so a
-- bare load races the socket (observed live: "no plugins loaded" after
-- login). Retry in a backgrounded shell for up to ~10s. If it ultimately
-- fails, wayrdp falls back to waiting on shm; the ext-image-copy path only
-- returns after removing the shm.conf drop-in.
--
-- Boot-crash avoidance (Hyprland <=0.56): an early hyprctl IPC query on
-- workspaces/monitors null-derefs the keyboard layout index before xkb init
-- (getWorkspaceData -> IKeyboard::getActiveLayoutIndex -> SIGSEGV), and the
-- Omarchy start-hyprland wrapper then relaunches with --safe-mode. Wait for
-- compositor input readiness (a keyboard reporting a non-empty active_keymap)
-- via `hyprctl devices -j` -- which is itself safe pre-init (empty list, no
-- layout deref) -- before issuing any plugin-load query.
os.execute("sh -c '(for i in $(seq 1 40); do hyprctl devices -j 2>/dev/null | grep -q \"\\\"active_keymap\\\":[ ]*\\\"[^\\\"]\" && break; sleep 0.25; done; for i in $(seq 1 40); do hyprctl plugin load $HOME/.local/share/wayrdp/hyprland-capture.so >/dev/null 2>&1 && exit 0; sleep 0.25; done) &'")
PLUG_EOF
                    chown "$PLUGIN_USER":"$PLUGIN_USER" "$AUTO_LUA" 2>/dev/null || true
                    echo "Wrote plugin autoload hook to $AUTO_LUA."
                fi
            fi

            # If a session is live, load the plugin right now (ignore the
            # "twice" error — load is idempotent-keyed by path).
            if [ -d "/run/user/$(id -u "$PLUGIN_USER")/hypr" ]; then
                sudo -u "$PLUGIN_USER" XDG_RUNTIME_DIR="/run/user/$(id -u "$PLUGIN_USER")" \
                    hyprctl plugin load "$PLUGIN_SO" >/dev/null 2>&1 || true
            fi
        fi
    fi
fi

# -- Firewall: open RDP port 3389 -------------------------------------------
# Omarchy ships ufw with `INPUT policy DROP`, allowing only ssh(22) +
# localsend(53317) + docker-dns. The wayrdp listener (0.0.0.0:3389) is healthy
# but silently unreachable from the host until 3389 is allowed — this was the
# precise cause of the scripted-T5 "TCP connect to 3389 failed" while ssh
# worked. Open it via ufw when present (best-effort; non-fatal if absent).
if command -v ufw >/dev/null 2>&1; then
    if ufw allow 3389/tcp >/dev/null 2>&1; then
        echo "ufw: allowed 3389/tcp."
    else
        echo "WARN: ufw present but 'ufw allow 3389/tcp' failed (non-fatal)." >&2
    fi
elif command -v firewall-cmd >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port=3389/tcp >/dev/null 2>&1 \
        && firewall-cmd --reload >/dev/null 2>&1 \
        && echo "firewalld: allowed 3389/tcp." \
        || echo "WARN: firewall-cmd present but rule-add failed (non-fatal)." >&2
else
    echo "No ufw/firewalld firewall manager found; leaving 3389 to the distro default."
fi

# -- Hyper-V vsock transport (vmconnect Enhanced Session) --------------------
# wayrdp listens on AF_VSOCK (hv_sock) alongside TCP so vmconnect Enhanced
# Session works with no network dependency. vsock bypasses netfilter entirely,
# so nothing here touches ufw. hv_sock is a module on most guest kernels
# (CONFIG_VSOCKETS=m); load it for the current boot and persist it.
# On non-Hyper-V guests the module is absent — wayrdp then logs once and
# serves TCP only, so failures here are non-fatal; we also skip the
# modules-load entry to keep the boot log clean there.
if [ -d /sys/module/hv_sock ] || modprobe hv_sock 2>/dev/null; then
    echo "hv_sock: loaded."
    printf '# wayrdp: Hyper-V sockets for the vmconnect Enhanced Session transport\nhv_sock\n' \
        > /etc/modules-load.d/wayrdp-vsock.conf
    echo "hv_sock: persisted via /etc/modules-load.d/wayrdp-vsock.conf."
else
    echo "NOTE: hv_sock not available (not a Hyper-V guest?) — Enhanced Session disabled, TCP-only." >&2
fi

# -- Resolve the autologin (desktop) user -----------------------------------
# The C# side substitutes __AUTOLOGIN_USER__ with the gallery item's
# initial username; when blank/absent, fall back to the /etc/passwd scan
# (first UID>=1000 with a home dir), mirroring install_lamco.sh.
AUTOLOGIN_USER="__AUTOLOGIN_USER__"
if [ "$AUTOLOGIN_USER" = "__AUTOLOGIN_USER__" ] || [ -z "$AUTOLOGIN_USER" ]; then
    AUTOLOGIN_USER=""
    AUTOLOGIN_USER="$(awk -F: '$3 >= 1000 && $3 < 65534 && $6 != "" {print $1; exit}' /etc/passwd 2>/dev/null || echo "")"
fi
if [ -z "$AUTOLOGIN_USER" ] || ! id "$AUTOLOGIN_USER" >/dev/null 2>&1; then
    echo "ERROR: could not resolve a desktop user for the wayrdp user unit" >&2
    echo "       (AUTOLOGIN_USER='$AUTOLOGIN_USER')." >&2
    exit 1
fi
echo "Desktop user: $AUTOLOGIN_USER"

# -- systemd USER unit + linger ---------------------------------------------
# wayrdp runs as a USER unit because it must attach to the live Wayland
# session (Wayland socket + hyprctl). Enable linger so the user manager (and
# the unit) starts at boot before any login; bind to graphical-session.target
# so the unit only runs when a Wayland display exists.
UNIT_SRC="/usr/local/lib/systemd/user/wayrdp.service"
if [ ! -f "$UNIT_SRC" ]; then
    echo "ERROR: wayrdp.service not at $UNIT_SRC after install." >&2
    exit 1
fi

loginctl enable-linger "$AUTOLOGIN_USER" 2>/dev/null || true
USER_UID="$(id -u "$AUTOLOGIN_USER")"
run_as_user() {
    sudo -u "$AUTOLOGIN_USER" XDG_RUNTIME_DIR="/run/user/$USER_UID" "$@"
}

# -- TCP listener bind -------------------------------------------------------
# Default TCP bind is loopback-only, which vmconnect (vsock) does not need
# but mstsc from the host absolutely does — a loopback-only 3389 presents as
# the generic mstsc 0x204 "can't connect" error. Listen on all interfaces;
# the firewall section above owns 3389 reachability.
USER_HOME="$(getent passwd "$AUTOLOGIN_USER" | cut -d: -f6)"
AUTO_DROPIN_DIR="$USER_HOME/.config/systemd/user/wayrdp.service.d"
mkdir -p "$AUTO_DROPIN_DIR" 2>/dev/null || true
cat > "$AUTO_DROPIN_DIR/tcp.conf" <<'TCP_EOF'
# Bind the RDP TCP listener on all interfaces (loopback-only by default) so
# mstsc from the Hyper-V host can reach the guest. vsock is unaffected.
[Service]
Environment=WAYRDP_TCP=1
Environment=WAYRDP_BIND=0.0.0.0
TCP_EOF
chown -R "$AUTOLOGIN_USER":"$AUTOLOGIN_USER" "$AUTO_DROPIN_DIR" 2>/dev/null || true
echo "TCP bind drop-in written ($AUTO_DROPIN_DIR/tcp.conf)."

# -- NLA SAM seed ------------------------------------------------------------
# wayrdp refuses to start when the SAM is missing/empty: "no usable NLA
# credentials", unit restart-loops, 3389 never listens (observed on a cold
# deploy). Seed it once from the gallery item's initial password so the first
# RDP connect accepts the console creds. The password arrives base64-encoded
# from the C# side; if the placeholder survives there is nothing to seed and
# the readiness gate below fails loudly. Write via a temp file + chmod 600 so
# a partial write can never pass as "usable" SAM content on a later run.
WAYRDP_NLA_PASSWORD_B64="__WAYRDP_NLA_PASSWORD_B64__"
SAM_FILE="$USER_HOME/.local/share/wayrdp/SAM"
# Note: the comparison placeholder is split ("..."_B64__") so the C# string
# substitution cannot replace it — otherwise it always compares equal to the
# seeded value and seeding is silently skipped.
if [ "$WAYRDP_NLA_PASSWORD_B64" != "__WAYRDP_NLA_PASSWORD""_B64__" ] && ! run_as_user test -s "$SAM_FILE" 2>/dev/null; then
    NLA_PW="$(printf '%s' "$WAYRDP_NLA_PASSWORD_B64" | base64 -d 2>/dev/null || echo "")"
    if [ -n "$NLA_PW" ] && command -v winpr-hash3 >/dev/null 2>&1; then
        mkdir -p "$(dirname "$SAM_FILE")" && chown "$AUTOLOGIN_USER":"$AUTOLOGIN_USER" "$(dirname "$SAM_FILE")" 2>/dev/null || true
        SAM_TMP="$(dirname "$SAM_FILE")/.SAM.seed.$$"
        if run_as_user winpr-hash3 -u "$AUTOLOGIN_USER" -p "$NLA_PW" -f sam > "$SAM_TMP" 2>/dev/null && [ -s "$SAM_TMP" ]; then
            cp "$SAM_TMP" "$SAM_FILE" && chmod 600 "$SAM_FILE" 2>/dev/null || true
            chown "$AUTOLOGIN_USER":"$AUTOLOGIN_USER" "$SAM_FILE" 2>/dev/null || true
            echo "NLA SAM seeded for '$AUTOLOGIN_USER'."
        else
            echo "NOTE: winpr-hash3 SAM generation failed — wayrdp will refuse to start until a SAM is provisioned." >&2
        fi
        rm -f "$SAM_TMP" 2>/dev/null || true
    elif [ -n "$NLA_PW" ]; then
        echo "NOTE: winpr-hash3 not installed — cannot seed wayrdp NLA SAM; first RDP connect will fail." >&2
    fi
    unset NLA_PW
fi

run_as_user systemctl --user daemon-reload 2>/dev/null || true
run_as_user systemctl --user enable wayrdp.service 2>/dev/null || true

# graphical-session.target is not guaranteed under Hyprland (Omarchy starts
# wayrdp-class units from an autostart hook because the target may never
# activate). Mirror install_lamco.sh: add an exec_on_start hook to Hyprland's
# autostart.lua so the unit starts with the session.
HYPRLAND_CONF_HOME="$(getent passwd "$AUTOLOGIN_USER" | cut -d: -f6)/.config/hypr"
if [ -d "$HYPRLAND_CONF_HOME" ] || command -v hyprctl >/dev/null 2>&1; then
    AUTO_LUA="$HYPRLAND_CONF_HOME/autostart.lua"
    mkdir -p "$HYPRLAND_CONF_HOME" 2>/dev/null || true
    if ! grep -q "wayrdp.service" "$AUTO_LUA" 2>/dev/null; then
        cat >> "$AUTO_LUA" <<'AUTO_LUA_EOF'
-- wayrdp autostart (added by VMCreate install_wayrdp.sh): Hyprland may not
-- raise graphical-session.target, so start the wayrdp user unit directly.
-- Race guard: exec_on_start fires before Hyprland owns the Wayland socket,
-- and wayrdp's backend has no retry — a pump started too early fails with
-- "Could not find wayland compositor" and the peer gets a black screen until
-- the unit is restarted. Poll for the socket first.
o.exec_on_start("dbus-update-activation-environment --systemd WAYLAND_DISPLAY XDG_CURRENT_DESKTOP HYPRLAND_INSTANCE_SIGNATURE")
o.exec_on_start("sh -c 'for i in $(seq 1 40); do [ -S \"$XDG_RUNTIME_DIR/wayland-1\" ] || [ -S \"$XDG_RUNTIME_DIR/wayland-0\" ] && break; sleep 0.25; done; systemctl --user start wayrdp.service'")
AUTO_LUA_EOF
        chown "$AUTOLOGIN_USER":"$AUTOLOGIN_USER" "$AUTO_LUA" 2>/dev/null || true
        echo "Wrote wayrdp autostart hook to $AUTO_LUA."
    fi
fi

# -- Defang Omarchy start-hyprland's silent --safe-mode fallback -----------
# Omarchy's /usr/bin/start-hyprland forks Hyprland behind a watchdog pipe and,
# on an UNCLEAN exit ("Hyprland exit not-cleanly, restarting"), relaunches it
# with `--safe-mode`. In safe mode Hyprland's getMainConfigPath() short-circuits
# to <instancePath>/recoverycfg.lua and never reads the real hyprland.lua — when
# that file is absent the compositor falls back to a generated
# `hl.config({ autogenerated = true })` and shows the "autogenerated config"
# error overlay with the wayrdp/plugin hooks never loaded (black vmconnect).
# A single transient boot-time crash (e.g. the <=0.56 devicesRequest null-deref
# triggered by an early hyprctl) therefore wedges the whole session into the
# recovery fallback. Make the boot path deterministic: replace the binary with a
# wrapper that relaunches with the REAL config, never --safe-mode, retries
# FOREVER with capped backoff (a bounded give-up leaves zero Hyprland running =
# permanent black vmconnect until manual SSH intervention, which is worse than
# a retrying log loop). Original is preserved at start-hyprland.real.
START_HL="/usr/bin/start-hyprland"
if [ -f "$START_HL" ] && ! grep -q "VMC_DETERMINISTIC_WRAPPER" "$START_HL" 2>/dev/null; then
    if [ ! -f "${START_HL}.real" ]; then
        cp -a "$START_HL" "${START_HL}.real" 2>/dev/null || true
    fi
    cat > "$START_HL" <<'START_HL_EOF'
#!/bin/bash
# VMC_DETERMINISTIC_WRAPPER — VMCreate install_wayrdp.sh
# Drop-in for Omarchy's start-hyprland. Launches Hyprland with the args passed
# through after `--` (sddm greeter passes `--config <greeter.lua>`; the session
# entry passes none). On an unclean exit it does NOT relaunch with --safe-mode:
# that silent fallback hijacks the config path to recoverycfg.lua and was the
# cause of the autogenerated-config + black-RDP boot bug. Instead it relaunches
# the real config, bounded, and fails loud.
set -u
PASS=("$@")
# Strip our own leading `--` separator (sddm: start-hyprland -- --config ...)
[ "${1:-}" = "--" ] && PASS=("${@:2}")
HYPRLAND_BIN="$(command -v Hyprland || echo /usr/bin/Hyprland)"
LOG="$HOME/.cache/hyprland/start-hyprland.wrapper.log"
mkdir -p "$(dirname "$LOG")" 2>/dev/null || LOG=/dev/null
echo "[$(date -Is)] start-hyprland(VMCreate): launch '$HYPRLAND_BIN ${PASS[*]}'" >>"$LOG" 2>/dev/null
# Never leave a dead desktop: retry FOREVER with capped exponential backoff.
# The previous bounded give-up was the second half of the crash-loop bug —
# a transient boot-time 0.56.2 devicesRequest SEGV plus wayrdp's IPC storm
# killed compositor after compositor, and after strike 3 there was NO
# Hyprland at all, so vmconnect black-screened until manual SSH + sddm
# restart. A user-visible permanent black screen is worse than a log loop.
attempt=0
backoff=1
while true; do
    attempt=$((attempt + 1))
    "$HYPRLAND_BIN" "${PASS[@]}"
    rc=$?
    echo "[$(date -Is)] Hyprland exited rc=$rc (attempt $attempt, next in ${backoff}s)" >>"$LOG" 2>/dev/null
    # Clean exit (rc 0) -> user logged out: done. Anything else -> relaunch
    # the REAL config after backoff. SIGABRT (134) = Hyprland saved a crash
    # report; SIGKILL (137) = wrapper/sddm teardown racing us — still retry.
    [ $rc -eq 0 ] && exit 0
    sleep $backoff
    backoff=$(( backoff < 15 ? backoff * 2 : 15 ))
done
START_HL_EOF
    chmod 755 "$START_HL"
    echo "Installed deterministic start-hyprland wrapper (no --safe-mode fallback)."
else
    echo "start-hyprland already a VMCreate deterministic wrapper (or absent); skipping."
fi

# Start it now if a Wayland session is already up; otherwise the autostart
# hook / graphical-session.target brings it up on next login.
WAYLAND_UP="$(run_as_user systemctl --user is-active graphical-session.target 2>/dev/null || true)"
if [ "$WAYLAND_UP" = "active" ] || [ -S "/run/user/$USER_UID/wayland-1" ] || [ -S "/run/user/$USER_UID/wayland-0" ]; then
    run_as_user systemctl --user start wayrdp.service 2>/dev/null || true
    echo "Started wayrdp.service (Wayland session present)."
else
    echo "NOTICE: no active Wayland session yet; wayrdp.service starts on login."
fi

# -- Readiness gate: installed is NOT the same as listening ------------------
# Give the unit a moment, then check it is active. A unit that immediately
# fails is a real failure, not a degraded install.
WAYRDP_ACTIVE="$(run_as_user systemctl --user is-active wayrdp.service 2>/dev/null || true)"
if [ "$WAYRDP_ACTIVE" = "active" ]; then
    echo "wayrdp.service is active."
elif [ "$WAYRDP_ACTIVE" = "inactive" ] || [ -z "$WAYRDP_ACTIVE" ]; then
    # No session yet — unit is enabled + lingered + autostart-hooked, so it
    # comes up on login. That is a usable (degraded) state, not a failure.
    DEGRADED=1
    echo "WAYRDP DEGRADED: wayrdp.service not active yet (no live Wayland"
    echo "session); enabled + lingered + autostart-hooked, starts on login."
else
    echo "ERROR: wayrdp.service state '$WAYRDP_ACTIVE' after start attempt." >&2
    run_as_user systemctl --user status wayrdp.service --no-pager -l 2>&1 | tail -20 >&2 || true
    exit 1
fi

# -- Result contract (terminal line) -----------------------------------------
# Cleanup: leave no root-owned guest script/tarball behind. The tarball +
# sha256 sidecar are consumed; remove them.
rm -f "$WAYRDP_PKG_TARBALL" "$WAYRDP_PKG_SHA256_FILE" 2>/dev/null || true

if [ "${DEGRADED:-0}" = "1" ]; then
    echo "WAYRDP_RESULT=degraded"
    exit 0
fi
echo "WAYRDP_RESULT=ok"
exit 0

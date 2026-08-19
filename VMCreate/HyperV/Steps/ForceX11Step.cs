using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Forces the X11 display server on the guest VM by installing X11 runtime
    /// prerequisites (dbus-x11, Xwrapper.config), restoring previously disabled
    /// Wayland sessions, detecting available X11 sessions, and configuring SDDM,
    /// LightDM, and GDM to use X11.
    /// <para>
    /// Wayland on Hyper-V is unstable and can cause crashes or blank screens
    /// because the <c>hyperv_drm</c> driver (the synthetic graphics driver
    /// in Linux guests) has limited and incomplete atomic modesetting support.
    /// Wayland requires atomic modesetting for its rendering pipeline, so
    /// compositors that attempt to use it on Hyper-V may crash, freeze, or
    /// produce a blank display. X11 does not require atomic modesetting and
    /// works reliably with the <c>hyperv_drm</c> framebuffer.
    /// </para>
    /// <para>
    /// <b>Ordering is critical:</b> This step restores previously disabled
    /// Wayland session files first (so LightDM can resolve session names),
    /// then writes display manager configs that pin X11 as the default.
    /// Subsequent steps handle xrdp, KWin, AccountsService, Wayland disabling,
    /// and hyperv-daemons installation.
    /// </para>
    /// <para>
    /// This step is a safe no-op when no Wayland sessions exist.
    /// </para>
    /// <para>
    /// LightDM autologin is gated on <see cref="VmCustomizations.ConfigureXrdp"/>:
    /// when xRDP/Enhanced Session is enabled, autologin is omitted so display :0
    /// sits at the LightDM greeter and xRDP owns the user's session on :10,
    /// matching the autologin-disable in <c>install_xrdp.sh</c> and avoiding the
    /// dual-session D-Bus reboot hang. When xRDP is disabled, autologin is kept
    /// so the headless Hyper-V console does not hang at the greeter.
    /// </para>
    /// <para>
    /// Runs at Order 240, after <see cref="RemoveVmwareToolsStep"/> (230)
    /// and before <see cref="FixXrdpStep"/> (245).
    /// </para>
    /// </summary>
    public class ForceX11Step : ICustomizationStep
    {
        public string Name => "Force X11 Display Server";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 240;
        public string? ProgressPhaseId => "Sub_ForceX11";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations?.RdpBackend != RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Forcing X11 display server on VM {VMName}", shell.VmName);

            // Deploy the script, then execute it. This avoids all shell
            // quoting issues from nested bash -c layers in the SSH pipeline.
            // Normalize to LF -- CopyContentAsync base64-encodes the string as-is,
            // and the C# verbatim literal contains Windows CRLF line endings.
            //
            // Gate LightDM autologin on whether xRDP/Enhanced Session is enabled.
            // When ConfigureXrdp is true, the ISO installs xRDP (install_xrdp.sh)
            // which deliberately disables display-manager autologin to avoid a
            // dual-session D-Bus conflict: with autologin on, LightDM logs the
            // user into display :0 while xRDP opens a session on :10, and both
            // share one D-Bus user bus. Clicking Reboot from the xRDP desktop
            // then kills Plasma across both displays while systemd-logind
            // refuses to reboot because the LightDM session is still active --
            // black screen with cursor, reboot never happens. With autologin off,
            // :0 sits at the greeter and the xRDP session is the only active one.
            // When ConfigureXrdp is false, the Hyper-V console is the access
            // path, so keep autologin to avoid a hung greeter on a headless box.
            bool enableAutologin = !customizations.ConfigureXrdp;
            string script = X11Script.Replace("\r\n", "\n").Replace("__ENABLE_AUTOLOGIN__", enableAutologin ? "1" : "0");
            await shell.CopyContentAsync(script, "/tmp/force_x11.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/force_x11.sh && sudo rm -f /tmp/force_x11.sh", ct);

            logger.LogInformation("X11 enforcement result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }

        private const string X11Script = @"#!/bin/bash
set -o pipefail

# -- Install dbus-x11 (required for X11 desktop sessions) ------------------
# dbus-x11 provides dbus-launch, which every X11 desktop session needs to
# start a D-Bus session bus.  Without it, startplasma-x11, gnome-session,
# and other X11 sessions silently fail.  This is a deterministic failure on
# fresh installs where the distro shipped Wayland and never pulled in dbus-x11.
if ! dpkg -l dbus-x11 >/dev/null 2>&1; then
    apt-get update
    apt-get install -y dbus-x11
fi

# -- Fix Xwrapper.config (allow non-console Xorg wrapper launches) ---------
# If anything routes through the Xorg wrapper launcher, restrictive defaults
# will block non-console users.  Allow anybody so X11 sessions started by
# display managers or xrdp can launch X.
if [ -d /etc/X11 ]; then
    if [ -f /etc/X11/Xwrapper.config ]; then
        cp /etc/X11/Xwrapper.config /etc/X11/Xwrapper.config.bak
    fi
    echo ""allowed_users=anybody"" > /etc/X11/Xwrapper.config
fi

# =========================================================================
# CRITICAL ORDERING NOTE
# =========================================================================
# LightDM caches the last-used session name.  If a previous run disabled a
# Wayland session file (e.g. plasma.desktop -> plasma.desktop.disabled),
# LightDM may try to start the cached session which no longer exists.
#
# Therefore the correct sequence is:
#   1. INSTALL X11 runtime prerequisites (dbus-x11, Xwrapper.config)
#   2. RESTORE any previously disabled Wayland session files
#   3. DETECT available X11 sessions
#   4. WRITE display manager configs that pin X11 as the default
#   5. (FixXrdpStep handles xrdp xorg.conf and startwm.sh)
#   5. (DisableKwinCompositingStep handles kwinrc for all users)
#   6. (FixAccountsServiceStep handles XSession overrides)
#   7. (DisableWaylandSessionsStep disables Wayland session files)
#   8. (InstallHypervDaemonsStep installs HV integration and regenerates initramfs)
# =========================================================================

# -- Step 1: Restore previously disabled Wayland sessions -----------------
# If a prior run moved .desktop files to the disabled/ directory, move them
# back so that display managers can resolve session names during config read.
# This is essential for LightDM, which may have a stale cached session name.
mkdir -p /usr/share/wayland-sessions/disabled 2>/dev/null || true
restored=0
for f in /usr/share/wayland-sessions/disabled/*.desktop; do
    [ -f ""$f"" ] || continue
    base=$(basename ""$f"")
    mv ""$f"" ""/usr/share/wayland-sessions/$base"" 2>/dev/null || true
    restored=1
done
[ $restored -eq 1 ] && echo ""Restored previously disabled Wayland sessions"" || echo ""No previously disabled Wayland sessions to restore""

# Also restore any .desktop.disabled files left by older scripts that
# renamed plasma.desktop -> plasma.desktop.disabled instead of moving to a
# subdirectory.
for f in /usr/share/wayland-sessions/*.desktop.disabled; do
    [ -f ""$f"" ] || continue
    original=""${f%.disabled}""
    mv ""$f"" ""$original"" 2>/dev/null || true
    restored=1
done

# -- Step 2: Detect available X11 sessions ----------------------------------
# Used to set the default session for SDDM and LightDM.
x11_session=""
for xs in /usr/share/xsessions/*.desktop; do
    [ -f ""$xs"" ] || continue
    x11_session=$(basename ""$xs"" .desktop)
    break
done
if [ -n ""$x11_session"" ]; then
    echo ""Detected X11 session: $x11_session""
else
    echo ""No X11 sessions found -- display managers will use their defaults""
fi

# -- Step 3: Configure SDDM (KDE/Plasma) ---------------------------------
mkdir -p /etc/sddm.conf.d
cat > /etc/sddm.conf.d/force-x11.conf << SDDM_EOF
[General]
DisplayServer=x11
SDDM_EOF
if [ -n ""$x11_session"" ]; then
    echo ""Session=$x11_session"" >> /etc/sddm.conf.d/force-x11.conf
fi
echo ""SDDM configured for X11""

# -- Step 4: Configure LightDM (XFCE/Mate/Cinnamon/Plasma) -----------------
# Uses [Seat:*] which is the modern equivalent of [SeatDefaults].
# The 91- prefix ensures it sorts after any distro-provided drop-in configs.
#
# autologin-user is set to the default 'user' account ONLY when xRDP/Enhanced
# Session is disabled (__ENABLE_AUTOLOGIN__=1). When xRDP is enabled
# (__ENABLE_AUTOLOGIN__=0), autologin is omitted so display :0 sits at the
# LightDM greeter while xRDP owns the user's session on :10 -- this matches
# install_xrdp.sh's deliberate autologin-disable and avoids the dual-session
# D-Bus reboot hang (see the Phase 2 rationale in the xrdp-display-conflict
# memory note). LightDM doesn't use Wayland by default, so this drop-in only
# pins the X11 session and (optionally) the autologin account.
mkdir -p /etc/lightdm/lightdm.conf.d
if [ -n ""$x11_session"" ]; then
    if [ ""__ENABLE_AUTOLOGIN__"" = ""1"" ]; then
        cat > /etc/lightdm/lightdm.conf.d/91-hyperv-x11.conf << LIGHTDM_EOF
[Seat:*]
user-session=$x11_session
autologin-user=user
autologin-user-timeout=0
LIGHTDM_EOF
        echo ""LightDM configured for X11 session with autologin: $x11_session""
    else
        cat > /etc/lightdm/lightdm.conf.d/91-hyperv-x11.conf << LIGHTDM_EOF
[Seat:*]
user-session=$x11_session
LIGHTDM_EOF
        echo ""LightDM configured for X11 session (autologin disabled for xRDP): $x11_session""
    fi
else
    # No X11 session detected -- write a minimal config that at least
    # avoids Wayland (LightDM doesn't use Wayland by default anyway).
    if [ ""__ENABLE_AUTOLOGIN__"" = ""1"" ]; then
        cat > /etc/lightdm/lightdm.conf.d/91-hyperv-x11.conf << LIGHTDM_FALLBACK_EOF
[Seat:*]
autologin-user=user
autologin-user-timeout=0
LIGHTDM_FALLBACK_EOF
        echo ""LightDM drop-in created with autologin (no specific X11 session detected)""
    else
        cat > /etc/lightdm/lightdm.conf.d/91-hyperv-x11.conf << LIGHTDM_FALLBACK_EOF
[Seat:*]
LIGHTDM_FALLBACK_EOF
        echo ""LightDM drop-in created (no specific X11 session, autologin disabled for xRDP)""
    fi
fi

# -- Step 5: Configure GDM (GNOME/Fedora/Ubuntu) ---------------------------
# Set WaylandEnable=false in the GDM custom.conf.
# Handle both /etc/gdm3/custom.conf (newer) and /etc/gdm/custom.conf (older).
for gdm_conf in /etc/gdm3/custom.conf /etc/gdm/custom.conf; do
    gdm_dir=$(dirname ""$gdm_conf"")
    mkdir -p ""$gdm_dir"" 2>/dev/null || true

    if [ -f ""$gdm_conf"" ]; then
        # File exists -- ensure WaylandEnable=false is in the [daemon] section
        if grep -q '^WaylandEnable' ""$gdm_conf"" 2>/dev/null; then
            sed -i 's/^WaylandEnable=.*/WaylandEnable=false/' ""$gdm_conf"" 2>/dev/null || true
        elif grep -q '^\[daemon\]' ""$gdm_conf"" 2>/dev/null; then
            sed -i '/^\[daemon\]/a WaylandEnable=false' ""$gdm_conf"" 2>/dev/null || true
        else
            # No [daemon] section -- append it
            printf '\n[daemon]\nWaylandEnable=false\n' >> ""$gdm_conf""
        fi
    else
        # File doesn't exist -- create it with the minimal content
        cat > ""$gdm_conf"" << GDM_EOF
[daemon]
WaylandEnable=false

[security]

[xdmcp]

[chooser]

[debug]
GDM_EOF
    fi
    echo ""GDM configured for X11 at $gdm_conf""
done

echo ""=== X11 enforcement complete ===""
exit 0
";
    }
}

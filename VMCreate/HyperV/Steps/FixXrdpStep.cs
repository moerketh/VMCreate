using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Fixes xrdp configuration for Hyper-V by commenting out DRM options in
    /// xorg.conf and writing a startwm.sh that forces X11 with software rendering.
    /// <para>
    /// hyperv_drm does not expose a DRI render node, so xrdp sessions that load
    /// the xorgxrdp module will fail to start if DRMDevice, DRI3, or DRMAllowList
    /// lines are present.  The startwm.sh sets environment variables that force
    /// X11 rendering (KWIN_COMPOSE=N, QT_QUICK_BACKEND=software, etc.) and
    /// detects the appropriate desktop session at login time.
    /// </para>
    /// <para>
    /// Safe no-op when xrdp is not installed.
    /// </para>
    /// <para>
    /// Runs at Order 245, after <see cref="ForceX11Step"/> (240) and before
    /// <see cref="DisableKwinCompositingStep"/> (250).
    /// </para>
    /// </summary>
    public class FixXrdpStep : ICustomizationStep
    {
        public string Name => "Fix xrdp for Hyper-V";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 245;
        public string? ProgressPhaseId => "Sub_FixXrdp";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations?.RdpBackend != RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            logger.LogInformation("Fixing xrdp configuration for Hyper-V on VM {VMName}", shell.VmName);

            string script = XrdpScript.Replace("\r\n", "\n");
            await shell.CopyContentAsync(script, "/tmp/fix_xrdp.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/fix_xrdp.sh && sudo rm -f /tmp/fix_xrdp.sh", ct);

            logger.LogInformation("xrdp fix result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }

        private const string XrdpScript = @"#!/bin/bash
set -o pipefail

# -- Fix xrdp xorg.conf (comment out DRM lines) --------------------------
# hyperv_drm does not expose a DRI render node (/dev/dri/renderD128), so
# xrdp sessions that load the xorgxrdp module will fail to start if the
# DRMDevice line is present.  DRI3 and DRMAllowList also reference DRM
# devices that don't exist on Hyper-V.  Comment out all three lines so xrdp
# falls back to software rendering, which works reliably on Hyper-V.
# Note: Load ""glamoregl"" and Load ""xorgxrdp"" are kept -- xorgxrdp.so links
# against glamoregl and removing it would break the module load chain.
if [ -f /etc/X11/xrdp/xorg.conf ]; then
    cp /etc/X11/xrdp/xorg.conf /etc/X11/xrdp/xorg.conf.bak
    sed -i 's|^\([\t ]*Option[\t ]*""DRMDevice"".*\)|# \1  # hyperv_drm has no render node|' /etc/X11/xrdp/xorg.conf 2>/dev/null || true
    sed -i 's|^\([\t ]*Option[\t ]*""DRI3"".*\)|# \1  # no DRI support on hyperv_drm|' /etc/X11/xrdp/xorg.conf 2>/dev/null || true
    sed -i 's|^\([\t ]*Option[\t ]*""DRMAllowList"".*\)|# \1  # no DRM devices to allow|' /etc/X11/xrdp/xorg.conf 2>/dev/null || true
    echo ""xrdp xorg.conf: commented out DRMDevice, DRI3, DRMAllowList (backup at .bak)""
else
    echo ""No xrdp xorg.conf found -- skipping""
fi

# -- Write xrdp startwm.sh with X11 environment ----------------------------
# xrdp sessions have a minimal PATH and no D-Bus session, which causes
# ""command not found"" errors for dbus-launch and other tools.  The default
# startwm.sh may also not set XDG_SESSION_TYPE=x11, causing toolkits to
# attempt Wayland rendering.  We write a startwm.sh that:
#   - Sources /etc/profile and ~/.profile for a sane environment
#   - Sets LANG/LC_ALL (xrdp sessions often have no locale)
#   - Forces XDG_SESSION_TYPE=x11 so toolkits use X11
#   - Disables KWin compositing via KWIN_COMPOSE=N
#   - Forces Qt software rendering (QT_QUICK_BACKEND=software,
#     QSG_RENDER_LOOP=basic) which is required for xrdp's software renderer
#   - Writes ~/.config/kwinrc per-session (handles new users)
#   - Removes cached KWin output config that may reference invalid modes
#   - Uses /usr/bin/dbus-launch (xrdp's PATH may not include /usr/bin)
#   - Detects and exec's the appropriate X11 desktop session
if [ -d /etc/xrdp ]; then
    if [ -f /etc/xrdp/startwm.sh ]; then
        cp /etc/xrdp/startwm.sh /etc/xrdp/startwm.sh.bak
    fi
    cat > /etc/xrdp/startwm.sh << 'STARTWM_EOF'
#!/bin/sh
# xrdp session startup -- forced X11 with software rendering for Hyper-V
if test -r /etc/profile; then . /etc/profile; fi
if test -r ~/.profile; then . ~/.profile; fi

export LANG=en_US.UTF-8
export LC_ALL=en_US.UTF-8
export XDG_SESSION_TYPE=x11
export KWIN_COMPOSE=N
export QT_QUICK_BACKEND=software
export QSG_RENDER_LOOP=basic

# Write per-session kwinrc to disable compositing (handles new users too)
mkdir -p ~/.config
cat > ~/.config/kwinrc << 'KRC'
[Compositing]
Backend=XRender
Enabled=false
OpenGLIsUnsafe=true
KRC
rm -f ~/.config/kwinoutputconfig.json

# Detect and start the appropriate X11 desktop session
if command -v startplasma-x11 >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=KDE
    export XDG_CURRENT_DESKTOP=KDE
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec /usr/bin/startplasma-x11
elif command -v startxfce4 >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=XFCE
    export XDG_CURRENT_DESKTOP=XFCE
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec startxfce4
elif command -v gnome-session >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=GNOME
    export XDG_CURRENT_DESKTOP=GNOME
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec gnome-session
elif command -v cinnamon-session >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=CINNAMON
    export XDG_CURRENT_DESKTOP=Cinnamon
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec cinnamon-session
elif command -v mate-session >/dev/null 2>&1; then
    export XDG_SESSION_DESKTOP=MATE
    export XDG_CURRENT_DESKTOP=MATE
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec mate-session
else
    # Fallback: try whatever X11 session was detected, or just start X
    eval $(/usr/bin/dbus-launch --sh-syntax --exit-with-session)
    exec xterm
fi
STARTWM_EOF
    chmod +x /etc/xrdp/startwm.sh
    echo ""xrdp startwm.sh written with X11 environment (backup at .bak)""
else
    echo ""xrdp not installed -- skipping startwm.sh""
fi

echo ""=== xrdp fix complete ===""
exit 0
";
    }
}

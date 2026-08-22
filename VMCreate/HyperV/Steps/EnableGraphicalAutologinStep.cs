using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Enables graphical autologin on the guest VM while keeping Wayland enabled,
    /// so the Lamco RDP Server (which shares an existing Wayland session) has a
    /// live desktop session to attach to at boot.
    /// <para>
    /// This is the Lamco counterpart to the autologin handling in
    /// <see cref="ForceX11Step"/>: when xrdp is selected, autologin is deliberately
    /// disabled so display :0 sits at the greeter and xrdp owns the session on
    /// :10 (avoiding a dual-session D-Bus reboot hang). Lamco, by contrast, shares
    /// the user's own Wayland session via XDG Desktop Portal + PipeWire, so it
    /// needs the user logged in at the console — hence autologin ON.
    /// </para>
    /// <para>
    /// Detects the active display manager at runtime (GDM, SDDM, or LightDM) and
    /// configures autologin for the VM's initial user
    /// (<see cref="GalleryItem.InitialUsername"/>), selecting a Wayland session
    /// from <c>/usr/share/wayland-sessions/*.desktop</c>. Does not touch
    /// <c>WaylandEnable</c> (Wayland stays on, unlike the xrdp path).
    /// </para>
    /// <para>
    /// Also adds the user to the <c>render</c> and <c>video</c> groups so PipeWire
    /// and VA-API hardware encoding can access GPU devices, and enables
    /// <c>loginctl enable-linger</c> so the user's systemd services (including the
    /// Lamco user unit) start at boot without an interactive SSH login.
    /// </para>
    /// <para>
    /// Runs at Order 238, after <see cref="InstallLamcoRdpStep"/> (235) and before
    /// the xrdp block (240-270, which is skipped for Lamco). Safe no-op when no
    /// display manager or Wayland session is present.
    /// </para>
    /// </summary>
    public class EnableGraphicalAutologinStep : ICustomizationStep
    {
        public string Name => "Enable Graphical Autologin (Wayland)";
        public CustomizationPhase Phase => CustomizationPhase.PostBoot;
        public StepPlatform Platform => StepPlatform.Linux;
        public int Order => 238;
        public string? ProgressPhaseId => "Sub_EnableAutologin";

        public bool IsApplicable(GalleryItem item, VmCustomizations customizations)
            => customizations.RdpBackend == RdpBackend.Lamco;

        public async Task ExecuteAsync(IGuestShell shell, GalleryItem item, VmCustomizations customizations, ILogger logger, CancellationToken ct)
        {
            var autologinUser = !string.IsNullOrWhiteSpace(item?.InitialUsername) ? item.InitialUsername : "root";
            logger.LogInformation("Enabling graphical Wayland autologin for user '{User}' on VM {VMName}", autologinUser, shell.VmName);

            string script = AutologinScript
                .Replace("\r\n", "\n")
                .Replace("__AUTOLOGIN_USER__", autologinUser);

            await shell.CopyContentAsync(script, "/tmp/enable_autologin.sh", ct);

            string result = await shell.RunCommandAsync(
                "sudo bash /tmp/enable_autologin.sh && sudo rm -f /tmp/enable_autologin.sh", ct);

            logger.LogInformation("Graphical autologin result on VM {VMName}: {Result}", shell.VmName, result.Trim());
        }

        private const string AutologinScript = @"#!/bin/bash
set -o pipefail

USER=""__AUTOLOGIN_USER__""
mkdir -p /etc/sddm.conf.d /etc/lightdm/lightdm.conf.d /etc/gdm3 /etc/gdm

# -- Detect an available Wayland session name -------------------------------
# Pick the first available wayland-sessions/*.desktop. Prefer plasma/mutter
# (GNOME/KDE) names, fall back to whatever exists. Do NOT disable or move any
# session files — Lamco needs Wayland enabled.
wayland_session=""""
for f in /usr/share/wayland-sessions/*.desktop; do
    [ -f ""$f"" ] || continue
    base=$(basename ""$f"" .desktop)
    wayland_session=""$base""
    # Preference order: plasma, plasmax11 is X11 so skip; prefer 'plasma' then 'gnome' then 'mutter'
    case ""$base"" in
        plasma|gnome|mutter) wayland_session=""$base""; break ;;
    esac
done

if [ -z ""$wayland_session"" ]; then
    echo ""No /usr/share/wayland-sessions/*.desktop found — Wayland may not be installed.""
    echo ""Autologin configured but no Wayland session to select.""
    # Fall back to the display manager default rather than failing.
    wayland_session=""""
fi

echo ""Selected Wayland session: ${wayland_session:-(dm default)}""

# -- Add the user to render/video groups for PipeWire + GPU access ---------
# Best-effort: groups may not exist on minimal installs.
getent group render >/dev/null 2>&1 && usermod -aG render ""$USER"" 2>/dev/null || true
getent group video  >/dev/null 2>&1 && usermod -aG video  ""$USER"" 2>/dev/null || true
getent group audio  >/dev/null 2>&1 && usermod -aG audio  ""$USER"" 2>/dev/null || true

# -- Enable linger so the user's systemd services run at boot --------------
loginctl enable-linger ""$USER"" 2>/dev/null || true

# -- Detect active display manager -----------------------------------------
dm=""""
if [ -f /etc/X11/default-display-manager ]; then
    dm=$(cat /etc/X11/default-display-manager 2>/dev/null)
fi
if [ -z ""$dm"" ] && command -v systemctl >/dev/null 2>&1; then
    dm=$(systemctl cat display-manager.service 2>/dev/null | head -1 | sed -n 's/.*-\([^ @]*\).service.*/\1/p')
fi
# Normalize: trim path, lowercase
dm=$(basename ""$dm"" 2>/dev/null | tr '[:upper:]' '[:lower:]')

echo ""Detected display manager: ${dm:-(none)}""

# -- GDM (GNOME: Ubuntu, Fedora, Debian-GNOME) -----------------------------
# Enable AutomaticLogin in /etc/gdm3/custom.conf or /etc/gdm/custom.conf.
# Do NOT set WaylandEnable=false — Lamco requires Wayland.
for conf in /etc/gdm3/custom.conf /etc/gdm/custom.conf; do
    if [ -f ""$conf"" ]; then
        if ! grep -q '^\[daemon\]' ""$conf""; then
            printf '\n[daemon]\n' >> ""$conf""
        fi
        # Remove any existing AutomaticLogin/AutomaticLoginEnable lines then append fresh.
        sed -i '/^#\?AutomaticLogin=/d; /^#\?AutomaticLoginEnable=/d' ""$conf""
        sed -i '/^\[daemon\]/a AutomaticLoginEnable=true' ""$conf""
        sed -i '/^\[daemon\]/a AutomaticLogin='""$USER"" ""$conf""
        echo ""Configured GDM autologin in $conf""
    fi
done

# -- SDDM (KDE: openSUSE TW, Parrot-KDE, Debian-KDE) -----------------------
# Build the [Autologin] block conditionally — avoid $(...) inside heredocs
# so the C# verbatim string and bash both parse cleanly.
if [ -d /etc/sddm.conf.d ]; then
    {
        printf '[Autologin]\nUser=%s\n' ""$USER""
        if [ -n ""$wayland_session"" ]; then
            printf 'Session=%s\n' ""$wayland_session""
        fi
    } > /etc/sddm.conf.d/99-lamco-autologin.conf
    echo ""Configured SDDM autologin in /etc/sddm.conf.d/99-lamco-autologin.conf""
fi

# -- LightDM (Parrot, some Debian spins) -----------------------------------
# Parrot historically uses LightDM. Set autologin-user + autologin-session.
if [ -d /etc/lightdm/lightdm.conf.d ]; then
    {
        printf '[Seat:*]\nautologin-user=%s\nautologin-user-timeout=0\n' ""$USER""
        if [ -n ""$wayland_session"" ]; then
            printf 'autologin-session=%s\n' ""$wayland_session""
        fi
    } > /etc/lightdm/lightdm.conf.d/99-lamco-autologin.conf
    # LightDM requires the user to be in the autologin group on some distros
    getent group autologin >/dev/null 2>&1 && usermod -aG autologin ""$USER"" 2>/dev/null || true
    echo ""Configured LightDM autologin in /etc/lightdm/lightdm.conf.d/99-lamco-autologin.conf""
fi

# -- Create monitors.xml to force 1920x1080@60 resolution -----------------
# The hyperv_drm driver reports 1024x768 as the preferred mode. mutter/GNOME
# reads monitors.xml to override the DRM preferred mode. KWin uses KScreen
# config instead but falls through to DRM default without a config file.
USER_HOME=$(getent passwd ""$USER"" | cut -d: -f6)
if [ -n ""$USER_HOME"" ] && [ -d ""$USER_HOME"" ]; then
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
    chown ""$USER"" ""$USER_HOME/.config/monitors.xml"" 2>/dev/null || true
    echo ""Created monitors.xml for 1920x1080@60 resolution.""
fi

# -- Set display resolution via kscreen-doctor for KDE/KWin ---------------
# KWin ignores monitors.xml and uses KScreen config instead. Without a KScreen
# config, KWin falls through to the hyperv_drm default (1024x768).
# kscreen-doctor can set the mode at runtime. We also create a KDE autostart
# script that runs kscreen-doctor after the Wayland session starts, ensuring
# the resolution is set before lamco-rdp-server connects.
if command -v kscreen-doctor >/dev/null 2>&1; then
    mkdir -p ""$USER_HOME/.config/autostart""
    cat > ""$USER_HOME/.config/autostart/kscreen-set-resolution.desktop"" << 'KSCREEN_AUTOSTART_EOF'
[Desktop Entry]
Type=Application
Name=Set Display Resolution
Exec=kscreen-doctor output.1.mode.1920x1080@60
X-KDE-autostart-phase=2
NoDisplay=true
KSCREEN_AUTOSTART_EOF
    chown ""$USER"" ""$USER_HOME/.config/autostart/kscreen-set-resolution.desktop"" 2>/dev/null || true
    echo ""Created KDE autostart script for 1920x1080@60 via kscreen-doctor.""
else
    echo ""kscreen-doctor not found — KWin will use DRM default (1024x768).""
fi

# -- Retired: vgem dummy render node ---------------------------------------
# The RDP capture path is all-software (MemFd buffers, llvmpipe on card0);
# no render node or DMA-BUF is needed. Remove any vgem artifacts left by
# older deployments - vgem racing the real DRM node for the renderD128
# name is worse than having no render node at all. The software-render
# env vars live in InstallLamcoRdpStep (kwin-software-render.sh).
rm -f /etc/modules-load.d/vgem.conf /etc/udev/rules.d/99-vgem-render.rules 2>/dev/null || true

echo ""=== graphical Wayland autologin configured for $USER ===""
exit 0
";
    }
}
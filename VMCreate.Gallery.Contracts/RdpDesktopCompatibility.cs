using System.Collections.Generic;

namespace VMCreate
{
    /// <summary>
    /// The RDP-backend / desktop rules both front ends and the deployment
    /// orchestrator enforce, in one place:
    /// <list type="bullet">
    /// <item>xrdp only on X11-only desktops (<see cref="DesktopEnvironmentOption.XrdpSupported"/>).
    /// Picking KDE or GNOME with xrdp used to run the X11 fixups against the
    /// stock Xfce pre-boot, then install a Wayland-only GNOME post-boot that
    /// xrdp could not serve (Kali, 2026-11-03).</item>
    /// <item>Lamco only on Lamco-capable desktops (Wayland + supported portal).</item>
    /// <item>wayrdp only where <see cref="LinuxDistroExtensions.SupportsWayrdp(GalleryItem)"/>
    /// holds (Omarchy / Hyprland).</item>
    /// </list>
    /// Images without a desktop catalog (Parrot, Ubuntu, …) are not desktop
    /// checked: there xrdp keeps its Force X11 fallback.
    /// </summary>
    public static class RdpDesktopCompatibility
    {
        /// <summary>
        /// Returns why this backend cannot be deployed with the effective
        /// desktop, or null when the combination is valid. The effective
        /// desktop is the pending switch target, or the catalog default when
        /// no switch is pending. Auto and None are always valid.
        /// </summary>
        public static string? Validate(
            GalleryItem? item, RdpBackend backend, DesktopEnvironmentOption? effectiveDesktop)
        {
            if (backend == RdpBackend.Wayrdp && !item.SupportsWayrdp())
                return "wayrdp only supports Hyprland (Omarchy images) at this time — use auto, lamco or xrdp.";

            if (effectiveDesktop == null)
                return null;

            if (backend == RdpBackend.Xrdp && !effectiveDesktop.XrdpSupported)
                return $"xrdp cannot serve {effectiveDesktop.DisplayName}: it runs on Wayland, and xrdp needs an X11 desktop. " +
                       "Use auto or lamco, or pick an X11 desktop.";

            if (backend == RdpBackend.Lamco && !effectiveDesktop.LamcoSupported)
                return $"The Lamco RDP Server cannot serve {effectiveDesktop.DisplayName}: it needs a Wayland desktop. Use auto or xrdp.";

            return null;
        }

        /// <summary>
        /// <see cref="Validate(GalleryItem?, RdpBackend, DesktopEnvironmentOption?)"/>
        /// for a deployment's customizations: resolves the effective desktop
        /// against the item's catalog first. An unknown desktop id is left to
        /// <c>DesktopSwitchStep</c>, which fails loudly on it.
        /// </summary>
        public static string? Validate(
            GalleryItem? item, VmCustomizations customizations, IEnumerable<IDesktopEnvironmentCatalog>? catalogs)
        {
            var catalog = catalogs.ForItem(item);
            var desktop = catalog?.Resolve(customizations.DesktopEnvironmentId);
            return Validate(item, customizations.RdpBackend, desktop);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace VMCreate
{
    /// <summary>
    /// Shared wizard-flow decisions so the Customize step is shown/hidden consistently
    /// across the settings page (navigation), the main window (step indicator), and the
    /// deploy page (phase list).
    /// </summary>
    public static class WizardFlow
    {
        /// <summary>
        /// True when the Customize page should be shown for the selected item — i.e. there is
        /// something to configure: a Windows VM (distribution steps), any distribution-specific
        /// option visible for the item, or a Linux disk-image flow.
        ///
        /// native-Hyper-V / ISO images with nothing to customize skip the page. Note that a
        /// native-Hyper-V Windows image is NOT skipped — it is pre-built (no disk conversion)
        /// yet still needs Windows post-boot customization.
        /// </summary>
        public static bool ShowsCustomizePage(GalleryItem item, IEnumerable<IConfigurableCustomizationStep> steps)
        {
            if (item == null) return false;

            // Windows VMs always have distribution steps (and typically post-boot customization).
            if (item.IsWindows) return true;

            // Any distribution-specific option visible for this item.
            if (steps != null && steps.Any(s => s.IsVisibleFor(item))) return true;

            // Linux disk-image flow (the original behavior): convertible images get the page;
            // pre-built native-Hyper-V images and ISO installers don't.
            bool isIso = string.Equals(item.FileType, "ISO", StringComparison.OrdinalIgnoreCase);
            return !item.IsNativeHyperV && !isIso;
        }
    }
}

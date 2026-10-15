using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VMCreate
{
    /// <summary>
    /// Validates the console resolution before it is substituted into guest
    /// scripts (e.g. <c>__CONSOLE_RESOLUTION__</c> in the Lamco / graphical
    /// autologin provisioning).
    /// <para>
    /// The value originates from user settings (a hand-editable JSON file),
    /// and the substituted tokens flow into root-run bash scripts: sed
    /// replacement text, compositor config heredocs (Hyprland Lua, GNOME
    /// monitors.xml, KDE kscreen-doctor) and log echoes. A hostile or
    /// malformed value must fail validation on the host, never reach the
    /// guest.
    /// </para>
    /// <para>
    /// Validation is deliberately shape-only: the Hyper-V synthetic display
    /// (hyperv_drm) publishes a fixed VESA DMT mode list, and a well-formed
    /// but unsupported mode degrades gracefully in the guest (the compositor
    /// falls back to its preferred 1024x768) instead of failing a deployment.
    /// The host only guarantees the string is a safe
    /// <c>width x height @ rate</c> triple, not that the guest supports it.
    /// </para>
    /// </summary>
    public static partial class ConsoleResolutionValidator
    {
        /// <summary>Default console resolution used when none is configured.</summary>
        public const string Default = "1920x1080@60";

        // ^[1-9]\d{2,4}x[1-9]\d{2,4}@[1-9]\d{1,2}$ — digits and the literal
        // separators 'x' and '@' only, no leading zeros, so no shell
        // metacharacters, quotes, whitespace or path separators can pass by
        // construction. Bounds: 100-99999 px and 10-999 Hz — everything
        // from 1280x720 to 8K ultrawides without blessing modes the
        // guest may not support.
        [GeneratedRegex("^[1-9]\\d{2,4}x[1-9]\\d{2,4}@[1-9]\\d{1,2}$", RegexOptions.CultureInvariant)]
        private static partial Regex ConsoleResolutionRegex();

        /// <summary>
        /// Returns true when the value is a safe console resolution string of
        /// the form <c>&lt;width&gt;x&lt;height&gt;@&lt;rate&gt;</c>
        /// (e.g. <c>1920x1080@60</c>).
        /// </summary>
        public static bool IsValidConsoleResolution(string? resolution)
            => TryParse(resolution, out _, out _, out _);

        /// <summary>
        /// Attempts to parse a console resolution string of the form
        /// <c>&lt;width&gt;x&lt;height&gt;@&lt;rate&gt;</c> into its integer
        /// parts. Returns false for null, empty or malformed values; the
        /// caller must fail loudly rather than substitute an unparsed value
        /// into a root-run script.
        /// </summary>
        public static bool TryParse(string? resolution, out int width, out int height, out int refreshRate)
        {
            width = height = refreshRate = 0;
            if (string.IsNullOrEmpty(resolution) || !ConsoleResolutionRegex().IsMatch(resolution))
                return false;

            int xIndex = resolution.IndexOf('x');
            int atIndex = resolution.IndexOf('@');
            return int.TryParse(resolution[..xIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
                && int.TryParse(resolution[(xIndex + 1)..atIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
                && int.TryParse(resolution[(atIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out refreshRate)
                && width > 0 && height > 0 && refreshRate > 0;
        }

        /// <summary>
        /// Validates <paramref name="resolution"/> and returns the exact
        /// token values the steps substitute into guest scripts: the full
        /// <c>WxH@R</c> mode string, the width and height numbers, and the
        /// three-decimal rate used in GNOME's monitors.xml
        /// (<c>60.000</c>). Callers must fail loudly when this returns
        /// false — never substitute an unvalidated value.
        /// </summary>
        public static bool TryGetTokenValues(string? resolution,
            out string value, out string width, out string height, out string rateXml)
        {
            value = width = height = rateXml = string.Empty;
            if (!TryParse(resolution, out int w, out int h, out int r))
                return false;

            value = FormattableString.Invariant($"{w}x{h}@{r}");
            width = FormattableString.Invariant($"{w}");
            height = FormattableString.Invariant($"{h}");
            rateXml = FormattableString.Invariant($"{r:0.000}");
            return true;
        }
    }
}
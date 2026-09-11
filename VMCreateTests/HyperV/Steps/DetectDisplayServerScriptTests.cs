using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Ships-with contract tests for <c>detect_display_server.sh</c> — the
    /// in-guest probe that decides whether an RDP-Auto deployment lands on
    /// the Wayland-native Lamco server or on xrdp. The script is deployed
    /// to real VMs as root; these tests pin the load-bearing branches of
    /// its detection logic so a regression fails here instead of silently
    /// misrouting every future Auto deployment.
    /// <para>
    /// The <see cref="EmbeddedScriptSyntaxTests"/> lint (bash -n) already
    /// covers syntax; these tests pin CONTENT: the SDDM branch shipped as
    /// a release blocker fix because stock Plasma 6 guests (Parrot KDE,
    /// Debian KDE, Kali KDE) matched nothing in update-alternatives and no
    /// GDM, and fell through to the x11 verdict — losing the Lamco path.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class DetectDisplayServerScriptTests
    {
        private static string LoadScript()
        {
            // Same embedded-resource lookup ScriptResourceLoader performs:
            // resource names are <namespace>.<path>.detect_display_server.sh.
            Assembly assembly = typeof(global::VMCreate.AutoRdpBackendResolveStep).Assembly;
            string resourceName = assembly.GetManifestResourceNames()
                .SingleOrDefault(n => n.EndsWith(".detect_display_server.sh", StringComparison.OrdinalIgnoreCase))
                ?? throw new AssertFailedException("detect_display_server.sh is not embedded in the VMCreate assembly");
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream!);
            return reader.ReadToEnd();
        }

        [TestMethod]
        public void Script_HandlesSddm_ThePlasmaDefaultDisplayManager()
        {
            var script = LoadScript();

            // The SDDM branch exists and is gated on the binary being present
            StringAssert.Contains(script, "command -v sddm",
                "the script must have an SDDM branch — stock Plasma 6 guests skipped " +
                "update-alternatives AND gdm, and fell through to x11, losing the Lamco path");

            // Both SDDM config locations must be consulted (conf.d overrides)
            StringAssert.Contains(script, "/etc/sddm.conf",
                "the SDDM branch must read the main sddm.conf");
            StringAssert.Contains(script, "/etc/sddm.conf.d/*.conf",
                "the SDDM branch must read conf.d dropins — a drop-in overrides the main file");

            // An X11-only pinned session (e.g. plasmax11) keeps the x11
            // verdict; anything else resolves to Wayland.
            StringAssert.Contains(script, "/usr/share/xsessions/$sddm_session.desktop",
                "the SDDM branch must classify a pinned session by checking xsessions vs wayland-sessions");

            // No pin at all ⇒ Plasma 6 SDDM greeter preselects the compiled-in
            // Wayland default — the branch must default to wayland, since
            // section 1 already proved a Wayland session is installed.
            int sddmStart = script.IndexOf("command -v sddm", StringComparison.Ordinal);
            int sddmEnd = script.IndexOf("# -- 4.", StringComparison.Ordinal);
            Assert.IsTrue(sddmStart >= 0 && sddmEnd > sddmStart,
                "cannot locate the SDDM section for the no-pin assertion — " +
                "update the test if the section anchor moved");
            string sddmSection = script[sddmStart..sddmEnd];
            StringAssert.Contains(sddmSection, "verdict=\"wayland\"",
                "the SDDM no-pin path must set verdict=wayland (Plasma 6 default)");
        }

        [TestMethod]
        public void Script_GdmOptOutStillRespected_AfterSddm()
        {
            var script = LoadScript();

            // Ordering contract: SDDM (section 3) must run BEFORE GDM (section 4)
            int sddm = script.IndexOf("command -v sddm", StringComparison.Ordinal);
            int gdm = script.IndexOf("command -v gdm3", StringComparison.Ordinal);
            Assert.IsTrue(sddm >= 0 && gdm > sddm,
                "SDDM handling must precede the GDM special case");
        }
    }
}
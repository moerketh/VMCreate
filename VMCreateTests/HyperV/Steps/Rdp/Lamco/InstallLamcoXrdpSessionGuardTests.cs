using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Guards in install_lamco.sh against a desktop session the stock xrdp
    /// started (live, Kali + KDE: Enhanced Session reached the image's xrdp
    /// mid-deployment; the session outlived the stopped xrdp, and the
    /// installer's kscreen-doctor call waited on its nested KWin forever).
    /// </summary>
    [TestClass]
    public sealed class InstallLamcoXrdpSessionGuardTests
    {
        private static string Script => ScriptResourceLoader.Load("install_lamco.sh");

        [TestMethod]
        public void RetiringXrdp_EndsItsOrphanedSessions()
        {
            string script = Script;
            int retire = script.IndexOf("pkill -x xrdp-sesexec", System.StringComparison.Ordinal);
            int terminate = script.IndexOf("loginctl terminate-session", System.StringComparison.Ordinal);

            Assert.IsTrue(retire >= 0 && terminate > retire,
                "the xrdp sessions are ended right where the stock xrdp is retired");
            StringAssert.Contains(script, "-p Service --value 2>/dev/null)\" = \"xrdp-sesman\"",
                "only sessions xrdp started are ended");
        }

        [TestMethod]
        public void KscreenDoctor_TargetsTheLocalSession_AndIsBounded()
        {
            string script = Script;

            StringAssert.Contains(script, "-p Seat --value 2>/dev/null)\" = \"seat0\"",
                "wayland-0 of another session (an orphaned xrdp login) is not the desktop");
            StringAssert.Contains(script, "timeout 10 sudo -u \"$AUTOLOGIN_USER\" WAYLAND_DISPLAY=wayland-0",
                "kscreen-doctor can never wedge the install");
        }
    }
}

using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Metadata
{
    [TestClass]
    public class LegacyNotesCredentialsTests
    {
        [TestMethod]
        public void TryExtract_VmCreateFormat_ReturnsBothAndEmptyRemainder()
        {
            // Exact format written by PowerShellVmConfigManager.SetVMLoginNotes.
            bool found = LegacyNotesCredentials.TryExtract("Initial Username: user\r\nInitial Password: parrot",
                out var user, out var pass, out var remaining);

            Assert.IsTrue(found);
            Assert.AreEqual("user", user);
            Assert.AreEqual("parrot", pass);
            Assert.AreEqual("", remaining);
        }

        [TestMethod]
        public void TryExtract_KeepsSurroundingText()
        {
            bool found = LegacyNotesCredentials.TryExtract("before\r\nInitial Username: kali\r\nInitial Password: kali\r\nafter",
                out _, out _, out var remaining);

            Assert.IsTrue(found);
            Assert.AreEqual("before\r\nafter", remaining);
        }

        [TestMethod]
        public void TryExtract_NoCredentials_ReturnsFalseAndOriginal()
        {
            Assert.IsFalse(LegacyNotesCredentials.TryExtract("nothing here", out var u, out var p, out var remaining));
            Assert.IsNull(u);
            Assert.IsNull(p);
            Assert.AreEqual("nothing here", remaining);
        }

        [TestMethod]
        public void TryExtract_PasswordWithSpacesAndSymbols_PreservedExactly()
        {
            LegacyNotesCredentials.TryExtract("Initial Username: root\nInitial Password: p@ss w:rd!", out _, out var pass, out _);
            Assert.AreEqual("p@ss w:rd!", pass);
        }
    }
}

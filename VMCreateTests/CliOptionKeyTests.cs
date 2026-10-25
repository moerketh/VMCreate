using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate.CLI.Commands;
using VMCreate.Gallery.distributions;

namespace VMCreate.Tests
{
    /// <summary>
    /// Pins how <c>create --option key=value</c> keys name optional steps.
    /// The help text's own example, <c>--option pwncloudos-sync=true</c>, used
    /// to match nothing (the step is named "PwnCloudOS Sync") and — because
    /// any --option suppresses the PwnCloudOS default — silently turned the
    /// sync off.
    /// </summary>
    [TestClass]
    public sealed class CliOptionKeyTests
    {
        private static readonly string[] Optional = { "PwnCloudOS Sync", "Install FLARE VM", "Install Elastic SIEM (add-on)" };

        [TestMethod]
        public void HelpExample_ResolvesToThePwnCloudOsSyncStep()
        {
            Assert.AreEqual(new PwnCloudOsSyncStep().Name,
                CreateCommand.ResolveOptionName("pwncloudos-sync", new[] { new PwnCloudOsSyncStep().Name }));
        }

        [TestMethod]
        public void Keys_MatchByNameOrSlug_CaseInsensitive()
        {
            Assert.AreEqual("PwnCloudOS Sync", CreateCommand.ResolveOptionName("PwnCloudOS Sync", Optional));
            Assert.AreEqual("PwnCloudOS Sync", CreateCommand.ResolveOptionName("pwncloudos sync", Optional));
            Assert.AreEqual("Install FLARE VM", CreateCommand.ResolveOptionName("install-flare-vm", Optional));
            Assert.AreEqual("Install Elastic SIEM (add-on)", CreateCommand.ResolveOptionName("install-elastic-siem-add-on", Optional));
        }

        [TestMethod]
        public void UnknownKey_ResolvesToNull()
        {
            Assert.IsNull(CreateCommand.ResolveOptionName("pwncloud", Optional), "no prefix matching");
            Assert.IsNull(CreateCommand.ResolveOptionName("pwncloudos-sync", System.Array.Empty<string>()),
                "a step not offered for this image is unknown");
        }

        [TestMethod]
        public void OptionSlug_LowercasesAndCollapsesSeparators()
        {
            Assert.AreEqual("install-elastic-siem-add-on", CreateCommand.OptionSlug("Install Elastic SIEM (add-on)"));
            Assert.AreEqual("pwncloudos-sync", CreateCommand.OptionSlug("  PwnCloudOS   Sync "));
        }
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace VMCreate.Tests.Unattend
{
    /// <summary>
    /// Tests for UnattendFloppyBuilder and the autounattend.xml template.
    /// These tests validate:
    ///   1. The autounattend.xml is well-formed XML with required elements
    ///   2. The IMAPI2 PowerShell script uses the correct API calls
    ///   3. The ISO creation actually works on Windows (creates a valid ISO)
    /// </summary>
    [TestClass]
    public sealed class UnattendFloppyBuilderTests
    {
        // IMAPI2 stash file creation can fail when multiple ISO creations run
        // concurrently. Use a lock to serialize the integration tests.
        private static readonly object _isoLock = new();

        private static readonly string UnattendTemplatePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Unattend", "autounattend.xml");

        private static readonly string UnattendXmlPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Unattend", "unattend.xml");

        private Mock<ILogger> _loggerMock = null!;
        private string _tempDir = null!;

        [TestInitialize]
        public void Init()
        {
            _loggerMock = new Mock<ILogger>();
            _tempDir = Path.Combine(Path.GetTempPath(), "vmcreate-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, true);
            }
            catch { /* best effort */ }
        }

        #region autounattend.xml validation tests

        [TestMethod]
        public void Autounattend_XmlIsWellFormed()
        {
            // Verify the embedded autounattend.xml is valid XML
            Assert.IsTrue(File.Exists(UnattendTemplatePath),
                $"autounattend.xml not found at {UnattendTemplatePath}");

            var doc = XDocument.Load(UnattendTemplatePath);
            Assert.IsNotNull(doc.Root, "autounattend.xml has no root element");
            Assert.AreEqual("unattend", doc.Root.Name.LocalName,
                "Root element should be 'unattend'");
        }

        [TestMethod]
        public void Autounattend_ContainsOobeSystemPass()
        {
            // The oobeSystem pass is critical for VHDX OOBE flow
            var doc = XDocument.Load(UnattendTemplatePath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");

            Assert.IsNotNull(oobeSettings, "autounattend.xml must contain an oobeSystem pass");

            // Verify shell-setup component exists in oobeSystem
            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");

            Assert.IsNotNull(shellSetup, "oobeSystem must contain Microsoft-Windows-Shell-Setup component");
        }

        [TestMethod]
        public void Autounattend_ContainsFlareUser()
        {
            // Verify the autounattend.xml creates the "flare" user with password "flare"
            var doc = XDocument.Load(UnattendTemplatePath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings, "Missing oobeSystem pass");

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup, "Missing Shell-Setup component");

            // Check LocalAccounts contains flare user
            var localAccounts = shellSetup!.Element(ns + "UserAccounts")?.Element(ns + "LocalAccounts");
            Assert.IsNotNull(localAccounts, "Missing LocalAccounts element");

            var flareAccount = localAccounts!.Elements(ns + "LocalAccount")
                .FirstOrDefault(a => a.Element(ns + "Name")?.Value == "flare");
            Assert.IsNotNull(flareAccount, "Missing 'flare' local account");

            Assert.AreEqual("flare", flareAccount!.Element(ns + "Password")?.Element(ns + "Value")?.Value,
                "flare user password should be 'flare'");
            Assert.AreEqual("true", flareAccount.Element(ns + "Password")?.Element(ns + "PlainText")?.Value,
                "flare user password should be plain text");
            Assert.AreEqual("Administrators", flareAccount.Element(ns + "Group")?.Value,
                "flare user should be in Administrators group");
        }

        [TestMethod]
        public void Autounattend_ContainsAdministratorPassword()
        {
            // Verify the Administrator password is set (required for PowerShell Direct)
            var doc = XDocument.Load(UnattendTemplatePath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings);

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup);

            var adminPw = shellSetup!.Element(ns + "UserAccounts")?.Element(ns + "AdministratorPassword");
            Assert.IsNotNull(adminPw, "Missing AdministratorPassword element");
            Assert.AreEqual("flare", adminPw!.Element(ns + "Value")?.Value,
                "Administrator password should be 'flare'");
            Assert.AreEqual("true", adminPw.Element(ns + "PlainText")?.Value,
                "Administrator password should be plain text");
        }

        [TestMethod]
        public void Autounattend_ContainsAutoLogon()
        {
            // Verify auto-logon is configured for the flare user
            var doc = XDocument.Load(UnattendTemplatePath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings);

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup);

            var autoLogon = shellSetup!.Element(ns + "AutoLogon");
            Assert.IsNotNull(autoLogon, "Missing AutoLogon element");
            Assert.AreEqual("flare", autoLogon!.Element(ns + "Username")?.Value,
                "AutoLogon username should be 'flare'");
            Assert.AreEqual("true", autoLogon.Element(ns + "Password")?.Element(ns + "PlainText")?.Value,
                "AutoLogon password should be plain text");
        }

        [TestMethod]
        public void Autounattend_EnablesPowerShellRemoting()
        {
            // Verify that PowerShell remoting is enabled in FirstLogonCommands
            var doc = XDocument.Load(UnattendTemplatePath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings);

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup);

            var firstLogonCommands = shellSetup!.Element(ns + "FirstLogonCommands");
            Assert.IsNotNull(firstLogonCommands, "Missing FirstLogonCommands");

            var commands = firstLogonCommands!.Elements(ns + "SynchronousCommand").ToList();
            Assert.IsTrue(commands.Count >= 1, "Should have at least one FirstLogonCommand");

            // First command should enable PS remoting
            var firstCommand = commands.FirstOrDefault(c => c.Element(ns + "Order")?.Value == "1");
            Assert.IsNotNull(firstCommand, "Missing first logon command");
            var commandLine = firstCommand!.Element(ns + "CommandLine")?.Value ?? "";
            Assert.IsTrue(commandLine.Contains("Enable-PSRemoting"),
                "First logon command should enable PS remoting. Got: " + commandLine);
        }

        [TestMethod]
        public void Autounattend_EnablesRDP()
        {
            // Verify that RDP is enabled in FirstLogonCommands
            var doc = XDocument.Load(UnattendTemplatePath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings);

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup);

            var firstLogonCommands = shellSetup!.Element(ns + "FirstLogonCommands");
            Assert.IsNotNull(firstLogonCommands);

            var commands = firstLogonCommands!.Elements(ns + "SynchronousCommand").ToList();
            var rdpCommand = commands.FirstOrDefault(c =>
                (c.Element(ns + "CommandLine")?.Value ?? "").Contains("fDenyTSConnections"));

            Assert.IsNotNull(rdpCommand, "Should have a command enabling RDP");
        }

        [TestMethod]
        public void Autounattend_HidesOOBEScreens()
        {
            // Verify that all OOBE screens are hidden for unattended install
            var doc = XDocument.Load(UnattendTemplatePath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings);

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup);

            var oobe = shellSetup!.Element(ns + "OOBE");
            Assert.IsNotNull(oobe, "Missing OOBE element");

            Assert.AreEqual("true", oobe!.Element(ns + "HideEULAPage")?.Value, "HideEULAPage should be true");
            Assert.AreEqual("true", oobe.Element(ns + "HideOnlineAccountScreens")?.Value, "HideOnlineAccountScreens should be true");
        }

        #endregion

        #region unattend.xml (VHDX injection) validation tests

        [TestMethod]
        public void UnattendXml_XmlIsWellFormed()
        {
            // Verify the unattend.xml (for VHDX injection) is valid XML
            Assert.IsTrue(File.Exists(UnattendXmlPath),
                $"unattend.xml not found at {UnattendXmlPath}");

            var doc = XDocument.Load(UnattendXmlPath);
            Assert.IsNotNull(doc.Root, "unattend.xml has no root element");
            Assert.AreEqual("unattend", doc.Root.Name.LocalName,
                "Root element should be 'unattend'");
        }

        [TestMethod]
        public void UnattendXml_ContainsOnlyOobeSystemPass()
        {
            // The VHDX-injected unattend.xml should only contain the oobeSystem pass,
            // since windowsPE is not processed during OOBE on a pre-installed VHDX.
            var doc = XDocument.Load(UnattendXmlPath);
            var ns = doc.Root!.GetDefaultNamespace();

            var passes = doc.Root.Elements(ns + "settings")
                .Select(s => s.Attribute("pass")?.Value)
                .ToList();

            Assert.AreEqual(1, passes.Count, "unattend.xml should contain exactly one pass");
            Assert.AreEqual("oobeSystem", passes[0],
                "unattend.xml should only contain the oobeSystem pass");
        }

        [TestMethod]
        public void UnattendXml_ContainsFlareUser()
        {
            // Verify the unattend.xml creates the "flare" user with password "flare"
            var doc = XDocument.Load(UnattendXmlPath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings, "Missing oobeSystem pass");

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup, "Missing Shell-Setup component");

            var localAccounts = shellSetup!.Element(ns + "UserAccounts")?.Element(ns + "LocalAccounts");
            Assert.IsNotNull(localAccounts, "Missing LocalAccounts element");

            var flareAccount = localAccounts!.Elements(ns + "LocalAccount")
                .FirstOrDefault(a => a.Element(ns + "Name")?.Value == "flare");
            Assert.IsNotNull(flareAccount, "Missing 'flare' local account");

            Assert.AreEqual("flare", flareAccount!.Element(ns + "Password")?.Element(ns + "Value")?.Value,
                "flare user password should be 'flare'");
            Assert.AreEqual("Administrators", flareAccount.Element(ns + "Group")?.Value,
                "flare user should be in Administrators group");
        }

        [TestMethod]
        public void UnattendXml_ContainsAdministratorPassword()
        {
            // Verify the Administrator password is set (required for PowerShell Direct)
            var doc = XDocument.Load(UnattendXmlPath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings);

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup);

            var adminPw = shellSetup!.Element(ns + "UserAccounts")?.Element(ns + "AdministratorPassword");
            Assert.IsNotNull(adminPw, "Missing AdministratorPassword element");
            Assert.AreEqual("flare", adminPw!.Element(ns + "Value")?.Value,
                "Administrator password should be 'flare'");
        }

        [TestMethod]
        public void UnattendXml_EnablesPowerShellRemoting()
        {
            // Verify that PowerShell remoting is enabled in FirstLogonCommands
            var doc = XDocument.Load(UnattendXmlPath);
            var ns = doc.Root!.GetDefaultNamespace();

            var oobeSettings = doc.Root.Elements(ns + "settings")
                .FirstOrDefault(s => s.Attribute("pass")?.Value == "oobeSystem");
            Assert.IsNotNull(oobeSettings);

            var shellSetup = oobeSettings!.Elements()
                .FirstOrDefault(c => c.Attribute("name")?.Value == "Microsoft-Windows-Shell-Setup");
            Assert.IsNotNull(shellSetup);

            var firstLogonCommands = shellSetup!.Element(ns + "FirstLogonCommands");
            Assert.IsNotNull(firstLogonCommands, "Missing FirstLogonCommands");

            var commands = firstLogonCommands!.Elements(ns + "SynchronousCommand").ToList();
            Assert.IsTrue(commands.Count >= 1, "Should have at least one FirstLogonCommand");

            var firstCommand = commands.FirstOrDefault(c => c.Element(ns + "Order")?.Value == "1");
            Assert.IsNotNull(firstCommand, "Missing first logon command");
            var commandLine = firstCommand!.Element(ns + "CommandLine")?.Value ?? "";
            Assert.IsTrue(commandLine.Contains("Enable-PSRemoting"),
                "First logon command should enable PS remoting. Got: " + commandLine);
        }

        #endregion

        #region ISO creation tests (Windows-only, requires IMAPI2)

        [TestMethod]
        [TestCategory("Integration")]
        public void BuildUnattendIso_CreatesValidIso()
        {
            // This test actually creates an ISO using IMAPI2 and verifies it exists.
            // It only runs on Windows with IMAPI2 available.
            // Note: IMAPI2 can fail with "Cannot initialize file-system stash file"
            // if temp directory is not writable or disk is low — this is an
            // environment issue, not a code bug.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("IMAPI2 ISO creation is only available on Windows");
                return;
            }

            // Use a unique temp directory per test to avoid IMAPI2 stash conflicts
            string testTempDir = Path.Combine(Path.GetTempPath(), "vmcreate-iso-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testTempDir);
            string isoPath = Path.Combine(testTempDir, "test-unattend.iso");

            try
            {
                lock (_isoLock)
                {
                    string result = UnattendFloppyBuilder.BuildUnattendIso(isoPath, _loggerMock.Object);

                    Assert.AreEqual(isoPath, result, "BuildUnattendIso should return the output path");
                    Assert.IsTrue(File.Exists(isoPath), "ISO file should exist at the output path");

                    var fileInfo = new FileInfo(isoPath);
                    Assert.IsTrue(fileInfo.Length > 0, "ISO file should not be empty");
                    // A valid ISO should be at least a few KB (headers + file data)
                    Assert.IsTrue(fileInfo.Length > 1024,
                        $"ISO file should be at least 1KB, but was {fileInfo.Length} bytes");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(isoPath))
                        File.Delete(isoPath);
                    if (Directory.Exists(testTempDir))
                        Directory.Delete(testTempDir, true);
                }
                catch { /* best effort */ }
            }
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void BuildUnattendIsoFromContent_CreatesValidIso()
        {
            // Test with the actual autounattend.xml content
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("IMAPI2 ISO creation is only available on Windows");
                return;
            }

            string unattendContent = File.ReadAllText(UnattendTemplatePath);
            string testTempDir = Path.Combine(Path.GetTempPath(), "vmcreate-iso-test2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testTempDir);
            string isoPath = Path.Combine(testTempDir, "test-custom-unattend.iso");

            try
            {
                lock (_isoLock)
                {
                    string result = UnattendFloppyBuilder.BuildUnattendIsoFromContent(isoPath, unattendContent, _loggerMock.Object);

                    Assert.AreEqual(isoPath, result);
                    Assert.IsTrue(File.Exists(isoPath), "ISO file should exist");

                    var fileInfo = new FileInfo(isoPath);
                    Assert.IsTrue(fileInfo.Length > 1024,
                        $"ISO file should be at least 1KB, but was {fileInfo.Length} bytes");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(isoPath))
                        File.Delete(isoPath);
                    if (Directory.Exists(testTempDir))
                        Directory.Delete(testTempDir, true);
                }
                catch { /* best effort */ }
            }
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void BuildUnattendIso_IsoContainsAutounattend()
        {
            // Verify the created ISO contains the autounattend.xml content
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("IMAPI2 ISO creation is only available on Windows");
                return;
            }

            string testTempDir = Path.Combine(Path.GetTempPath(), "vmcreate-iso-test3-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testTempDir);
            string isoPath = Path.Combine(testTempDir, "test-verify-content.iso");

            try
            {
                lock (_isoLock)
                {
                    UnattendFloppyBuilder.BuildUnattendIso(isoPath, _loggerMock.Object);

                    // Verify the ISO file exists and has reasonable size
                    Assert.IsTrue(File.Exists(isoPath), "ISO file should exist");
                    var fileInfo = new FileInfo(isoPath);
                    Assert.IsTrue(fileInfo.Length > 1024,
                        $"ISO should be at least 1KB, was {fileInfo.Length} bytes");

                    // Verify the ISO contains the autounattend.xml content by searching
                    // the raw bytes for the XML content (the filename may be stored in
                    // various encodings in the ISO9660/Joliet/UDF file systems)
                    byte[] isoBytes = File.ReadAllBytes(isoPath);
                    string isoContent = System.Text.Encoding.UTF8.GetString(isoBytes);
                    Assert.IsTrue(isoContent.Contains("<?xml") || isoContent.Contains("<unattend"),
                        "ISO should contain the autounattend.xml content");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(isoPath))
                        File.Delete(isoPath);
                    if (Directory.Exists(testTempDir))
                        Directory.Delete(testTempDir, true);
                }
                catch { /* best effort */ }
            }
        }

        #endregion

        #region IMAPI2 typed interop tests (Windows-only)

        [TestMethod]
        public void IMAPI2_AddTree_ImportsDirectoryIntoIso()
        {
            // Real behavioral test for IFsiDirectoryItem.AddTree — the method the
            // previous inert signature probe could not exercise. AddTree's native
            // signature is (BSTR sourceDirectory, VARIANT_BOOL includeBaseDirectory),
            // exactly two parameters; this round-trip guards against both signature
            // drift and vtable mis-declaration in Imapi2Interop.
            // See: https://learn.microsoft.com/en-us/windows/win32/api/imapi2fs/nf-imapi2fs-ifsidirectoryitem-addtree
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("IMAPI2 is only available on Windows");
                return;
            }

            string testTempDir = Path.Combine(Path.GetTempPath(), "vmcreate-addtree-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(testTempDir);
            string sourceDir = Path.Combine(testTempDir, "source");
            Directory.CreateDirectory(sourceDir);
            string isoPath = Path.Combine(testTempDir, "addtree-direct.iso");

            try
            {
                lock (_isoLock)
                {
                    // Assemble a real staged tree like the builder does, then embed it
                    // without the base directory (includeBaseDirectory: false), same
                    // as the production path.
                    File.WriteAllText(Path.Combine(sourceDir, "autounattend.xml"), "<?xml version=\"1.0\"?><unattend/>");
                    Imapi2Interop.CreateIsoFromDirectory(sourceDir, isoPath, volumeName: "UNATTEND");

                    Assert.IsTrue(File.Exists(isoPath), "ISO file should exist at the output path");
                    Assert.IsTrue(new FileInfo(isoPath).Length > 1024,
                        $"ISO should be >1KB, was {new FileInfo(isoPath).Length} bytes");

                    // A valid multi-file-system ISO contains the volume name marker
                    // and the file content (Joliet stores names as UCS-2, data bytes
                    // pass through unchanged).
                    byte[] isoBytes = File.ReadAllBytes(isoPath);
                    string isoText = System.Text.Encoding.UTF8.GetString(isoBytes);
                    Assert.IsTrue(isoText.Contains("<?xml") || isoText.Contains("<unattend"),
                        "ISO should contain the imported file content");
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(testTempDir))
                        Directory.Delete(testTempDir, true);
                }
                catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void IMAPI2_FsiFileSystemsToCreate_Values()
        {
            // Verify the IMAPI2 file system flags round-trip through the typed
            // interop: FsiFileSystemISO9660=1, Joliet=2, UDF=4; combined = 7,
            // matching what production sets (ISO9660 + Joliet + UDF).
            const FsiFileSystems ExpectedFileSystemFlags =
                FsiFileSystems.ISO9660 | FsiFileSystems.Joliet | FsiFileSystems.UDF;

            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("IMAPI2 is only available on Windows");
                return;
            }

            Type? imageType = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage", throwOnError: true);
            Assert.IsNotNull(imageType, "IMAPI2FS.MsftFileSystemImage ProgID must resolve on Windows");
            var image = (IFileSystemImage)Activator.CreateInstance(imageType)!;
            image.FileSystemsToCreate = ExpectedFileSystemFlags;

            Assert.AreEqual(ExpectedFileSystemFlags, (FsiFileSystems)image.FileSystemsToCreate,
                "FileSystemsToCreate should round-trip as ISO9660|Joliet|UDF (=7)");
        }

        #endregion
    }
}
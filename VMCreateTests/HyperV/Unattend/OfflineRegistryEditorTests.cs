using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Linq;
using VMCreate;
using VMCreate.HyperV.Unattend;

namespace VMCreate.Tests.HyperV.Unattend
{
    [TestClass]
    public sealed class OfflineRegistryEditorTests
    {
        private Mock<IPowerShellExecutor> _powerShell = null!;
        private Mock<ILogger<OfflineRegistryEditor>> _logger = null!;
        private OfflineRegistryEditor _editor = null!;
        [TestInitialize]
        public void Setup()
        {
            _powerShell = new Mock<IPowerShellExecutor>();
            _logger = new Mock<ILogger<OfflineRegistryEditor>>();
            _editor = new OfflineRegistryEditor(_powerShell.Object, _logger.Object);
        }

        [TestMethod]
        public void AddKey_InvokesRegAdd()
        {
            _editor.AddKey("HKLM\\Test\\Key");
            _powerShell.Verify(p => p.RunApplication("reg",
                It.Is<string[]>(args =>
                    args.Contains("add")
                    && args.Contains("HKLM\\Test\\Key")
                    && args.Contains("/f"))),
                Times.Once);
        }

        [TestMethod]
        public void AddKey_NeverUsesRunCommand()
        {
            // Regression pin: reg.exe is not a cmdlet; RunCommand passes parameters via
            // AddParameter, which reg.exe rejects ("Invalid Argument/Option - 'ArgumentList'").
            _powerShell
                .Setup(p => p.RunApplication(It.IsAny<string>(), It.IsAny<string[]>()))
                .Returns(new PowerShellResult());
            _editor.AddKey("HKLM\\Test\\Key");
            _editor.SetDword("HKLM\\Test\\Key", "V", 1);
            _editor.SetString("HKLM\\Test\\Key", "V", "Off");
            _editor.UnloadHive("Mount");
            _powerShell.Verify(p => p.RunCommand(
                It.IsAny<string>(), It.IsAny<(string, object)[]>()), Times.Never);
        }

        [TestMethod]
        public void SetDword_InvokesRegAddWithCorrectType()
        {
            _editor.SetDword("HKLM\\Test\\Key", "ValueName", 1);
            _powerShell.Verify(p => p.RunApplication("reg",
                It.Is<string[]>(args =>
                    args.Contains("REG_DWORD")
                    && args.Contains("ValueName")
                    && args.Contains("1"))),
                Times.Once);
        }

        [TestMethod]
        public void SetString_InvokesRegAddWithCorrectType()
        {
            _editor.SetString("HKLM\\Test\\Key", "ValueName", "Off");
            _powerShell.Verify(p => p.RunApplication("reg",
                It.Is<string[]>(args =>
                    args.Contains("REG_SZ")
                    && args.Contains("ValueName")
                    && args.Contains("Off"))),
                Times.Once);
        }

        [TestMethod]
        public void SetServiceStart_InvokesRegAddWithStartDword()
        {
            _editor.SetServiceStart("Mount", "ControlSet001", "WinDefend", 4);
            _powerShell.Verify(p => p.RunApplication("reg",
                It.Is<string[]>(args =>
                    args.Contains("HKLM\\Mount\\ControlSet001\\Services\\WinDefend")
                    && args.Contains("Start")
                    && args.Contains("4"))),
                Times.Once);
        }

        [TestMethod]
        public void LoadHive_Throws_WhenHiveMissing()
        {
            bool threw = false;
            try
            {
                _editor.LoadHive("Z:\\missing\\SOFTWARE", "Mount");
            }
            catch (FileNotFoundException)
            {
                threw = true;
            }
            Assert.IsTrue(threw, "Expected FileNotFoundException for missing hive.");
        }
    }
}

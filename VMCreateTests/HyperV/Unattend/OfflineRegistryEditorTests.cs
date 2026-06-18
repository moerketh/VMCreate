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
        private Mock<IPowerShellExecutor> _powerShell;
        private Mock<ILogger<OfflineRegistryEditor>> _logger;
        private OfflineRegistryEditor _editor;

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
            _powerShell.Verify(p => p.RunCommand("reg",
                It.Is<(string, object)[]>(args =>
                    args.Any(a => a.Item1 == "ArgumentList"
                        && a.Item2.GetType() == typeof(string[])
                        && ((string[])a.Item2).Contains("add")
                        && ((string[])a.Item2).Contains("HKLM\\Test\\Key")
                        && ((string[])a.Item2).Contains("/f")))),
                Times.Once);
        }

        [TestMethod]
        public void SetDword_InvokesRegAddWithCorrectType()
        {
            _editor.SetDword("HKLM\\Test\\Key", "ValueName", 1);
            _powerShell.Verify(p => p.RunCommand("reg",
                It.Is<(string, object)[]>(args =>
                    args.Any(a => a.Item1 == "ArgumentList"
                        && a.Item2.GetType() == typeof(string[])
                        && ((string[])a.Item2).Contains("REG_DWORD")
                        && ((string[])a.Item2).Contains("ValueName")
                        && ((string[])a.Item2).Contains("1")))),
                Times.Once);
        }

        [TestMethod]
        public void SetString_InvokesRegAddWithCorrectType()
        {
            _editor.SetString("HKLM\\Test\\Key", "ValueName", "Off");
            _powerShell.Verify(p => p.RunCommand("reg",
                It.Is<(string, object)[]>(args =>
                    args.Any(a => a.Item1 == "ArgumentList"
                        && a.Item2.GetType() == typeof(string[])
                        && ((string[])a.Item2).Contains("REG_SZ")
                        && ((string[])a.Item2).Contains("ValueName")
                        && ((string[])a.Item2).Contains("Off")))),
                Times.Once);
        }

        [TestMethod]
        public void SetServiceStart_InvokesRegAddWithStartDword()
        {
            _editor.SetServiceStart("Mount", "ControlSet001", "WinDefend", 4);
            _powerShell.Verify(p => p.RunCommand("reg",
                It.Is<(string, object)[]>(args =>
                    args.Any(a => a.Item1 == "ArgumentList"
                        && a.Item2.GetType() == typeof(string[])
                        && ((string[])a.Item2).Contains("HKLM\\Mount\\ControlSet001\\Services\\WinDefend")
                        && ((string[])a.Item2).Contains("Start")
                        && ((string[])a.Item2).Contains("4")))),
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

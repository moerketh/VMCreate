using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Linq;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    [TestClass]
    public sealed class DeploymentLoggerTests
    {
        [TestMethod]
        public void Constructor_SetsVmName()
        {
            var logger = new DeploymentLogger("MyVM");
            Assert.AreEqual("MyVM", logger.VmName);
        }

        [TestMethod]
        public void Constructor_NullVmName_FallsBackToUnnamed()
        {
            var logger = new DeploymentLogger(null);
            Assert.AreEqual("Unnamed", logger.VmName);
        }

        [TestMethod]
        public void Log_AddsInfoEntry()
        {
            var logger = new DeploymentLogger("vm");
            logger.Log("hello");

            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(DeploymentLogSeverity.Info, logger.Entries[0].Severity);
            Assert.AreEqual("hello", logger.Entries[0].Message);
        }

        [TestMethod]
        public void LogWarning_AddsWarningEntry()
        {
            var logger = new DeploymentLogger("vm");
            logger.LogWarning("warn");

            Assert.AreEqual(DeploymentLogSeverity.Warning, logger.Entries[0].Severity);
        }

        [TestMethod]
        public void LogError_AddsErrorEntry()
        {
            var logger = new DeploymentLogger("vm");
            logger.LogError("err");

            Assert.AreEqual(DeploymentLogSeverity.Error, logger.Entries[0].Severity);
        }

        [TestMethod]
        public void LogStep_AddsStepEntry()
        {
            var logger = new DeploymentLogger("vm");
            logger.LogStep("PrepareDisk", true, "converted");

            var entry = logger.Entries[0];
            Assert.AreEqual(DeploymentLogSeverity.Step, entry.Severity);
            Assert.AreEqual("PrepareDisk: OK", entry.Message);
            Assert.AreEqual("converted", entry.Details);
        }

        [TestMethod]
        public void GetLog_ContainsVmNameAndEntries()
        {
            var logger = new DeploymentLogger("TestVM");
            logger.Log("first");
            logger.LogError("second");

            var log = logger.GetLog();
            StringAssert.Contains(log, "TestVM");
            StringAssert.Contains(log, "first");
            StringAssert.Contains(log, "second");
        }

        [TestMethod]
        public void SaveToFile_WritesLogToDisk()
        {
            var logger = new DeploymentLogger("SaveVM");
            logger.Log("entry");

            string path = Path.Combine(Path.GetTempPath(), $"{nameof(SaveToFile_WritesLogToDisk)}-{System.Guid.NewGuid():N}.log");
            try
            {
                logger.SaveToFile(path);
                Assert.IsTrue(File.Exists(path));
                StringAssert.Contains(File.ReadAllText(path), "entry");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [TestMethod]
        public void Entries_ReturnsSnapshot_DoesNotReflectLaterChanges()
        {
            var logger = new DeploymentLogger("vm");
            var snapshot = logger.Entries;
            logger.Log("after");

            Assert.AreEqual(0, snapshot.Count);
            Assert.AreEqual(1, logger.Entries.Count);
        }

        [TestMethod]
        public void LogStep_Failure_MarksFail()
        {
            var logger = new DeploymentLogger("vm");
            logger.LogStep("InjectUnattend", false, "exit 1");

            Assert.AreEqual("InjectUnattend: FAIL", logger.Entries[0].Message);
        }
    }
}

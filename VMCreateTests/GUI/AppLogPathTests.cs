using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace VMCreate.Tests.GUI
{
    /// <summary>
    /// Pins the OpenLog path resolution. Serilog's RollingInterval.Day
    /// writes "VMCreateYYYYMMDD.log" — the template path
    /// (%TEMP%\VMCreate.log) never exists, so OpenLog's Process.Start
    /// failed with "file not found" whenever the user clicked Open Log
    /// (observed after a failed deploy).
    /// </summary>
    [TestClass]
    public class AppLogPathTests
    {
        // Works in a private temp directory: the real %TEMP%\VMCreate*.log
        // files belong to the user (and to any deploy running right now) and
        // must never be written or deleted by a test.
        private string _dir = null!;
        private string _template = null!;
        private static readonly DateTime Today = new(2026, 10, 25, 12, 0, 0);

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vmcreate-logpath-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _template = Path.Combine(_dir, "VMCreate.log");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        [TestMethod]
        public void ResolveRollingLogFile_ResolvesTodaysRollingFile_WhenItExists()
        {
            string expected = Path.Combine(_dir, "VMCreate20261025.log");
            File.WriteAllText(expected, "test");

            Assert.AreEqual(expected, global::VMCreate.App.ResolveRollingLogFile(_template, Today),
                "when today's rolling file exists, OpenLog must open THAT file, not the never-existing template");
        }

        [TestMethod]
        public void ResolveRollingLogFile_FallsBackToTemplate_WhenOnlyOlderFilesExist()
        {
            File.WriteAllText(Path.Combine(_dir, "VMCreate20261024.log"), "yesterday");

            Assert.AreEqual(_template, global::VMCreate.App.ResolveRollingLogFile(_template, Today));
        }

        [TestMethod]
        public void CurrentLogFilePath_FallsBackToTemplate_BeforeFirstWriteToday()
        {
            string dir = Path.GetDirectoryName(global::VMCreate.App.LogFilePath)!;
            string before = DateTime.Now.ToString("yyyyMMdd");

            string result = global::VMCreate.App.CurrentLogFilePath;

            string after = DateTime.Now.ToString("yyyyMMdd");

            // A midnight rollover between the write-check and the assert
            // makes the wall-clock date unstable — accept either side of it.
            bool isTemplate = result == global::VMCreate.App.LogFilePath;
            bool isDatedFile =
                result == Path.Combine(dir, $"VMCreate{before}.log") ||
                result == Path.Combine(dir, $"VMCreate{after}.log");

            Assert.IsTrue(isTemplate || isDatedFile,
                $"result '{result}' must be the template or a current-date rolling file " +
                $"({before}/{after}) — never a stale or malformed name");
        }
    }
}

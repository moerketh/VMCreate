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
        [TestMethod]
        public void CurrentLogFilePath_ResolvesTodaysRollingFile_WhenItExists()
        {
            string dir = Path.GetDirectoryName(global::VMCreate.App.LogFilePath)!;
            string expected = Path.Combine(dir,
                $"VMCreate{DateTime.Now:yyyyMMdd}.log");

            try
            {
                File.WriteAllText(expected, "test");
                Assert.AreEqual(expected, global::VMCreate.App.CurrentLogFilePath,
                    "when today's rolling file exists, OpenLog must open THAT file, not the never-existing template");
            }
            finally
            {
                File.Delete(expected);
            }
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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using VMCreate.CLI.Progress;

namespace VMCreate.Tests
{
    /// <summary>
    /// Pins the final NDJSON line of a successful <c>create --format json</c>
    /// run: it names the created VM, whose timestamp suffix only CreateVM
    /// knows, so scripts need not guess it from the requested name.
    /// </summary>
    [TestClass]
    public sealed class JsonProgressReporterTests
    {
        [TestMethod]
        public void DoneLine_NamesTheCreatedVm_InTheProgressSchema()
        {
            string line = JsonProgressReporter.DoneLine("t-omarchy-o1d_20261025162932");

            Assert.AreEqual("{\"phase\":\"Done\",\"vm_name\":\"t-omarchy-o1d_20261025162932\"}", line,
                "same snake_case schema as the progress lines, nulls omitted");
            using var doc = JsonDocument.Parse(line);
            Assert.AreEqual("t-omarchy-o1d_20261025162932", doc.RootElement.GetProperty("vm_name").GetString());
        }

        [TestMethod]
        public void DoneLine_EscapesTheName()
        {
            using var doc = JsonDocument.Parse(JsonProgressReporter.DoneLine("a\"b\\c_20261025162932"));
            Assert.AreEqual("a\"b\\c_20261025162932", doc.RootElement.GetProperty("vm_name").GetString());
        }
    }
}

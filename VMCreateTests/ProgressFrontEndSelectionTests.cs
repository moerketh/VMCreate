using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate.CLI.Commands;

namespace VMCreate.Tests
{
    /// <summary>
    /// Pins the create command's progress front-end selection
    /// (<see cref="CreateCommand.SelectProgressFrontEnd"/>). The redirected-text
    /// path is load-bearing: Spectre's Live display dereferences the real
    /// console handle and crashes with IOException when stdout is piped or
    /// redirected — the exact crash reported against --format text. These
    /// tests make it impossible to reorder the checks or drop the redirect
    /// guard unnoticed, and pin that --quiet/--format json win regardless of
    /// the console shape.
    /// </summary>
    [TestClass]
    public sealed class ProgressFrontEndSelectionTests
    {
        [TestMethod]
        public void Quiet_SuppressesProgress_EvenWithJsonOrRedirect()
        {
            Assert.AreEqual(CreateCommand.ProgressFrontEnd.None,
                CreateCommand.SelectProgressFrontEnd(quiet: true, jsonMode: false, isOutputRedirected: false));
            Assert.AreEqual(CreateCommand.ProgressFrontEnd.None,
                CreateCommand.SelectProgressFrontEnd(quiet: true, jsonMode: true, isOutputRedirected: false));
            Assert.AreEqual(CreateCommand.ProgressFrontEnd.None,
                CreateCommand.SelectProgressFrontEnd(quiet: true, jsonMode: false, isOutputRedirected: true));
        }

        [TestMethod]
        public void JsonMode_WinsOverRedirect_AndIgnoresConsoleShape()
        {
            // CI captures NDJSON through redirection — json must not fall back
            // to the plain-text reporter just because stdout is a pipe.
            Assert.AreEqual(CreateCommand.ProgressFrontEnd.Json,
                CreateCommand.SelectProgressFrontEnd(quiet: false, jsonMode: true, isOutputRedirected: true));
            Assert.AreEqual(CreateCommand.ProgressFrontEnd.Json,
                CreateCommand.SelectProgressFrontEnd(quiet: false, jsonMode: true, isOutputRedirected: false));
        }

        [TestMethod]
        public void RedirectedText_SelectsPlainText_NeverTheLiveDisplay()
        {
            Assert.AreEqual(CreateCommand.ProgressFrontEnd.PlainText,
                CreateCommand.SelectProgressFrontEnd(quiet: false, jsonMode: false, isOutputRedirected: true));
        }

        [TestMethod]
        public void InteractiveTerminal_SelectsRichLiveDisplay()
        {
            Assert.AreEqual(CreateCommand.ProgressFrontEnd.Rich,
                CreateCommand.SelectProgressFrontEnd(quiet: false, jsonMode: false, isOutputRedirected: false));
        }
    }
}

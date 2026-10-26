using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using VMCreate.CLI.Commands;

namespace VMCreate.Tests
{
    /// <summary>
    /// Pins the <c>--cloud-init</c> flag's parse-level contract: the option
    /// exists on the create command and takes exactly one file argument. The
    /// deployment-shaped behavior (seed attachment, merge order, validation)
    /// is pinned in <c>CloudInitSeedBuilderTests</c> and
    /// <c>DiskImageVmCreationStrategyTests</c>.
    /// </summary>
    [TestClass]
    public sealed class CloudInitCliOptionTests
    {
        private static (ParseResult Parse, Option<string> Option) ParseCloudInit(params string[] args)
        {
            // Build parses the option tree without a DI container: the option
            // registration is what this test pins, not command execution.
            // CreateCommand.Build returns the 'create' command itself, so the
            // arguments are the command's own options (no leading "create").
            var provider = new ServiceCollection().BuildServiceProvider();
            var cmd = CreateCommand.Build(provider);

            var parse = cmd.Parse(args);
            var option = cmd.Options.Single(o => o.Name == "--cloud-init");
            return (parse, (Option<string>)option);
        }

        [TestMethod]
        public void CloudInit_OptionExistsAndOmittedYieldsNull()
        {
            var (parse, option) = ParseCloudInit("--image", "kali");

            Assert.IsNull(parse.GetValue(option),
                "no --cloud-init means no custom user-data — the seed stays the stock cloud-config");
        }

        [TestMethod]
        public void CloudInit_AcceptAFilePath()
        {
            var (parse, option) = ParseCloudInit("--cloud-init", "C:\\seed\\user-data.yaml");

            Assert.AreEqual(0, parse.Errors.Count,
                "a single file path is a valid --cloud-init invocation");
            Assert.AreEqual("C:\\seed\\user-data.yaml", parse.GetValue(option));
        }

        [TestMethod]
        public void CloudInit_RejectsMissingValue()
        {
            var (parse, _) = ParseCloudInit("--cloud-init");

            Assert.IsTrue(parse.Errors.Count > 0,
                "--cloud-init without a value must fail the parse, not silently deploy without user-data");
        }
    }
}
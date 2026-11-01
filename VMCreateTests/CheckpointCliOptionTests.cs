using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.CommandLine;
using System.Linq;
using VMCreate.CLI.Commands;

namespace VMCreate.Tests
{
    /// <summary>
    /// Pins the <c>--no-checkpoint</c> flag's parse-level contract and the
    /// on-by-default setting it opts out of. When the checkpoint is taken is
    /// pinned in <c>VmDeploymentOrchestratorTests</c>.
    /// </summary>
    [TestClass]
    public sealed class CheckpointCliOptionTests
    {
        private static bool ParseNoCheckpoint(params string[] args)
        {
            var cmd = CreateCommand.Build(new ServiceCollection().BuildServiceProvider());
            var option = (Option<bool>)cmd.Options.Single(o => o.Name == "--no-checkpoint");
            var parse = cmd.Parse(args);
            Assert.AreEqual(0, parse.Errors.Count);
            return parse.GetValue(option);
        }

        [TestMethod]
        public void NoCheckpoint_OmittedLeavesTheCheckpointOn()
        {
            Assert.IsFalse(ParseNoCheckpoint("--image", "kali"));
            Assert.IsTrue(new VmSettings().CreateCleanCheckpoint, "GUI and CLI default: checkpoint after deploy");
            Assert.IsTrue(VmDeploymentPlan.FromSettings(new VmSettings { VMName = "x" }).CreateCleanCheckpoint);
        }

        [TestMethod]
        public void NoCheckpoint_FlagOptsOut()
        {
            Assert.IsTrue(ParseNoCheckpoint("--image", "kali", "--no-checkpoint"));
            Assert.IsFalse(VmDeploymentPlan.FromSettings(
                new VmSettings { VMName = "x", CreateCleanCheckpoint = false }).CreateCleanCheckpoint);
        }
    }
}

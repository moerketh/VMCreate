using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="EnablePasswordlessSudoStep"/> and
    /// <see cref="RemovePasswordlessSudoStep"/> — the Omarchy-deploy sudo
    /// bootstrap. The central contract is password hygiene: the deploy
    /// password may ONLY travel on ssh's stdin; the guest command string must
    /// never contain it.
    /// </summary>
    [TestClass]
    public sealed class PasswordlessSudoStepTests
    {
        private const string Password = "sup3r-secret";

        private Mock<IGuestShell> _shell = null!;
        private Mock<ILogger> _logger = null!;

        private static GalleryItem TaggedItem(string username = "omarchy") => new()
        {
            Name = "Omarchy",
            LinuxDistro = LinuxDistro.Arch,
            InitialUsername = username,
            InitialPassword = Password,
            Tags = new List<string> { "omarchy" },
        };

        [TestInitialize]
        public void Setup()
        {
            _shell = new Mock<IGuestShell>();
            _shell.Setup(s => s.VmName).Returns("TestVM");
            // Default probe answer: drop-in absent, plain RunCommandAsync
            // returns empty unless reconfigured.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("ABSENT");
            _logger = new Mock<ILogger>();
        }

        // ── EnablePasswordlessSudoStep metadata ─────────────────────────

        [TestMethod]
        public void EnableStep_Metadata_IsCorrect()
        {
            var step = new EnablePasswordlessSudoStep();

            Assert.AreEqual(CustomizationPhase.PostBoot, step.Phase);
            Assert.AreEqual(StepPlatform.Linux, step.Platform);
            Assert.AreEqual(45, step.Order, "45 = directly ahead of the first sudo-based step (RemoveVBoxGuestAdditions at 50)");
            Assert.IsFalse(step.IsOptional, "required infrastructure — not a user-toggleable add-on");
            Assert.IsTrue(step.DefaultEnabled);
            Assert.AreEqual("Sub_PasswordlessSudoBootstrap", step.DeployPhaseId);
            Assert.AreEqual("Sub_PasswordlessSudoBootstrap", step.ProgressPhaseId);
        }

        [TestMethod]
        public void EnableStep_Gates_OnOmarchyTag()
        {
            var step = new EnablePasswordlessSudoStep();

            Assert.IsTrue(step.IsVisibleFor(TaggedItem()));
            Assert.IsTrue(step.IsApplicable(TaggedItem(), new VmCustomizations()));

            Assert.IsFalse(step.IsVisibleFor(new GalleryItem { Name = "Ubuntu" }), "untagged items must not see the card");
            Assert.IsFalse(step.IsApplicable(new GalleryItem { Name = "Ubuntu" }, new VmCustomizations()));
            Assert.IsFalse(step.IsVisibleFor(null));
            Assert.IsFalse(step.IsApplicable(null, null));
        }

        // ── EnablePasswordlessSudoStep execution ────────────────────────

        [TestMethod]
        public async Task EnableStep_Execute_PasswordGoesOnStdin_NeverInCommand()
        {
            var step = new EnablePasswordlessSudoStep();
            string? command = null;
            string? stdin = null;
            _shell.Setup(s => s.RunCommandWithStdinAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((cmd, si, _) => { command = cmd; stdin = si; })
                  .ReturnsAsync("");

            await step.ExecuteAsync(_shell.Object, TaggedItem(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandWithStdinAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.IsNotNull(command);
            Assert.AreEqual(Password + "\n", stdin, "the password is exactly one stdin line for sudo -S");

            StringAssert.StartsWith(command, "sudo -S -k bash -c", "one-shot sudo -S (stdin password) with -k to force the prompt to consume the stdin line");
            StringAssert.Contains(command, EnablePasswordlessSudoStep.DropInPath);
            StringAssert.Contains(command, "omarchy ALL=(ALL:ALL) NOPASSWD: ALL");
            StringAssert.Contains(command, "visudo -c -f", "the drop-in must be visudo-validated before acceptance");
            StringAssert.Contains(command, "chmod 440");
            Assert.IsFalse(command.Contains(Password),
                "the password must never appear in the guest command string (command lines are visible via ps and logs)");
        }

        [TestMethod]
        public async Task EnableStep_Execute_StagesVisudoChecks_ThenMovesIntoPlace()
        {
            var step = new EnablePasswordlessSudoStep();
            string? command = null;
            _shell.Setup(s => s.RunCommandWithStdinAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, string, CancellationToken>((cmd, _, _) => command = cmd)
                  .ReturnsAsync("");

            await step.ExecuteAsync(_shell.Object, TaggedItem(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            Assert.IsNotNull(command);
            // The drop-in is staged next to its final destination under a
            // dot-suffixed name — sudo never parses sudoers.d files whose
            // name contains a dot — validated there, and only a passing
            // visudo check renames it into place. A failed check must not
            // leave a broken file in /etc/sudoers.d: sudoers.d content is
            // live for every sudo, and a malformed drop-in breaks sudo on
            // the guest.
            StringAssert.Contains(command, EnablePasswordlessSudoStep.DropInPath + ".vmcreate-tmp",
                "the file must be staged, not written straight to the sudoers.d target");
            StringAssert.Contains(command, "visudo -c -f \"$tmp\"",
                "visudo must validate the staged copy");
            StringAssert.Contains(command, "mv \"$tmp\" " + EnablePasswordlessSudoStep.DropInPath,
                "only a passing visudo check moves the drop-in into place (atomic within /etc/sudoers.d)");
            // The script embeds in sudo bash -c '...' with single quotes
            // escaped as '\'' (SshTransport.EscapeSingleQuotes), so the trap
            // assertion uses the escaped form of: trap 'rm -f "$tmp"' EXIT.
            StringAssert.Contains(command, "trap '\\''rm -f \"$tmp\"'\\'' EXIT",
                "a failing check must clean the staged file up");
            StringAssert.Contains(command, "> \"$tmp\"",
                "printf writes the staged file, not the live drop-in path");
        }

        [TestMethod]
        public async Task EnableStep_Execute_WhenDropInAlreadyPresent_IsIdempotent()
        {
            var step = new EnablePasswordlessSudoStep();
            // Probe finds the drop-in AND NOPASSWD sudo already works.
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("PRESENT");

            await step.ExecuteAsync(_shell.Object, TaggedItem(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandWithStdinAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "a leftover drop-in must not be rewritten (a rerun over NOPASSWD sudo would feed the password line to bash)");
        }

        [TestMethod]
        public async Task EnableStep_Execute_WithoutPassword_Skips()
        {
            var step = new EnablePasswordlessSudoStep();
            var item = TaggedItem();
            item.InitialPassword = null;

            await step.ExecuteAsync(_shell.Object, item, new VmCustomizations(), _logger.Object, CancellationToken.None);

            _shell.Verify(s => s.RunCommandWithStdinAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task EnableStep_Execute_InvalidUsername_ThrowsBeforeAnyGuestCall()
        {
            var step = new EnablePasswordlessSudoStep();
            var item = TaggedItem("root;rm -rf /");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                step.ExecuteAsync(_shell.Object, item, new VmCustomizations(), _logger.Object, CancellationToken.None));

            _shell.Verify(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _shell.Verify(s => s.RunCommandWithStdinAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ── RemovePasswordlessSudoStep ──────────────────────────────────

        [TestMethod]
        public void RemoveStep_Metadata_IsCorrect()
        {
            var step = new RemovePasswordlessSudoStep();

            Assert.AreEqual(CustomizationPhase.PostBoot, step.Phase);
            Assert.AreEqual(StepPlatform.Linux, step.Platform);
            Assert.AreEqual(880, step.Order,
                "880 = after every provisioning step and after automation-user teardown (850), still inside the SSH window before DisableSsh (900)");
        }

        [TestMethod]
        public void RemoveStep_Gates_OnOmarchyTag()
        {
            var step = new RemovePasswordlessSudoStep();

            Assert.IsTrue(step.IsApplicable(TaggedItem(), new VmCustomizations()));
            Assert.IsFalse(step.IsApplicable(new GalleryItem { Name = "Ubuntu" }, new VmCustomizations()));
            Assert.IsFalse(step.IsApplicable(null, null));
        }

        [TestMethod]
        public async Task RemoveStep_Execute_RemovesDropIn_UsingTheDropInsOwnSudo()
        {
            var step = new RemovePasswordlessSudoStep();
            string? command = null;
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Callback<string, CancellationToken>((cmd, _) => command = cmd)
                  .ReturnsAsync("REMOVED");

            await step.ExecuteAsync(_shell.Object, TaggedItem(), new VmCustomizations(), _logger.Object, CancellationToken.None);

            Assert.IsNotNull(command);
            StringAssert.StartsWith(command, "sudo -n rm -f", "non-interactive sudo — authorized by the drop-in itself or the stock NOPASSWD cloud image");
            StringAssert.Contains(command, EnablePasswordlessSudoStep.DropInPath);
            _shell.Verify(s => s.RunCommandWithStdinAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
                "teardown never needs the password");
        }

        [TestMethod]
        public async Task RemoveStep_Execute_FailureToRemove_IsBestEffort()
        {
            var step = new RemovePasswordlessSudoStep();
            _shell.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("LEFT");

            // Must not throw — a leftover drop-in weakens sudo but never
            // breaks the deployment.
            await step.ExecuteAsync(_shell.Object, TaggedItem(), new VmCustomizations(), _logger.Object, CancellationToken.None);
        }

        // ── Order-collision guard ───────────────────────────────────────

        [TestMethod]
        public void StepOrders_DoNotCollideWithShippedSteps()
        {
            var shippedOrders = new (string Name, int Order)[]
            {
                ("EnablePasswordlessSudoStep", new EnablePasswordlessSudoStep().Order),
                ("RemoveVBoxGuestAdditionsStep", new RemoveVBoxGuestAdditionsStep().Order),
                ("RemoveAutomationUserStep", new RemoveAutomationUserStep().Order),
                ("RemovePasswordlessSudoStep", new RemovePasswordlessSudoStep().Order),
                ("DisableSshStep", new DisableSshStep().Order),
            };

            var orders = new HashSet<int>();
            foreach (var (name, order) in shippedOrders)
                Assert.IsTrue(orders.Add(order), $"order {order} collides with another step ({name})");

            Assert.IsTrue(new EnablePasswordlessSudoStep().Order < new RemoveVBoxGuestAdditionsStep().Order,
                "the bootstrap must run before the first sudo-based step");
            Assert.IsTrue(new RemoveAutomationUserStep().Order < new RemovePasswordlessSudoStep().Order,
                "teardown must run after automation-user removal");
            Assert.IsTrue(new RemovePasswordlessSudoStep().Order < new DisableSshStep().Order,
                "teardown must run before the SSH window closes");
        }
    }
}

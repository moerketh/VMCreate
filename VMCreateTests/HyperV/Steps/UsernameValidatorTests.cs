using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate;

namespace VMCreate.Tests.HyperV.Steps
{
    /// <summary>
    /// Tests for <see cref="UsernameValidator"/> — the gate for gallery-sourced
    /// usernames before they are substituted into root-run guest scripts.
    /// </summary>
    [TestClass]
    public sealed class UsernameValidatorTests
    {
        [DataTestMethod]
        [DataRow("user")]
        [DataRow("ubuntu")]
        [DataRow("parrot")]
        [DataRow("_service")]
        [DataRow("a")]
        [DataRow("user-2")]
        [DataRow("u1234567890123456789012345678901")] // 32 chars
        public void IsValidLinuxUsername_AcceptsValidNames(string name)
        {
            Assert.IsTrue(UsernameValidator.IsValidLinuxUsername(name), $"'{name}' should be valid");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("User")]           // uppercase
        [DataRow("1user")]          // starts with digit
        [DataRow("-user")]          // starts with hyphen
        [DataRow("user name")]      // whitespace
        [DataRow("user;rm")]        // shell metachar
        [DataRow("user'quoted")]    // quote
        [DataRow("user\"dq")]       // double quote
        [DataRow("user/../../x")]   // path separators
        [DataRow("$(whoami)")]      // command substitution
        [DataRow("user\nnewline")]  // newline injection
        [DataRow("u12345678901234567890123456789012")] // 33 chars, too long
        public void IsValidLinuxUsername_RejectsHostileOrInvalidNames(string name)
        {
            Assert.IsFalse(UsernameValidator.IsValidLinuxUsername(name), $"'{name}' must be rejected");
        }
    }
}
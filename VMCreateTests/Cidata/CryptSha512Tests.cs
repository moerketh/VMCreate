using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMCreate.Cidata;

namespace VMCreate.Tests.Cidata
{
    /// <summary>
    /// Tests for <see cref="CryptSha512"/>, the pure C# port of the "$6$" sha512-crypt
    /// algorithm. Expected values were generated with OpenSSL 3.5.7 ("openssl passwd -6")
    /// and cross-checked against Ulrich Drepper's published SHA-crypt reference vectors
    /// and John the Ripper's test vectors.
    /// </summary>
    [TestClass]
    public sealed class CryptSha512Tests
    {
        [TestMethod]
        public void Hash_WorldVector_MatchesOpenSsl()
        {
            var actual = CryptSha512.Hash("Hello world!", "saltstring");
            Assert.AreEqual(
                "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1",
                actual);
        }

        [TestMethod]
        public void Hash_DrepperSampleVector_MatchesOpenSsl()
        {
            var actual = CryptSha512.Hash("Hello world!", "saltstringsaltstring");
            Assert.AreEqual(
                "$6$saltstringsaltst$e.3mR68CqZEpesEX1HlFZT6sEanSOjM/b5UoDyDo00a8syek2cJldMjrbtKP86.FJvzluVR7nc3DNzelAwTxj.",
                actual);
        }

        [TestMethod]
        public void Hash_LongSalt_TruncatedTo16Characters()
        {
            // 22-char salt "toolongsaltstring" is truncated to the first 16 chars.
            var actual = CryptSha512.Hash("This is just a test", "toolongsaltstring");
            Assert.AreEqual(
                "$6$toolongsaltstrin$lQ8jolhgVRVhY4b5pZKaysCLi0QBxGoNeKQzQ3glMhwllF7oGDZxUhx1yxdYcz/e1JSbq3y6JMxxl8audkUEm0",
                actual);
        }

        [TestMethod]
        public void Hash_SaltTruncatesAtDollarSign()
        {
            // Per the spec, the salt is cut at the first '$' before the 16-char
            // truncation is applied; "abc$defghijklmnop" therefore uses "abc".
            // (openssl's CLI truncates its -salt argument to 16 chars ignoring '$',
            // so this vector was generated with plain salt "abc".)
            var actual = CryptSha512.Hash("test", "abc$defghijklmnop");
            Assert.AreEqual(
                "$6$abc$XdYVWINHFqAPqCkQNpBc64k/whmYeD3VETO0Hk79c4aGoQEvgiYKd/03lbc/XloI0fcMwRw.Rj.dZbn3LdPVz.",
                actual);
        }

        [TestMethod]
        public void Hash_ExplicitRounds_EmitsRoundsPrefix()
        {
            var actual = CryptSha512.Hash("a short string", "asaltof16chars..", 123456);
            Assert.AreEqual(
                "$6$rounds=123456$asaltof16chars..$BtCwjqMJGx5hrJhZywWvt0RLE8uZ4oPwcelCjmw2kSYu.Ec6ycULevoBK25fs2xXgMNrCzIMVcgEJAstJeonj1",
                actual);
        }

        [TestMethod]
        public void Hash_ExplicitRounds_ClampedToMinimum()
        {
            // Rounds below 1000 are clamped to 1000; the clamped value is emitted
            // because rounds were explicitly requested.
            var actual = CryptSha512.Hash("the minimum number is still observed", "roundstoolow", 10);
            Assert.AreEqual(
                "$6$rounds=1000$roundstoolow$kUMsbe306n21p9R.FRkW3IGn.S9NPN0x50YhH1xhLsPuWGsUSklZt58jaTfF4ZEQpyUNGc0dqbpBYYBaHHrsX.",
                actual);
        }

        [TestMethod]
        public void Hash_Rounds10000_Vector()
        {
            var actual = CryptSha512.Hash("Hello world!", "saltstringsaltstring", 10000);
            Assert.AreEqual(
                "$6$rounds=10000$saltstringsaltst$OW1/O6BYHV6BcXZu8QVeXbDWra3Oeqh0sbHbbMCVNSnCM/UrjmM0Dp8vOuZeHBy/YTBmSK6H9qs/y3RnOaw5v.",
                actual);
        }

        [TestMethod]
        public void Hash_Rounds1400_Vector()
        {
            var actual = CryptSha512.Hash("a very much longer password to use for testing", "anotherlongsaltstring", 1400);
            Assert.AreEqual(
                "$6$rounds=1400$anotherlongsalts$8RZJ4IrOAzu4H5Cd0Nc0Lcm9ErAINiiCV78olKkyOeY.f18drvcHwqJCDVZQ4NXWJbDBWMxjLyizDfhLd/P6c/",
                actual);
        }

        [TestMethod]
        public void Hash_EmptyPassword_MatchesOpenSsl()
        {
            var actual = CryptSha512.Hash(string.Empty, "ojWH1AiTee9x1peC");
            Assert.AreEqual(
                "$6$ojWH1AiTee9x1peC$QVEnTvRVlPRhcLQCk/HnHaZmlGAAjCfrAN0FtOsOnUk5K5Bn/9eLHHiRzrTzaIKjW9NTLNIBUCtNVOowWS2mN.",
                actual);
        }

        [TestMethod]
        public void Hash_PasswordWithSpecialChars_MatchesOpenSsl()
        {
            // From John the Ripper's sha512crypt test vectors.
            var actual = CryptSha512.Hash("*U*U*U*U", "OmBOuxFYBZCYAadG");
            Assert.AreEqual(
                "$6$OmBOuxFYBZCYAadG$WCckkSZok9xhp4U1shIZEV7CCVwQUwMVea7L3A77th6SaE9jOPupEMJB.z0vIWCDiN9WLh2m9Oszrj5G.gt330",
                actual);
        }

        [TestMethod]
        public void Hash_LongPassword_Crosses64ByteBlocks()
        {
            // An 80-byte password exercises the whole-key block loop (step 9)
            // plus the remainder handling (step 10).
            var password = new string('x', 80);
            var actual = CryptSha512.Hash(password, "crossblocktest");
            Assert.AreEqual(
                "$6$crossblocktest$Rs5pSljYCRjLiaKBiUrG2XPxAN3LiajEhwLKnu4APIcUu2pNlQr.EPgkzXsx/QaZ3QQZZEbBvGzIpqlRmGBM5.",
                actual);
        }

        [TestMethod]
        public void Hash_PasswordExactlyOneBlock_MatchesOpenSsl()
        {
            var password = new string('z', 64);
            var actual = CryptSha512.Hash(password, "oneblocktest");
            Assert.AreEqual(
                "$6$oneblocktest$lt/8G2qMhCTcQ4K5sgv0bvpFD8SErsdLkbqI8H6i0mm0aP3M4uPlgHkDC/5N6R690ALRg9.V37by7msYRh5.L1",
                actual);
        }

        [TestMethod]
        public void Hash_Utf8Password_MatchesOpenSsl()
        {
            // UTF-8 multibyte characters are hashed at the byte level.
            var actual = CryptSha512.Hash("pässwörd", "utf8test");
            Assert.AreEqual(
                "$6$utf8test$Oyj4H9FFapDPyrrBZIxXllVqIevhgyU2S66aOyygtc4CSF10Y/S0l8jAYNvQoB3i4GuEBYD2/MVPkUE80KO5x.",
                actual);
        }
    }
}
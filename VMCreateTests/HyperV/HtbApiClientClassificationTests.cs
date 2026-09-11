using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// Pins HtbApiClient.ClassifyContent — the classification helper that
    /// lets failure logs describe what a body looks like WITHOUT writing
    /// any of it into the plaintext %TEMP% log. These branches exist
    /// purely to prevent the leak: when a validity heuristic
    /// false-negatives on a REAL config, the log must receive
    /// '{Classification} ({Length} bytes)', never a body preview. A future
    /// refactor that turns a classifier back into a preview, or misroutes
    /// a body, must fail here rather than ship silently.
    /// </summary>
    [TestClass]
    public class HtbApiClientContentClassificationTests
    {
        [TestMethod]
        public void ClassifyContent_Null_ReportsEmpty()
        {
            Assert.AreEqual("empty", HtbApiClient.ClassifyContent(null));
        }

        [TestMethod]
        public void ClassifyContent_Whitespace_ReportsEmpty()
        {
            Assert.AreEqual("empty", HtbApiClient.ClassifyContent("   \r\n  \t "));
        }

        [TestMethod]
        public void ClassifyContent_EmptyString_ReportsEmpty()
        {
            Assert.AreEqual("empty", HtbApiClient.ClassifyContent(""));
        }

        [TestMethod]
        public void ClassifyContent_ObjectJson_ReportsValidJson()
        {
            Assert.AreEqual("valid JSON", HtbApiClient.ClassifyContent("{\"error\":\"invalid token\"}"));
        }

        [TestMethod]
        public void ClassifyContent_ArrayJson_ReportsValidJson()
        {
            Assert.AreEqual("valid JSON", HtbApiClient.ClassifyContent("[1,2,3]"));
        }

        [TestMethod]
        public void ClassifyContent_JsonWithLeadingWhitespace_ReportsValidJson()
        {
            Assert.AreEqual("valid JSON", HtbApiClient.ClassifyContent("  \n  {\"id\": 403}"));
        }

        [TestMethod]
        public void ClassifyContent_MalformedJson_FallsThroughToUnrecognized()
        {
            // Truncated/garbled body leading with '{': the bracket heuristic
            // alone is NOT a classification — JsonDocument.Parse fails and the
            // branch must fall through to 'unrecognized'. This mirrors the
            // real HTB failure mode (connection API error bodies start with
            // '{' but a truncated one is bracket-leading JSON garbage).
            Assert.AreEqual("unrecognized",
                HtbApiClient.ClassifyContent("{\"error\": \"invalid\", \"detail\": "));
        }

        [TestMethod]
        public void ClassifyContent_HtmlDoctype_ReportsHtml()
        {
            Assert.AreEqual("HTML", HtbApiClient.ClassifyContent("<!DOCTYPE html><html><body>403</body></html>"));
        }

        [TestMethod]
        public void ClassifyContent_HtmlTag_ReportsHtml()
        {
            Assert.AreEqual("HTML", HtbApiClient.ClassifyContent("<html><body>login</body></html>"));
        }

        [TestMethod]
        public void ClassifyContent_HtmlLeadingWhitespace_ReportsHtml()
        {
            Assert.AreEqual("HTML", HtbApiClient.ClassifyContent("  <html><body>login</body></html>"));
        }

        [TestMethod]
        public void ClassifyContent_HtmlCaseInsensitive_ReportsHtml()
        {
            Assert.AreEqual("HTML", HtbApiClient.ClassifyContent("<HTML><BODY>LOGIN</BODY></HTML>"));
        }

        /// <summary>
        /// THE branch that exists purely to prevent the leak. An .ovpn config
        /// embeds the private key inline (-----BEGIN ... PRIVATE KEY-----);
        /// if a validity heuristic ever false-negatives on a real config,
        /// this classification — never a preview — is what the log receives.
        /// </summary>
        [TestMethod]
        public void ClassifyContent_PemKeyMaterial_ReportsPemKeyMaterial()
        {
            string realisticOvpnHeader =
                "-----BEGIN OPENVPN STATIC KEY V1-----\n" +
                "a1b2c3d4e5f60718\n" +
                "-----END OPENVPN STATIC KEY V1-----";
            Assert.AreEqual("PEM/key material", HtbApiClient.ClassifyContent(realisticOvpnHeader));
        }

        [TestMethod]
        public void ClassifyContent_PemCertificate_ReportsPemKeyMaterial()
        {
            Assert.AreEqual("PEM/key material",
                HtbApiClient.ClassifyContent("-----BEGIN CERTIFICATE-----\nMIIB...\n-----END CERTIFICATE-----"));
        }

        [TestMethod]
        public void ClassifyContent_PemWithLeadingWhitespace_ReportsPemKeyMaterial()
        {
            // Leading whitespace is extremely common when a chunked transfer
            // or proxy injects stray CR/LF before the real body.
            Assert.AreEqual("PEM/key material",
                HtbApiClient.ClassifyContent(" \r\n-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----"));
        }

        [TestMethod]
        public void ClassifyContent_OtherwiseUnrecognized()
        {
            Assert.AreEqual("unrecognized", HtbApiClient.ClassifyContent("plain text error from a proxy"));
        }

        [TestMethod]
        public void ClassifyContent_SeparatorDashes_ReportsUnrecognized()
        {
            // A decorative '-----' rule with no BEGIN keyword must not be
            // classified as key material.
            Assert.AreEqual("unrecognized", HtbApiClient.ClassifyContent("----- section break -----"));
        }

        [TestMethod]
        public void ClassifyContent_NearPemPrefix_ConservativelyReportsPemKeyMaterial()
        {
            // '-----BEGINX' DOES match the '-----BEGIN' probe. That is
            // deliberate and it is the SAFE direction: over-classifying a
            // non-key body as 'PEM/key material' can never leak content (it
            // still logs only a classification), while under-classifying a
            // real key could. The test pins the conservative direction so a
            // future 'tightening' of the probe is a conscious decision.
            Assert.AreEqual("PEM/key material", HtbApiClient.ClassifyContent("-----BEGINX"));
        }
    }
}
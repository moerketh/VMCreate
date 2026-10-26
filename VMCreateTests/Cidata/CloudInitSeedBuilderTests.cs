using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;

namespace VMCreate.Tests.Cidata
{
    [TestClass]
    public sealed class CloudInitSeedBuilderTests
    {
        [TestMethod]
        public void BuildSeedIso_IsReadableByCloudInit()
        {
            string isoPath = Path.Combine(Path.GetTempPath(), "vmcreate-cloudinit-test-" + Guid.NewGuid().ToString("N") + ".iso");
            try
            {
                try
                {
                    CloudInitSeedBuilder.BuildSeedIso(isoPath, "t-seed", "ssh-ed25519 AAAAtest vmcreate", NullLogger.Instance);
                }
                catch (Exception ex) when (ex is not AssertFailedException)
                {
                    // IMAPI2 is unavailable in some CI containers.
                    Assert.Inconclusive($"IMAPI2 unavailable: {ex.Message}");
                    return;
                }

                NoCloudSeedAssert.IsCloudInitReadable(isoPath);
            }
            finally
            {
                try { File.Delete(isoPath); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void BuildSeedIso_WithCustomUserData_IsMultipartAndReadableByCloudInit()
        {
            // The --cloud-init deploy: the seed's user-data is a MIME
            // multipart whose first part is the user's script and whose
            // second part is the built-in cloud-config block. NoCloud itself
            // has no format opinion — cloud-init's UserDataProcessor walks
            // MIME natively — but the ISO must still be ISO9660+Joliet with
            // no UDF descriptor or ds-identify skips the whole seed.
            string isoPath = Path.Combine(Path.GetTempPath(), "vmcreate-cloudinit-test-" + Guid.NewGuid().ToString("N") + ".iso");
            try
            {
                try
                {
                    CloudInitSeedBuilder.BuildSeedIso(
                        isoPath, "t-seed", "ssh-ed25519 AAAAtest vmcreate", NullLogger.Instance,
                        "#!/bin/sh\necho custom-part-ran\n");
                }
                catch (Exception ex) when (ex is not AssertFailedException)
                {
                    Assert.Inconclusive($"IMAPI2 unavailable: {ex.Message}");
                    return;
                }

                NoCloudSeedAssert.IsCloudInitReadable(isoPath);
                Assert.IsTrue(File.ReadAllBytes(isoPath).Length > 0);
            }
            finally
            {
                try { File.Delete(isoPath); } catch { /* best effort */ }
            }
        }

        // ── Multipart framing ──────────────────────────────────────────────

        [TestMethod]
        public void WithoutCustomUserData_UserDataIsPlainCloudConfig()
        {
            // Regression pin: the no-custom seed stays the plain single
            // #cloud-config document every tagged (Elastic SIEM) deployment
            // already boots on — no MIME framing appears out of nowhere.
            string userData = CloudInitSeedBuilder.BuildUserData("ssh-ed25519 AAAAtest vmcreate");

            Assert.IsTrue(userData.StartsWith("#cloud-config", StringComparison.Ordinal),
                "cloud-init ignores user-data without the header");
            StringAssert.Contains(userData, "name: vmcreate");
            StringAssert.Contains(userData, "ssh-ed25519 AAAAtest vmcreate");
            Assert.IsFalse(userData.Contains("MIME-Version", StringComparison.Ordinal),
                "no MIME framing without custom content");
        }

        [TestMethod]
        public void CustomUserData_MergesUserPartAheadOfBuiltinCloudConfig()
        {
            // Order is load-bearing: cloud-init merges cloud-config parts in
            // document order with dict(replace), so the built-in block must
            // be the SECOND part to win conflicting keys while the user's
            // non-colliding keys apply verbatim.
            string multipart = CloudInitSeedBuilder.BuildMultipartUserData(
                "ssh-ed25519 AAAAtest vmcreate",
                "#cloud-config\npackages: [htop]\nusers:\n  - name: evil\n");

            Assert.IsTrue(multipart.StartsWith("MIME-Version: 1.0\n", StringComparison.Ordinal));
            StringAssert.Contains(multipart, "Content-Type: multipart/mixed; boundary=");
            Assert.IsTrue(multipart.Contains("Content-Type: text/plain; charset=\"utf-8\"", StringComparison.Ordinal));
            StringAssert.Contains(multipart, "Content-Disposition: attachment; filename=\"user_cloud_init.txt\"");
            Assert.IsTrue(multipart.Contains("Content-Type: text/cloud-config", StringComparison.Ordinal));
            StringAssert.Contains(multipart, "Content-Disposition: attachment; filename=\"vmcreate_builtin.yaml\"");

            int userPart = multipart.IndexOf("user_cloud_init.txt", StringComparison.Ordinal);
            int builtinPart = multipart.IndexOf("vmcreate_builtin.yaml", StringComparison.Ordinal);
            Assert.IsTrue(userPart >= 0 && builtinPart > userPart,
                "the user part must precede the built-in cloud-config part");

            // Both parts' payloads survive intact.
            StringAssert.Contains(multipart, "packages: [htop]");
            StringAssert.Contains(multipart, "name: vmcreate");
        }

        [TestMethod]
        public void CustomUserData_PayloadEndsInsidePartFraming()
        {
            // A part whose payload does not end with a newline must get one:
            // the closing delimiter must be on its own line or the payload
            // would swallow it into the last command.
            string multipart = CloudInitSeedBuilder.BuildMultipartUserData(
                "ssh-ed25519 AAAAtest vmcreate", "#!/bin/sh\necho no-trailing-newline");
            StringAssert.Contains(multipart, "echo no-trailing-newline\n");
            Assert.IsTrue(multipart.EndsWith("--\n", StringComparison.Ordinal),
                "the document ends with the closing delimiter");
        }

        [TestMethod]
        public void CustomUserData_BoundaryIsAbsentFromBothParts()
        {
            // Adversarial payload: a shell script that literally contains
            // MIME headers and a boundary-looking line. The chosen boundary
            // must not collide with any of it, or the document would split
            // early and hand cloud-init truncated parts.
            string adversarial =
                "#!/bin/sh\n" +
                "cat >/tmp/x <<EOF\n" +
                "MIME-Version: 1.0\n" +
                "Content-Type: multipart/mixed; boundary=\"==vmcreate-should-not-match\"\n" +
                "--==vmcreate-should-not-match\n" +
                "EOF\n";
            string multipart = CloudInitSeedBuilder.BuildMultipartUserData(
                "ssh-ed25519 AAAAtest vmcreate", adversarial);

            int bidx = multipart.IndexOf("boundary=\"", StringComparison.Ordinal) + "boundary=\"".Length;
            string boundary = multipart[bidx..multipart.IndexOf('"', bidx)];
            Assert.IsTrue(boundary.StartsWith("==vmcreate", StringComparison.Ordinal));
            Assert.IsFalse(adversarial.Contains(boundary, StringComparison.Ordinal),
                "the boundary must be absent from the user payload");
        }

        [TestMethod]
        public void CustomUserData_LfOnly_EvenFromCrLfFile()
        {
            // Files authored on Windows arrive with CRLF; LoadUserDataFile
            // normalizes to LF — functionally required, not cosmetic: a
            // Linux kernel reading "#!/bin/sh\r" fails exec (interpreter
            // "sh\r" not found) and a CRLF cloud-config breaks YAML
            // string values.
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "#!/bin/sh\r\necho hi\r\n");
                string content = CloudInitSeedBuilder.LoadUserDataFile(path);
                StringAssert.Contains(content, "#!/bin/sh\necho hi\n");
                Assert.IsFalse(content.Contains('\r'),
                    "the seed must not carry CR bytes into the guest");

                // And the multipart envelope wraps the normalized payload.
                string multipart = CloudInitSeedBuilder.BuildMultipartUserData(
                    "ssh-ed25519 AAAAtest vmcreate", content);
                StringAssert.Contains(multipart, "#!/bin/sh\necho hi\n");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        // ── LoadUserDataFile validation ────────────────────────────────────

        [TestMethod]
        public void LoadUserDataFile_MissingFile_ThrowsWithThePath()
        {
            string path = Path.Combine(Path.GetTempPath(), "vmcreate-no-such-" + Guid.NewGuid().ToString("N") + ".yaml");
            var ex = Assert.Throws<FileNotFoundException>(() => CloudInitSeedBuilder.LoadUserDataFile(path));
            StringAssert.Contains(ex.Message, path);
        }

        [TestMethod]
        public void LoadUserDataFile_EmptyFile_Throws()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "   \n\n");
                var ex = Assert.Throws<ArgumentException>(() => CloudInitSeedBuilder.LoadUserDataFile(path));
                StringAssert.Contains(ex.Message, "empty");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [TestMethod]
        public void LoadUserDataFile_UnrecognizedMarker_ThrowsWithTheValidOnes()
        {
            // cloud-init silently ignores parts it cannot type — a typo'd
            // user-data file must fail loudly here instead.
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "apt-get install htop\n");
                var ex = Assert.Throws<ArgumentException>(() => CloudInitSeedBuilder.LoadUserDataFile(path));
                StringAssert.Contains(ex.Message, "#cloud-config");
                StringAssert.Contains(ex.Message, "#!");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [TestMethod]
        public void LoadUserDataFile_MultipartFile_Throws()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    "MIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=\"x\"\n\n--x\n");
                var ex = Assert.Throws<ArgumentException>(() => CloudInitSeedBuilder.LoadUserDataFile(path));
                StringAssert.Contains(ex.Message, "multipart");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [TestMethod]
        public void LoadUserDataFile_MimeTextEmbeddedMidFile_IsFine()
        {
            // A script that merely prints MIME-looking lines mid-file is a
            // valid shell script — only a document that LEADS with MIME
            // headers is an actual multipart document.
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    "#!/bin/sh\necho 'Content-Type: multipart/mixed; boundary=\"x\"'\n");
                string content = CloudInitSeedBuilder.LoadUserDataFile(path);
                StringAssert.Contains(content, "boundary=\"x\"");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [TestMethod]
        public void LoadUserDataFile_AcceptsEveryRecognizedMarker()
        {
            string[] markers =
            {
                "#!/bin/sh\necho hi\n",
                "#cloud-config\npackages: [htop]\n",
                "#include\nhttp://example.com/x\n",
                "#include-once\nhttp://example.com/x\n",
                "#cloud-boothook\necho hook\n",
                "#cloud-config-archive\n- content: |\n    hi\n",
                "#cloud-config-jsonp\n[\n  { \"op\": \"add\" }\n]\n",
                "#part-handler\n#!/usr/bin/env python3\n",
                "## template: jinja\n#cloud-config\nhostname: {{ ds.meta_data.hostname }}\n",
            };

            foreach (string marker in markers)
            {
                string path = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(path, marker);
                    string content = CloudInitSeedBuilder.LoadUserDataFile(path);
                    Assert.AreEqual(marker, content, $"marker {marker[..marker.IndexOf('\n')]} must round-trip");
                }
                finally
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }

        [TestMethod]
        public void LoadUserDataFile_TrimsBomAndLeadingBlankLines()
        {
            // cloud-init lstrips whitespace before prefix-sniffing, but a
            // leading blank line plus a BOM is exactly the shape a Windows
            // editor produces; normalize so the marker is line 1.
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "\uFEFF\n\n#cloud-config\npackages: [htop]\n", new UTF8Encoding(true));
                string content = CloudInitSeedBuilder.LoadUserDataFile(path);
                Assert.IsTrue(content.StartsWith("#cloud-config", StringComparison.Ordinal));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}

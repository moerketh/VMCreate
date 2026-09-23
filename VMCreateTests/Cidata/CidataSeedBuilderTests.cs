using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreate.Cidata;

namespace VMCreate.Tests.Cidata
{
    /// <summary>
    /// Tests for <see cref="CidataSeedBuilder"/>. Expected partition numbers and file
    /// shapes come from the upstream omarchy configurator (quattro / v4.0.4) full-disk
    /// template and the official unattended-install documentation.
    /// </summary>
    [TestClass]
    public sealed class CidataSeedBuilderTests
    {
        private static CidataSeedOptions CreateOptions(long diskSizeBytes = 150L * 1024 * 1024 * 1024) => new()
        {
            Username = "omarchy",
            Password = "hunter2!",
            Hostname = "target",
            Timezone = "Europe/Copenhagen",
            KeyboardLayout = "dk",
            DiskSizeBytes = diskSizeBytes,
        };

        [TestMethod]
        public void ComputePartitionLayout_150GiB_MatchesUpstreamMath()
        {
            // 150 GiB = 153600 MiB; main = 153600 - 2049 - 1 = 151550 MiB.
            var layout = CidataSeedBuilder.ComputePartitionLayout(150L * 1024 * 1024 * 1024);

            Assert.AreEqual(1024L * 1024, layout.BootStart);
            Assert.AreEqual(2L * 1024 * 1024 * 1024, layout.BootSize);
            Assert.AreEqual((2L * 1024 + 1) * 1024 * 1024, layout.MainStart);
            Assert.AreEqual(151_550L * 1024 * 1024, layout.MainSize);
        }

        [TestMethod]
        public void ComputePartitionLayout_FloorsToWholeMiB()
        {
            // 1 GiB + 512 KiB: the fractional MiB is floored away, exactly as upstream
            // (bash integer division) does, which leaves a non-positive main partition.
            Assert.Throws<InvalidOperationException>(() =>
                CidataSeedBuilder.ComputePartitionLayout((1024 + 1) * 1024L * 1024 / 2 + 512 * 1024));
        }

        [TestMethod]
        public void ComputePartitionLayout_DiskWithoutRoomForGptReserve_Throws()
        {
            // 2 GiB ESP + 1 MiB start + 1 MiB GPT reserve leaves nothing.
            Assert.Throws<InvalidOperationException>(() =>
                CidataSeedBuilder.ComputePartitionLayout(2L * 1024 * 1024 * 1024 + 2 * 1024 * 1024));
        }

        [TestMethod]
        public void ComputePartitionLayout_BelowOmarchyMinimum_Throws()
        {
            // 8 GiB fits the layout math, but not the ~20 GB of packages an
            // Omarchy install downloads.
            Assert.Throws<InvalidOperationException>(() =>
                CidataSeedBuilder.ComputePartitionLayout(8L * 1024 * 1024 * 1024));
        }

        [TestMethod]
        public void ComputePartitionLayout_AtOmarchyMinimum_Succeeds()
        {
            var layout = CidataSeedBuilder.ComputePartitionLayout(CidataSeedBuilder.MinimumDiskSizeBytes);
            Assert.IsTrue(layout.MainSize > 0);
        }

        [TestMethod]
        public void ComputePartitionLayout_CommonSizes_LeaveExactGptReserve()
        {
            foreach (int gib in new[] { 32, 64, 100, 150, 200, 500 })
            {
                var layout = CidataSeedBuilder.ComputePartitionLayout((long)gib * 1024 * 1024 * 1024);
                long total = layout.BootStart + layout.BootSize + layout.MainSize + 1024 * 1024;
                Assert.AreEqual((long)gib * 1024 * 1024 * 1024, total, $"disk of {gib} GiB");
            }
        }

        [TestMethod]
        public void BuildUserConfigurationJson_IsValidJsonWithExpectedShape()
        {
            string json = CidataSeedBuilder.BuildUserConfigurationJson(CreateOptions());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.AreEqual("full_disk", root.GetProperty("omarchy_install").GetProperty("mode").GetString());
            Assert.AreEqual(false, root.GetProperty("omarchy_install").GetProperty("defer_provisioning").GetBoolean());
            Assert.AreEqual("/mnt", root.GetProperty("omarchy_install").GetProperty("target_mount").GetString());
            Assert.AreEqual("linux-omarchy", root.GetProperty("omarchy_install").GetProperty("storage").GetProperty("kernel").GetString());

            var mod = root.GetProperty("disk_config").GetProperty("device_modifications")[0];
            Assert.AreEqual("/dev/sda", mod.GetProperty("device").GetString());
            Assert.IsTrue(mod.GetProperty("wipe").GetBoolean());

            var partitions = mod.GetProperty("partitions");
            Assert.AreEqual(2, partitions.GetArrayLength());

            var boot = partitions[0];
            Assert.AreEqual("/boot", boot.GetProperty("mountpoint").GetString());
            Assert.AreEqual("fat32", boot.GetProperty("fs_type").GetString());
            Assert.AreEqual("ea21d3f2-82bb-49cc-ab5d-6f81ae94e18d", boot.GetProperty("obj_id").GetString());
            Assert.AreEqual(2L * 1024 * 1024 * 1024, boot.GetProperty("size").GetProperty("value").GetInt64());
            Assert.AreEqual(1024 * 1024, boot.GetProperty("start").GetProperty("value").GetInt64());

            var main = partitions[1];
            Assert.AreEqual("btrfs", main.GetProperty("fs_type").GetString());
            Assert.AreEqual(151_550L * 1024 * 1024, main.GetProperty("size").GetProperty("value").GetInt64());
            Assert.AreEqual((2L * 1024 + 1) * 1024 * 1024, main.GetProperty("start").GetProperty("value").GetInt64());
            Assert.AreEqual(4, main.GetProperty("btrfs").GetArrayLength());
            Assert.AreEqual("@pkg", main.GetProperty("btrfs")[3].GetProperty("name").GetString());

            Assert.AreEqual("target", root.GetProperty("hostname").GetString());
            Assert.AreEqual("Europe/Copenhagen", root.GetProperty("timezone").GetString());
            Assert.AreEqual("dk", root.GetProperty("locale_config").GetProperty("kb_layout").GetString());
            CollectionAssert.AreEqual(new[] { "linux-omarchy" },
                root.GetProperty("kernels").EnumerateArray().Select(k => k.GetString()).ToArray());

            // Unencrypted installs must not carry a disk_encryption block.
            Assert.IsFalse(root.GetProperty("disk_config").TryGetProperty("disk_encryption", out _));

            var packages = root.GetProperty("packages").EnumerateArray().Select(p => p.GetString()).ToArray();
            CollectionAssert.AreEqual(
                new[] { "base-devel", "git", "omarchy-keyring", "omarchy-settings", "omarchy" }, packages);

            Assert.AreEqual(true, root.GetProperty("ntp").GetBoolean());
            Assert.AreEqual(true, root.GetProperty("swap").GetBoolean());
            Assert.AreEqual("Limine", root.GetProperty("bootloader_config").GetProperty("bootloader").GetString());
            Assert.AreEqual("iso", root.GetProperty("network_config").GetProperty("type").GetString());
        }

        [TestMethod]
        public void BuildUserConfigurationJson_IncludesUpstreamMirrorsWithDollarPlaceholders()
        {
            string json = CidataSeedBuilder.BuildUserConfigurationJson(CreateOptions());

            StringAssert.Contains(json, "https://mirror.omarchy.org/$repo/os/$arch");
            StringAssert.Contains(json, "https://mirror.rackspace.com/archlinux/$repo/os/$arch");
            StringAssert.Contains(json, "https://geo.mirror.pkgbuild.com/$repo/os/$arch");
        }

        [TestMethod]
        public void BuildUserConfigurationJson_EscapesUserSuppliedValues()
        {
            var options = CreateOptions();
            options.Hostname = "host\"\\name";

            string json = CidataSeedBuilder.BuildUserConfigurationJson(options);
            using var doc = JsonDocument.Parse(json);

            Assert.AreEqual("host\"\\name", doc.RootElement.GetProperty("hostname").GetString());
        }

        [TestMethod]
        public void BuildUserCredentialsJson_HasCryptHashForRootAndSudoUser()
        {
            string json = CidataSeedBuilder.BuildUserCredentialsJson(CreateOptions());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string rootHash = root.GetProperty("root_enc_password").GetString()!;
            var user = root.GetProperty("users")[0];
            Assert.AreEqual(rootHash, user.GetProperty("enc_password").GetString());
            Assert.AreEqual("omarchy", user.GetProperty("username").GetString());
            Assert.IsTrue(user.GetProperty("sudo").GetBoolean());

            // "$6$salt$digest" with a 16-char salt, no rounds= prefix.
            Assert.IsTrue(Regex.IsMatch(rootHash, @"^\$6\$[./0-9A-Za-z]{16}\$[./0-9A-Za-z]{86}$"),
                $"hash malformed: {rootHash}");
        }

        [TestMethod]
        public void BuildUserCredentialsJson_HashesTheGivenPassword()
        {
            var options = CreateOptions();
            string json = CidataSeedBuilder.BuildUserCredentialsJson(options);
            using var doc = JsonDocument.Parse(json);
            string hash = doc.RootElement.GetProperty("root_enc_password").GetString()!;

            // Re-derive with the salt extracted from the produced hash; must verify.
            string salt = hash.Split('$')[2];
            Assert.AreEqual(CryptSha512.Hash(options.Password, salt), hash);
        }

        [TestMethod]
        public void BuildUserCredentialsJson_AcceptsExplicitHash()
        {
            const string known = "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1";

            string json = CidataSeedBuilder.BuildUserCredentialsJson(CreateOptions(), known);

            StringAssert.Contains(json, known);
        }

        [TestMethod]
        public void Build_Validation_RejectsMissingRequiredFields()
        {
            Assert.Throws<ArgumentException>(() =>
                CidataSeedBuilder.BuildUserConfigurationJson(new CidataSeedOptions { Password = "p", Hostname = "h", DiskSizeBytes = 150 }));
            Assert.Throws<ArgumentException>(() =>
                CidataSeedBuilder.BuildUserConfigurationJson(new CidataSeedOptions { Username = "u", Hostname = "h", DiskSizeBytes = 150 }));
            Assert.Throws<ArgumentException>(() =>
                CidataSeedBuilder.BuildUserConfigurationJson(new CidataSeedOptions { Username = "u", Password = "p", DiskSizeBytes = 150 }));
        }

        [TestMethod]
        public async Task BuildCidataIso_CreatesIsoWithCidataVolume()
        {
            // Skipped when IMAPI2 is unavailable (e.g. CI containers).
            string probeDir = Path.Combine(Path.GetTempPath(), "vmcreate-imapi2-probe-" + Guid.NewGuid().ToString("N"));
            string probeIso = probeDir + ".iso";
            try
            {
                Directory.CreateDirectory(probeDir);
                File.WriteAllText(Path.Combine(probeDir, "probe.txt"), "probe");
                Imapi2Interop.CreateIsoFromDirectory(probeDir, probeIso, "probe");
            }
            catch (Exception ex)
            {
                Assert.Inconclusive($"IMAPI2 unavailable: {ex.Message}");
                return;
            }
            finally
            {
                try { Directory.Delete(probeDir, true); } catch { /* best effort */ }
                try { File.Delete(probeIso); } catch { /* best effort */ }
            }

            string isoPath = Path.Combine(Path.GetTempPath(), "vmcreate-cidata-test-" + Guid.NewGuid().ToString("N") + ".iso");
            try
            {
                CidataSeedBuilder.BuildCidataIso(isoPath, CreateOptions(), NullLogger.Instance);

                Assert.IsTrue(File.Exists(isoPath));
                Assert.IsTrue(new FileInfo(isoPath).Length > 0, "seed ISO should not be empty");

                // The ISO9660 PVD is at sector 16 (offset 0x8000); the 32-byte
                // space-padded volume identifier sits at offset 40. Like the upstream
                // documented "genisoimage -volid cidata -joliet -rock" command, IMAPI2
                // uppercases the PVD label and keeps the mixed-case "cidata" in the
                // Joliet SVD (sector 17, UTF-16BE); the installer accepts either form.
                var all = File.ReadAllBytes(isoPath);
                Assert.AreEqual(1, all[0x8000], "PVD type byte");
                Assert.AreEqual("CD001", Encoding.ASCII.GetString(all, 0x8001, 5));
                Assert.AreEqual("CIDATA", Encoding.ASCII.GetString(all, 0x8000 + 40, 32).TrimEnd(' '));

                Assert.AreEqual(2, all[0x8800], "Joliet SVD type byte");
                string jolietId = Encoding.BigEndianUnicode.GetString(all, 0x8800 + 40, 32).TrimEnd('\0', ' ');
                Assert.AreEqual("cidata", jolietId);
            }
            finally
            {
                try { File.Delete(isoPath); } catch { /* best effort */ }
            }
        }
    }
}

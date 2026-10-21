using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using VMCreate.HyperV.VmCreation;

namespace VMCreate.Tests.HyperV.VmCreation
{
    /// <summary>
    /// Pins the disk-full pre-flight. The shipped failure this guards
    /// against: the MBR-clone flow filled drive D: mid-clone, Hyper-V
    /// PAUSED the VM ("Disk Full", event 12635/18524), the vsock channel
    /// died, and the deployment surfaced as an SSH keepalive timeout —
    /// nothing in the log said "disk full". The guard must fail the
    /// deployment BEFORE the ISO cycle, with the drive letter and the
    /// free/required numbers in the message.
    /// </summary>
    [TestClass]
    public class VmDiskSpaceGuardTests
    {
        private static Func<string, (long FreeBytes, string DriveName)> Probe(long freeBytes) => _ => (freeBytes, "D:\\");

        [TestMethod]
        public void EnoughSpace_PassesSilently()
        {
            VmDiskSpaceGuard.EnsureAvailable(@"D:\Hyper-V\ disks", 10_000, Probe(long.MaxValue));
        }

        [TestMethod]
        public void TooLittleSpace_FailsFast_WithNumbersAndDriveInMessage()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                VmDiskSpaceGuard.EnsureAvailable(@"D:\x", 10_000, Probe(4_000)));

            StringAssert.Contains(ex.Message, "D:\\");
            StringAssert.Contains(ex.Message, "free");
            StringAssert.Contains(ex.Message, "required");
            StringAssert.Contains(ex.Message, "free up space", StringComparison.OrdinalIgnoreCase);
        }

        [TestMethod]
        public void ExactlyTheRequirement_Passes()
        {
            // Boundary: >= required is enough (headroom is already baked in).
            VmDiskSpaceGuard.EnsureAvailable(@"D:\x", 10_000, Probe(10_000));
        }

        [TestMethod]
        public void RequiredBytes_CloneFlow_DoublesSourcePlusHeadroom()
        {
            // cloneWritesSecondDisk: the ISO cycle partclones a full second
            // disk next to the source.
            long required = VmDiskSpaceGuard.RequiredBytes("media.vhdx", cloneWritesSecondDisk: true);
            Assert.AreEqual(VmDiskSpaceGuard.HeadroomBytes, required,
                "missing media file: headroom only (no crash on absent files)");
        }

        [TestMethod]
        public void RequiredBytes_CustomizeFlow_HeadroomOnly()
        {
            // Gen2 customize attaches the prepared image as-is — no second copy.
            long required = VmDiskSpaceGuard.RequiredBytes("media.vhdx", cloneWritesSecondDisk: false);
            Assert.AreEqual(VmDiskSpaceGuard.HeadroomBytes, required);
        }

        [TestMethod]
        public void RequiredBytes_WithRealMediaFile_IncludesSourceSize()
        {
            string file = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllBytes(file, new byte[1024]);
                long required = VmDiskSpaceGuard.RequiredBytes(file, cloneWritesSecondDisk: true);
                Assert.AreEqual(1024 + VmDiskSpaceGuard.HeadroomBytes, required);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [TestMethod]
        public void ProbeFailure_DoesNotBlock()
        {
            // A probe that cannot read the drive reports long.MaxValue (see
            // the guard's DefaultProbe) — the deployment proceeds rather
            // than failing on a probe hiccup.
            VmDiskSpaceGuard.EnsureAvailable(@"Q:\nonexistent\path", 1, path => (long.MaxValue, "Q:\\"));
        }
    }
}

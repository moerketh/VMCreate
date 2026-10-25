using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;

namespace VMCreate.Tests.Cidata
{
    /// <summary>
    /// Shared checks for NoCloud seed ISOs. cloud-init's NoCloud datasource
    /// only scans devices blkid reports as TYPE=vfat or TYPE=iso9660; an
    /// image that also carries UDF is reported as TYPE=udf and its user-data
    /// is silently ignored (IMAPI2 adds UDF unless told not to).
    /// </summary>
    internal static class NoCloudSeedAssert
    {
        private static readonly string[] UdfIdentifiers = { "BEA01", "NSR02", "NSR03" };

        public static void IsCloudInitReadable(string isoPath)
        {
            byte[] all = File.ReadAllBytes(isoPath);
            Assert.AreEqual("CD001", Encoding.ASCII.GetString(all, 0x8001, 5), "seed must be ISO9660");

            // The UDF volume recognition sequence sits in the descriptor area
            // from sector 16 onward, after the ISO9660 descriptors.
            string descriptors = Encoding.ASCII.GetString(all, 0x8000, Math.Min(all.Length - 0x8000, 32 * 2048));
            foreach (string udfId in UdfIdentifiers)
                Assert.IsFalse(descriptors.Contains(udfId),
                    $"seed ISO carries a UDF descriptor ({udfId}): blkid reports TYPE=udf and cloud-init ignores the seed");
        }
    }
}

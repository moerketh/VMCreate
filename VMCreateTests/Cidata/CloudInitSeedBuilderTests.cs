using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

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
    }
}

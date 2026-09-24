using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VMCreate.Gallery;

namespace VMCreate.Tests.Gallery
{
    /// <summary>
    /// Round-trip tests for the on-disk gallery cache: release variants are
    /// part of the item payload (a cached multi-release item must still show
    /// its release picker after a cold start) and a legacy cache that predates
    /// variants must simply deserialize items without them.
    /// </summary>
    [TestClass]
    public sealed class GalleryCacheRoundTripTests
    {
        private static string TempCachePath() =>
            Path.Combine(Path.GetTempPath(), $"vmcreate-gallery-{Guid.NewGuid():N}.json");

        private static (GalleryCache Cache, string Path) NewCache()
        {
            var path = TempCachePath();
            var cache = new GalleryCache(
                NullLogger<GalleryCache>.Instance,
                Options.Create(new AppSettings { GalleryCachePath = path }));
            return (cache, path);
        }

        private static GalleryItem MultiReleaseKali() => new()
        {
            Name = "Kali Linux",
            Publisher = "Offensive Security",
            DiskUri = "https://example.com/kali-2026.2.qcow2",
            Version = "2026.2",
            LinuxDistro = LinuxDistro.Kali,
            ReleaseVariants = new List<ReleaseVariant>
            {
                new()
                {
                    Id = "stable",
                    DisplayName = "Stable (2026.2)",
                    DiskUri = "https://example.com/kali-2026.2.qcow2",
                    Version = "2026.2",
                    LastUpdated = "2026-03-01",
                    DownloadSizeText = "3.5G",
                    IsDefault = true,
                },
                new()
                {
                    Id = "weekly",
                    DisplayName = "Weekly (2026-W38)",
                    Description = "untested build with the latest updates",
                    DiskUri = "https://example.com/kali-weekly-W38.qcow2",
                    Version = "2026-W38",
                    LastUpdated = "2026-09-18",
                    DownloadSizeText = "3.7G",
                },
            },
        };

        [TestMethod]
        public void SaveThenLoad_PreservesReleaseVariants()
        {
            var (cache, path) = NewCache();
            try
            {
                cache.SaveCache(new List<GalleryItem>
                {
                    MultiReleaseKali(),
                    new() { Name = "Ubuntu", DiskUri = "https://example.com/u.qcow2", Version = "24.04" },
                });

                Assert.IsTrue(cache.TryLoadCache(out var loaded), "a fresh cache is within its TTL");
                var kali = loaded!.Single(i => i.Name == "Kali Linux");

                Assert.AreEqual(2, kali.ReleaseVariants!.Count, "variants must survive the JSON round trip");

                var stable = kali.ReleaseVariants.Single(v => v.Id == "stable");
                Assert.IsTrue(stable.IsDefault);
                Assert.AreEqual("2026.2", stable.Version);
                Assert.AreEqual("3.5G", stable.DownloadSizeText);

                var weekly = kali.ReleaseVariants.Single(v => v.Id == "weekly");
                Assert.AreEqual("2026-W38", weekly.Version);
                Assert.AreEqual("2026-09-18", weekly.LastUpdated);
                Assert.AreEqual("untested build with the latest updates", weekly.Description);
                Assert.AreEqual("https://example.com/kali-weekly-W38.qcow2", weekly.DiskUri);

                // The cached item must still resolve releases like a fresh one.
                Assert.AreSame(kali, kali.ResolveRelease(null));
                Assert.AreEqual("2026-W38", kali.ResolveRelease("weekly")!.Version);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void LegacyCacheWithoutVariants_LoadsWithNullVariants()
        {
            var (cache, path) = NewCache();
            try
            {
                // Hand-written camelCase JSON exactly like a pre-variant cache.
                File.WriteAllText(path,
                    """[{"name":"Kali Linux 2026.2","publisher":"Offensive Security","diskUri":"https://example.com/kali.qcow2","version":"2026.2"}]""");

                Assert.IsTrue(cache.TryLoadCache(out var loaded));
                var kali = loaded!.Single();

                Assert.IsNull(kali.ReleaseVariants);
                Assert.AreSame(kali, kali.ResolveRelease(null),
                    "legacy items resolve a null release id to themselves");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

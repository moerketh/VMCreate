using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using VMCreate;

namespace VMCreate.Tests.Gallery
{
    /// <summary>
    /// Contract tests for release-variant resolution: a null id must mean
    /// "the item as published" (default variant), an explicit non-default id
    /// must produce a concrete downloadable clone, and unknown ids must yield
    /// null so callers fail loudly instead of deploying the wrong image.
    /// </summary>
    [TestClass]
    public sealed class ReleaseVariantContractTests
    {
        private static GalleryItem MultiReleaseItem() => new()
        {
            Name = "Kali Linux",
            Publisher = "Offensive Security",
            Description = "stable description",
            DiskUri = "https://example.com/kali-2026.2.qcow2",
            Version = "2026.2",
            LastUpdated = "2026-03-01",
            LinuxDistro = LinuxDistro.Kali,
            ReleaseVariants = new List<ReleaseVariant>
            {
                new()
                {
                    Id = "stable",
                    DisplayName = "Stable (2026.2)",
                    Description = "stable description",
                    DiskUri = "https://example.com/kali-2026.2.qcow2",
                    Version = "2026.2",
                    LastUpdated = "2026-03-01",
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
        public void NullId_ResolvesDefaultVariant_ToItemItself()
        {
            var item = MultiReleaseItem();

            var resolved = item.ResolveRelease(null);

            Assert.AreSame(item, resolved,
                "the default variant is what the item fields already describe — no clone needed");
        }

        [TestMethod]
        public void EmptyId_ResolvesDefaultVariant_ToItemItself()
        {
            var item = MultiReleaseItem();

            Assert.AreSame(item, item.ResolveRelease(""));
        }

        [TestMethod]
        public void DefaultVariantId_Resolves_ToItemItself()
        {
            var item = MultiReleaseItem();

            Assert.AreSame(item, item.ResolveRelease("stable"));
        }

        [TestMethod]
        public void NonDefaultId_Resolves_ToConcreteClone()
        {
            var item = MultiReleaseItem();

            var resolved = item.ResolveRelease("weekly")!;

            Assert.IsNotNull(resolved);
            Assert.AreNotSame(item, resolved);
            Assert.AreEqual("https://example.com/kali-weekly-W38.qcow2", resolved.DiskUri);
            Assert.AreEqual("2026-W38", resolved.Version);
            Assert.AreEqual("2026-09-18", resolved.LastUpdated);
            Assert.AreEqual("untested build with the latest updates", resolved.Description,
                "the variant channel description replaces the item description");
            Assert.AreEqual("Kali Linux", resolved.Name,
                "the VM-name default and gallery identity stay variant-invariant");
            Assert.AreEqual(item.Publisher, resolved.Publisher);
            Assert.AreEqual(item.LinuxDistro, resolved.LinuxDistro);
            Assert.IsNull(resolved.ReleaseVariants,
                "a resolved clone is a concrete downloadable image, not a picker");
        }

        [TestMethod]
        public void VariantId_MatchesCaseInsensitively()
        {
            Assert.IsNotNull(MultiReleaseItem().ResolveRelease("WEEKLY"));
        }

        [TestMethod]
        public void NonDefaultClone_KeepsFileTypeOverride()
        {
            // A variant can download from a filename whose extension does not
            // reveal the disk format (Rocky's .vhdfixed.xz latest-aliases). The
            // clone must carry the loader's declared override, or media-handler
            // selection falls back to extension inference and picks the wrong
            // handler for the non-default release only.
            var item = MultiReleaseItem();
            item.FileTypeOverride = "VHD";

            var resolved = item.ResolveRelease("weekly")!;

            Assert.AreEqual("VHD", resolved.FileTypeOverride);
        }

        [TestMethod]
        public void UnknownId_ReturnsNull()
        {
            Assert.IsNull(MultiReleaseItem().ResolveRelease("rolling"));
        }

        [TestMethod]
        public void VariantlessItem_NullId_ResolvesToItself()
        {
            var item = new GalleryItem { Name = "Ubuntu", DiskUri = "https://example.com/u.qcow2" };

            Assert.AreSame(item, item.ResolveRelease(null));
            Assert.AreSame(item, item.ResolveRelease(""));
        }

        [TestMethod]
        public void VariantlessItem_ExplicitId_ReturnsNull()
        {
            var item = new GalleryItem { Name = "Ubuntu", DiskUri = "https://example.com/u.qcow2" };

            Assert.IsNull(item.ResolveRelease("weekly"));
        }

        [TestMethod]
        public void NullItem_ResolvesToNull()
        {
            GalleryItem? item = null;

            Assert.IsNull(item.ResolveRelease(null));
            Assert.IsNull(item.ResolveRelease("weekly"));
        }

        [TestMethod]
        public void FindReleaseVariant_WithoutDefault_FallsBackToFirst()
        {
            var item = new GalleryItem
            {
                Name = "Mixed",
                ReleaseVariants = new List<ReleaseVariant>
                {
                    new() { Id = "a", DiskUri = "https://example.com/a" },
                    new() { Id = "b", DiskUri = "https://example.com/b" },
                },
            };

            var found = item.FindReleaseVariant(null);

            Assert.IsNotNull(found);
            Assert.AreEqual("a", found.Id);
        }

        [TestMethod]
        public void ResolveRelease_WithoutDefault_FallsBackToFirst()
        {
            var item = new GalleryItem
            {
                Name = "Mixed",
                DiskUri = "https://example.com/item",
                ReleaseVariants = new List<ReleaseVariant>
                {
                    // Neither is the default — the first acts as one.
                    new() { Id = "a", DiskUri = "https://example.com/a", Version = "1" },
                    new() { Id = "b", DiskUri = "https://example.com/b", Version = "2" },
                },
            };

            var resolved = item.ResolveRelease(null)!;

            Assert.AreSame(item, resolved,
                "no explicit default means the first variant must behave like the item itself");
        }
    }
}

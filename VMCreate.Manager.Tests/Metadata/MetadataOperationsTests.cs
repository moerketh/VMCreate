using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Metadata
{
    [TestClass]
    public class MetadataOperationsTests
    {
        [TestMethod]
        public void CreateFolder_DuplicateNameSameParent_Throws()
        {
            var doc = new MetadataDocument();
            doc.CreateFolder("Labs", null);
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.CreateFolder("labs", null));
        }

        [TestMethod]
        public void CreateFolder_SameNameDifferentParent_Allowed()
        {
            var doc = new MetadataDocument();
            var a = doc.CreateFolder("A", null);
            var b = doc.CreateFolder("B", null);
            doc.CreateFolder("Shared", a.Id);
            doc.CreateFolder("Shared", b.Id);
            Assert.AreEqual(4, doc.Folders.Count);
        }

        [TestMethod]
        public void CreateFolder_NameWithSeparator_Throws()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new MetadataDocument().CreateFolder("a/b", null));
        }

        [TestMethod]
        public void FolderColor_InheritsFromNearestColouredAncestor()
        {
            var doc = new MetadataDocument();
            var top = doc.CreateFolder("Engagements", null);
            var mid = doc.CreateFolder("ACME", top.Id);
            var leaf = doc.CreateFolder("Web", mid.Id);

            Assert.IsNull(doc.GetFolderColor(leaf.Id));
            doc.SetFolderColor(top.Id, "#e5534b");
            Assert.AreEqual("#E5534B", doc.GetFolderColor(leaf.Id));
            doc.SetFolderColor(mid.Id, "#5B9BE6");
            Assert.AreEqual("#5B9BE6", doc.GetFolderColor(leaf.Id));
            Assert.AreEqual("#E5534B", doc.GetFolderColor(top.Id));

            doc.SetFolderColor(mid.Id, null);
            Assert.AreEqual("#E5534B", doc.GetFolderColor(leaf.Id));
            Assert.IsNull(doc.GetFolderColor(null));
        }

        [TestMethod]
        public void EffectiveExpiry_OwnDateWins_ElseNearestFolder()
        {
            var doc = new MetadataDocument();
            var engagement = doc.CreateFolder("ACME", null);
            var web = doc.CreateFolder("Web", engagement.Id);
            var end = new DateOnly(2026, 12, 31);
            doc.SetFolderExpiry(engagement.Id, end);

            var inherits = new VmMetadata { FolderId = web.Id };
            var own = new VmMetadata { FolderId = web.Id, ExpiresOn = new DateOnly(2027, 1, 15) };
            var unfiled = new VmMetadata();

            Assert.AreEqual(end, doc.GetEffectiveExpiry(inherits));
            Assert.AreEqual(engagement.Id, doc.GetFolderExpiry(web.Id).From!.Id);
            Assert.AreEqual(new DateOnly(2027, 1, 15), doc.GetEffectiveExpiry(own));
            Assert.IsNull(doc.GetEffectiveExpiry(unfiled));
            Assert.IsNull(doc.GetEffectiveExpiry(null));

            doc.SetFolderExpiry(engagement.Id, null);
            Assert.IsNull(doc.GetEffectiveExpiry(inherits));
        }

        [TestMethod]
        public void ExpiredFilter_UsesInheritedExpiryWhenGiven()
        {
            var today = new DateOnly(2027, 1, 1);
            var filter = new SmartFolderFilter { ExpiredOnly = true };
            var meta = new VmMetadata();

            Assert.IsFalse(filter.Matches("vm", "Off", meta, today));
            Assert.IsTrue(filter.Matches("vm", "Off", meta, today, effectiveExpiry: new DateOnly(2026, 12, 31)));
            Assert.IsFalse(filter.Matches("vm", "Off", meta, today, effectiveExpiry: today), "the expiry day itself is not expired yet");
        }

        [TestMethod]
        public void RecordLinkedClone_InheritsFolderTagsLogin_ButNotTemplateState()
        {
            var doc = new MetadataDocument();
            var templateId = Guid.NewGuid();
            var template = doc.GetOrAddVm(templateId);
            template.FolderId = doc.EnsureFolderPath("Templates/Kali");
            template.Tags.Add("kali");
            template.IsTemplate = true;
            template.Protected = true;
            template.Purpose = "golden image";
            template.Credential = new StoredCredential { Username = "kali", ProtectedPassword = "blob" };
            template.Provenance = new VmProvenance { Tool = "VMCreate", GalleryItem = "Kali Linux", Version = "2026.3" };

            var cloneId = Guid.NewGuid();
            var clone = doc.RecordLinkedClone(cloneId, template, templateId, DateTimeOffset.Now);

            Assert.AreEqual(template.FolderId, clone.FolderId);
            CollectionAssert.AreEqual(new[] { "kali" }, clone.Tags);
            Assert.AreEqual(templateId, clone.TemplateId);
            Assert.IsFalse(clone.IsTemplate);
            Assert.IsFalse(clone.Protected);
            Assert.IsNull(clone.Purpose);
            Assert.AreEqual("kali", clone.Credential!.Username);
            Assert.AreNotSame(template.Credential, clone.Credential);
            Assert.AreEqual("Kali Linux", clone.Provenance!.GalleryItem);
            StringAssert.Contains(clone.Provenance.Tool, "linked clone");

            var payload = doc.ToNotesPayload(template)!;
            Assert.IsTrue(payload.Template);
            var restored = new MetadataDocument().AdoptFromNotes(templateId, payload);
            Assert.IsTrue(restored.IsTemplate, "the template flag survives a lost store via Notes");
        }

        [TestMethod]
        public void SetFolderColor_RejectsNonHex()
        {
            var doc = new MetadataDocument();
            var f = doc.CreateFolder("Labs", null);
            Assert.ThrowsExactly<ArgumentException>(() => doc.SetFolderColor(f.Id, "red"));
            Assert.ThrowsExactly<ArgumentException>(() => doc.SetFolderColor(f.Id, "#12345"));
            Assert.ThrowsExactly<ArgumentException>(() => doc.SetFolderColor(Guid.NewGuid(), "#123456"));
        }

        [TestMethod]
        public void MoveFolder_IntoOwnDescendant_Throws()
        {
            var doc = new MetadataDocument();
            var parent = doc.CreateFolder("P", null);
            var child = doc.CreateFolder("C", parent.Id);
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.MoveFolder(parent.Id, child.Id));
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.MoveFolder(parent.Id, parent.Id));
        }

        [TestMethod]
        public void DeleteFolder_MovesVmsAndSubfoldersToParent()
        {
            var doc = new MetadataDocument();
            var top = doc.CreateFolder("Top", null);
            var mid = doc.CreateFolder("Mid", top.Id);
            var leaf = doc.CreateFolder("Leaf", mid.Id);
            var vmId = Guid.NewGuid();
            doc.MoveVmToFolder(vmId, mid.Id);

            doc.DeleteFolder(mid.Id);

            Assert.IsNull(doc.FindFolder(mid.Id));
            Assert.AreEqual(top.Id, doc.FindFolder(leaf.Id)!.ParentId);
            Assert.AreEqual(top.Id, doc.FindVm(vmId)!.FolderId);
        }

        [TestMethod]
        public void DeleteFolder_ChildNameClash_IsRenamed()
        {
            var doc = new MetadataDocument();
            var a = doc.CreateFolder("A", null);
            doc.CreateFolder("X", null);
            var innerX = doc.CreateFolder("X", a.Id);

            doc.DeleteFolder(a.Id);

            Assert.AreEqual("X (2)", doc.FindFolder(innerX.Id)!.Name);
        }

        [TestMethod]
        public void FolderPath_RoundTripsThroughEnsure()
        {
            var doc = new MetadataDocument();
            var id = doc.EnsureFolderPath("Engagements/ACME/Internal");
            Assert.AreEqual("Engagements/ACME/Internal", doc.GetFolderPath(id));
            Assert.AreEqual(3, doc.Folders.Count);

            // Existing folders are reused, case-insensitively.
            Assert.AreEqual(id, doc.EnsureFolderPath("engagements/acme/internal"));
            Assert.AreEqual(3, doc.Folders.Count);
        }

        [TestMethod]
        public void EnsureFolderPath_Empty_IsRoot()
        {
            Assert.IsNull(new MetadataDocument().EnsureFolderPath("  "));
        }

        [TestMethod]
        public void ToNotesPayload_ExcludesCredentials_AndUsesFolderPath()
        {
            var doc = new MetadataDocument();
            var vm = doc.GetOrAddVm(Guid.NewGuid());
            vm.FolderId = doc.EnsureFolderPath("Labs/Web");
            vm.Credential = new StoredCredential { Username = "kali", ProtectedPassword = "blob" };

            var payload = doc.ToNotesPayload(vm);
            string notes = NotesBlock.Compose("", payload);

            Assert.AreEqual("Labs/Web", payload!.Folder);
            Assert.IsFalse(notes.Contains("blob"));
            Assert.IsFalse(notes.Contains("kali"));
        }

        [TestMethod]
        public void ToNotesPayload_EmptyMetadata_IsNull()
        {
            var doc = new MetadataDocument();
            Assert.IsNull(doc.ToNotesPayload(doc.GetOrAddVm(Guid.NewGuid())));
        }

        [TestMethod]
        public void AdoptFromNotes_CreatesFoldersAndCopiesFields()
        {
            var doc = new MetadataDocument();
            var id = Guid.NewGuid();
            doc.AdoptFromNotes(id, new NotesPayload { Folder = "A/B", Tags = new() { "t" }, Protected = true });

            var vm = doc.FindVm(id)!;
            Assert.AreEqual("A/B", doc.GetFolderPath(vm.FolderId));
            Assert.IsTrue(vm.Protected);
            CollectionAssert.AreEqual(new[] { "t" }, vm.Tags);
        }

        [TestMethod]
        public void SetTags_TrimsDedupesAndSorts()
        {
            var vm = new VmMetadata();
            vm.SetTags(new[] { " web ", "Lab", "lab", "", "api" });
            CollectionAssert.AreEqual(new[] { "api", "Lab", "web" }, vm.Tags);
        }

        [TestMethod]
        public void SmartFilter_CombinesConditions()
        {
            var today = new DateOnly(2026, 10, 31);
            var meta = new VmMetadata { Tags = new() { "malware" }, ExpiresOn = new DateOnly(2026, 10, 1) };
            var filter = new SmartFolderFilter { AnyTags = new() { "MALWARE" }, ExpiredOnly = true, States = new() { "Running" } };

            Assert.IsTrue(filter.Matches("sandbox", "Running", meta, today));
            Assert.IsFalse(filter.Matches("sandbox", "Off", meta, today));
            Assert.IsFalse(filter.Matches("sandbox", "Running", null, today));
        }

        [TestMethod]
        public void MatchesText_SearchesNamePurposeDescriptionTags()
        {
            var meta = new VmMetadata { Purpose = "ACME jump box", Description = "has **vpn**", Tags = new() { "external" } };
            Assert.IsTrue(MetadataOperations.MatchesText("kali", "Kali 2026", null));
            Assert.IsTrue(MetadataOperations.MatchesText("acme", "x", meta));
            Assert.IsTrue(MetadataOperations.MatchesText("vpn", "x", meta));
            Assert.IsTrue(MetadataOperations.MatchesText("extern", "x", meta));
            Assert.IsFalse(MetadataOperations.MatchesText("zzz", "x", meta));
        }

        [TestMethod]
        public void IsExpired_TodayIsNotExpired()
        {
            var today = new DateOnly(2026, 10, 31);
            Assert.IsFalse(new VmMetadata { ExpiresOn = today }.IsExpired(today));
            Assert.IsTrue(new VmMetadata { ExpiresOn = today.AddDays(-1) }.IsExpired(today));
            Assert.IsFalse(new VmMetadata().IsExpired(today));
        }
    }
}

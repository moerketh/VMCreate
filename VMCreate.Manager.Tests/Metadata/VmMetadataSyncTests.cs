using Moq;
using VMCreate.Manager.HyperV;
using VMCreate.Manager.Inventory;
using VMCreate.Manager.Metadata;
using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Metadata
{
    [TestClass]
    public class VmMetadataSyncTests
    {
        private string _dir = "";
        private JsonVmMetadataStore _store = null!;
        private Mock<IHyperVHost> _host = null!;
        private readonly Dictionary<Guid, string> _notes = new();

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VMCreate.Manager.Tests", Guid.NewGuid().ToString("N"));
            _store = new JsonVmMetadataStore(Path.Combine(_dir, "metadata.json"));
            _host = new Mock<IHyperVHost>();
            _host.Setup(h => h.GetNotesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => _notes.TryGetValue(id, out var n) ? n : "");
            _host.Setup(h => h.SetNotesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((Guid id, string n, CancellationToken _) => _notes[id] = n)
                .Returns(Task.CompletedTask);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        private VmMetadataSync CreateSync() => new(_store, _host.Object, new DpapiCredentialProtector());

        private static VmSummary Summary(Guid id, string notes) => new() { Id = id, Name = "vm", State = VmState.Off, Notes = notes };

        [TestMethod]
        public async Task UpdateVm_WritesStoreAndMirrorsIntoNotes_KeepingFreeText()
        {
            var id = Guid.NewGuid();
            _notes[id] = "my notes";

            await CreateSync().UpdateVmAsync(id, (doc, vm) =>
            {
                vm.FolderId = doc.EnsureFolderPath("Labs");
                vm.Protected = true;
            });

            var (free, payload) = NotesBlock.Parse(_notes[id]);
            Assert.AreEqual("my notes", free);
            Assert.AreEqual("Labs", payload!.Folder);
            Assert.AreEqual(true, payload.Protected);
            Assert.IsTrue((await _store.LoadAsync()).FindVm(id)!.Protected);
        }

        [TestMethod]
        public async Task PushToNotes_Unchanged_DoesNotWrite()
        {
            var id = Guid.NewGuid();
            var sync = CreateSync();
            await sync.UpdateVmAsync(id, (_, vm) => vm.Purpose = "x");
            _host.Invocations.Clear();

            await sync.PushToNotesAsync(id);

            _host.Verify(h => h.SetNotesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ── reconcile ────────────────────────────────────────────────────

        private Task<IReadOnlyList<VmSyncChange>> Reconcile(params Guid[] ids)
            => CreateSync().ReconcileAsync(ids.Select(id => Summary(id, _notes.TryGetValue(id, out var n) ? n : "")).ToList());

        /// <summary>An edit in Hyper-V Manager: the block's JSON replaced, free text kept.</summary>
        private void EditNotesOutside(Guid id, Action<NotesPayload> edit)
        {
            var (free, payload) = NotesBlock.Parse(_notes[id]);
            payload ??= new NotesPayload();
            edit(payload);
            _notes[id] = NotesBlock.Compose(free, payload);
        }

        [TestMethod]
        public async Task Reconcile_AdoptsUnknownVms_AndAnUnsyncedStoreEntryWins()
        {
            var known = Guid.NewGuid();
            var imported = Guid.NewGuid();
            await _store.UpdateAsync(d => { d.GetOrAddVm(known).Purpose = "local wins"; return 0; });
            string block = NotesBlock.Compose("", new NotesPayload { Purpose = "from notes", Folder = "Imported" });
            _notes[known] = block;
            _notes[imported] = block;

            var changes = await Reconcile(known, imported);

            var doc = await _store.LoadAsync();
            Assert.AreEqual(NotesSyncAction.Adopted, changes.Single(c => c.VmId == imported).Result.Action);
            Assert.AreEqual(NotesSyncAction.PushToNotes, changes.Single(c => c.VmId == known).Result.Action);
            Assert.AreEqual("local wins", doc.FindVm(known)!.Purpose);
            Assert.AreEqual("local wins", NotesBlock.Parse(_notes[known]).Payload!.Purpose);
            Assert.AreEqual("from notes", doc.FindVm(imported)!.Purpose);
            Assert.AreEqual("Imported", doc.GetFolderPath(doc.FindVm(imported)!.FolderId));
        }

        [TestMethod]
        public async Task Reconcile_TakesAnOutsideEdit()
        {
            var id = Guid.NewGuid();
            _notes[id] = "keep me";
            await CreateSync().UpdateVmAsync(id, (doc, vm) => { vm.Purpose = "old"; vm.FolderId = doc.EnsureFolderPath("Labs"); });
            EditNotesOutside(id, p => { p.Purpose = "edited in the MMC"; p.Tags = new() { "acme" }; });
            _host.Invocations.Clear();

            var change = (await Reconcile(id)).Single();

            Assert.AreEqual(NotesSyncAction.TookFromNotes, change.Result.Action);
            CollectionAssert.AreEquivalent(new[] { "tags", "purpose" }, change.Result.ChangedFields.ToList());
            var vm = (await _store.LoadAsync()).FindVm(id)!;
            Assert.AreEqual("edited in the MMC", vm.Purpose);
            CollectionAssert.AreEqual(new[] { "acme" }, vm.Tags);
            _host.Verify(h => h.SetNotesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.AreEqual(0, (await Reconcile(id)).Count, "in sync afterwards");
        }

        [TestMethod]
        public async Task Reconcile_PushesAStoreChangeThatNeverReachedNotes()
        {
            var id = Guid.NewGuid();
            await CreateSync().UpdateVmAsync(id, (_, vm) => vm.Purpose = "first");
            // A later store write whose Notes write failed (VM busy, tool crashed…).
            await _store.UpdateAsync(d => { d.FindVm(id)!.Purpose = "second"; return 0; });

            var change = (await Reconcile(id)).Single();

            Assert.AreEqual(NotesSyncAction.PushToNotes, change.Result.Action);
            Assert.AreEqual("second", NotesBlock.Parse(_notes[id]).Payload!.Purpose);
            Assert.AreEqual(0, (await Reconcile(id)).Count);
        }

        [TestMethod]
        public async Task Reconcile_OutsideEditCannotRemoveProtection()
        {
            var id = Guid.NewGuid();
            await CreateSync().UpdateVmAsync(id, (_, vm) => { vm.Protected = true; vm.Purpose = "a"; });
            EditNotesOutside(id, p => { p.Protected = null; p.Purpose = "b"; });

            var change = (await Reconcile(id)).Single();

            var vm = (await _store.LoadAsync()).FindVm(id)!;
            Assert.IsTrue(vm.Protected);
            Assert.AreEqual("b", vm.Purpose);
            Assert.IsTrue(change.Result.NeedsPush);
            Assert.AreEqual(true, NotesBlock.Parse(_notes[id]).Payload!.Protected, "Notes shows the protection again");
            Assert.AreEqual(0, (await Reconcile(id)).Count);
        }

        [TestMethod]
        public async Task Reconcile_RestoresARemovedBlock()
        {
            var id = Guid.NewGuid();
            await CreateSync().UpdateVmAsync(id, (doc, vm) => vm.FolderId = doc.EnsureFolderPath("Labs"));
            _notes[id] = "someone cleared it";

            var change = (await Reconcile(id)).Single();

            Assert.AreEqual(NotesSyncAction.RestoreBlock, change.Result.Action);
            var (free, payload) = NotesBlock.Parse(_notes[id]);
            Assert.AreEqual("someone cleared it", free);
            Assert.AreEqual("Labs", payload!.Folder);
        }

        [TestMethod]
        public async Task Reconcile_BothChanged_StoreWins_OldBlockReported()
        {
            var id = Guid.NewGuid();
            await CreateSync().UpdateVmAsync(id, (_, vm) => vm.Purpose = "synced");
            await _store.UpdateAsync(d => { d.FindVm(id)!.Purpose = "manager"; return 0; });
            EditNotesOutside(id, p => p.Purpose = "outside");

            var change = (await Reconcile(id)).Single();

            Assert.AreEqual(NotesSyncAction.Conflict, change.Result.Action);
            StringAssert.Contains(change.Result.OverwrittenBlock, "outside");
            Assert.AreEqual("manager", NotesBlock.Parse(_notes[id]).Payload!.Purpose);
        }

        [TestMethod]
        public async Task Reconcile_CredentialsOnlyEntry_DoesNotWipeABlock()
        {
            var id = Guid.NewGuid();
            await _store.UpdateAsync(d => { d.GetOrAddVm(id).Credential = new StoredCredential { Username = "kali" }; return 0; });
            _notes[id] = NotesBlock.Compose("", new NotesPayload { Folder = "Hack The Box" });

            var change = (await Reconcile(id)).Single();

            Assert.AreEqual(NotesSyncAction.TookFromNotes, change.Result.Action);
            var doc = await _store.LoadAsync();
            Assert.AreEqual("Hack The Box", doc.GetFolderPath(doc.FindVm(id)!.FolderId));
            Assert.AreEqual("kali", doc.FindVm(id)!.Credential!.Username);
            Assert.AreEqual("Hack The Box", NotesBlock.Parse(_notes[id]).Payload!.Folder);
        }

        [TestMethod]
        public async Task Reconcile_InSync_DoesNotRewriteTheStore()
        {
            var id = Guid.NewGuid();
            await CreateSync().UpdateVmAsync(id, (_, vm) => vm.Purpose = "x");
            await Reconcile(id); // records name and day seen
            var written = File.GetLastWriteTimeUtc(_store.FilePath);
            await Task.Delay(50);

            Assert.AreEqual(0, (await Reconcile(id)).Count);

            Assert.AreEqual(written, File.GetLastWriteTimeUtc(_store.FilePath));
        }

        [TestMethod]
        public async Task Reconcile_RecordsNames_StaleEntriesAreThoseNotOnTheHost()
        {
            var here = Guid.NewGuid();
            var gone = Guid.NewGuid();
            await _store.UpdateAsync(d =>
            {
                d.GetOrAddVm(here).Purpose = "a";
                var old = d.GetOrAddVm(gone);
                old.LastKnownName = "deleted-in-mmc";
                return 0;
            });

            await Reconcile(here);

            var doc = await _store.LoadAsync();
            Assert.AreEqual("vm", doc.FindVm(here)!.LastKnownName);
            Assert.AreEqual(DateOnly.FromDateTime(DateTime.Today), doc.FindVm(here)!.LastSeenOn);
            Assert.AreEqual(gone, doc.FindStaleEntries(new[] { here }).Single().VmId);
        }

        [TestMethod]
        public async Task MigrateLegacyCredentials_EncryptsAndStripsFromNotes()
        {
            var id = Guid.NewGuid();
            _notes[id] = "Initial Username: user\r\nInitial Password: parrot";
            var sync = CreateSync();

            Assert.IsTrue(SyncHasLegacy(id));
            Assert.IsTrue(await sync.MigrateLegacyCredentialsAsync(id));

            Assert.IsFalse(_notes[id].Contains("parrot"));
            var cred = (await _store.LoadAsync()).FindVm(id)!.Credential!;
            Assert.AreEqual("user", cred.Username);
            Assert.AreNotEqual("parrot", cred.ProtectedPassword);
            Assert.AreEqual("parrot", sync.RevealPassword(cred));
            Assert.IsFalse(await sync.MigrateLegacyCredentialsAsync(id), "Second run finds nothing");
        }

        private bool SyncHasLegacy(Guid id) => VmMetadataSync.HasLegacyCredentials(Summary(id, _notes[id]));
    }
}

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

        [TestMethod]
        public async Task AdoptFromNotes_OnlyForUnknownVms()
        {
            var known = Guid.NewGuid();
            var imported = Guid.NewGuid();
            await _store.UpdateAsync(d => { d.GetOrAddVm(known).Purpose = "local wins"; return 0; });

            string block = NotesBlock.Compose("", new NotesPayload { Purpose = "from notes", Folder = "Imported" });
            int adopted = await CreateSync().AdoptFromNotesAsync(new[] { Summary(known, block), Summary(imported, block) });

            var doc = await _store.LoadAsync();
            Assert.AreEqual(1, adopted);
            Assert.AreEqual("local wins", doc.FindVm(known)!.Purpose);
            Assert.AreEqual("from notes", doc.FindVm(imported)!.Purpose);
            Assert.AreEqual("Imported", doc.GetFolderPath(doc.FindVm(imported)!.FolderId));
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

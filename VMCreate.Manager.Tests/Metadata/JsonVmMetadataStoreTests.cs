using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Metadata
{
    [TestClass]
    public class JsonVmMetadataStoreTests
    {
        private string _dir = "";
        private string _path = "";

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VMCreate.Manager.Tests", Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "metadata.json");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        [TestMethod]
        public async Task Load_MissingFile_ReturnsEmptyDocument()
        {
            var doc = await new JsonVmMetadataStore(_path).LoadAsync();
            Assert.AreEqual(0, doc.Vms.Count);
            Assert.IsFalse(File.Exists(_path), "Loading must not create the file");
        }

        [TestMethod]
        public async Task Update_PersistsAcrossStoreInstances()
        {
            var id = Guid.NewGuid();
            await new JsonVmMetadataStore(_path).UpdateAsync(doc =>
            {
                var vm = doc.GetOrAddVm(id);
                vm.Purpose = "C2 server";
                vm.Protected = true;
                vm.ExpiresOn = new DateOnly(2027, 1, 15);
                vm.SetTags(new[] { "lab", "c2" });
                return 0;
            });

            var reloaded = await new JsonVmMetadataStore(_path).LoadAsync();
            var vm = reloaded.FindVm(id);
            Assert.IsNotNull(vm);
            Assert.AreEqual("C2 server", vm.Purpose);
            Assert.IsTrue(vm.Protected);
            Assert.AreEqual(new DateOnly(2027, 1, 15), vm.ExpiresOn);
            CollectionAssert.AreEqual(new[] { "c2", "lab" }, vm.Tags);
        }

        [TestMethod]
        public async Task Update_RereadsFile_SoConcurrentWritersDoNotLoseChanges()
        {
            // Two store instances stand in for VMCreate and the Manager.
            var a = new JsonVmMetadataStore(_path);
            var b = new JsonVmMetadataStore(_path);
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();

            await Task.WhenAll(
                Enumerable.Range(0, 20).Select(i => i % 2 == 0
                    ? a.UpdateAsync(d => { d.GetOrAddVm(idA).Tags.Add($"a{i}"); return 0; })
                    : b.UpdateAsync(d => { d.GetOrAddVm(idB).Tags.Add($"b{i}"); return 0; })));

            var doc = await a.LoadAsync();
            Assert.AreEqual(10, doc.FindVm(idA)!.Tags.Count);
            Assert.AreEqual(10, doc.FindVm(idB)!.Tags.Count);
        }

        [TestMethod]
        public async Task Load_CorruptFile_ThrowsAndDoesNotOverwrite()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{ this is not json");
            var store = new JsonVmMetadataStore(_path);

            await Assert.ThrowsExactlyAsync<MetadataStoreException>(() => store.LoadAsync());
            await Assert.ThrowsExactlyAsync<MetadataStoreException>(() => store.UpdateAsync(d => 0));
            Assert.AreEqual("{ this is not json", File.ReadAllText(_path));
        }

        [TestMethod]
        public async Task Load_NewerSchema_Throws()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{\"schemaVersion\": 99}");
            await Assert.ThrowsExactlyAsync<MetadataStoreException>(() => new JsonVmMetadataStore(_path).LoadAsync());
        }

        [TestMethod]
        public async Task Update_KeepsBackupOfPreviousVersion()
        {
            var store = new JsonVmMetadataStore(_path);
            await store.UpdateAsync(d => { d.GetOrAddVm(Guid.NewGuid()).Purpose = "first"; return 0; });
            await store.UpdateAsync(d => { d.Vms[0].Purpose = "second"; return 0; });

            StringAssert.Contains(File.ReadAllText(_path + ".bak"), "first");
            StringAssert.Contains(File.ReadAllText(_path), "second");
            Assert.IsFalse(File.Exists(_path + ".tmp"));
        }

        [TestMethod]
        public async Task Update_MutatorThrows_FileUnchanged()
        {
            var store = new JsonVmMetadataStore(_path);
            await store.UpdateAsync(d => { d.GetOrAddVm(Guid.NewGuid()).Purpose = "safe"; return 0; });
            string before = File.ReadAllText(_path);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                store.UpdateAsync<int>(d => { d.Vms.Clear(); throw new InvalidOperationException(); }));

            Assert.AreEqual(before, File.ReadAllText(_path));
        }

        [TestMethod]
        public void DpapiProtector_RoundTrips()
        {
            var protector = new DpapiCredentialProtector();
            string blob = protector.Protect("s3cret!");
            Assert.AreNotEqual("s3cret!", blob);
            Assert.AreEqual("s3cret!", protector.Unprotect(blob));
        }
    }
}

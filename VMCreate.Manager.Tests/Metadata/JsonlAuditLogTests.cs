using VMCreate.Metadata;

namespace VMCreate.Manager.Tests.Metadata
{
    [TestClass]
    public class JsonlAuditLogTests
    {
        private string _dir = "";
        private string _path = "";

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VMCreate.Manager.Tests", Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "audit.jsonl");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        [TestMethod]
        public async Task Read_MissingFile_IsEmptyAndCreatesNothing()
        {
            Assert.AreEqual(0, (await new JsonlAuditLog(_path).ReadAsync()).Count);
            Assert.IsFalse(File.Exists(_path));
        }

        [TestMethod]
        public async Task Append_ThenRead_NewestFirstWithFilterAndLimit()
        {
            var log = new JsonlAuditLog(_path);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            await log.AppendAsync(new AuditEntry { Tool = "Manager", Action = AuditActions.VmStart, VmId = a, VmName = "kali" });
            await log.AppendAsync(new AuditEntry { Tool = "Manager", Action = AuditActions.VmStart, VmId = b, VmName = "win" });
            await log.AppendAsync(new AuditEntry { Tool = "Manager", Action = AuditActions.VmTurnOff, VmId = a, VmName = "kali", Error = "Invalid state" });

            var all = await new JsonlAuditLog(_path).ReadAsync();
            CollectionAssert.AreEqual(new[] { AuditActions.VmTurnOff, AuditActions.VmStart, AuditActions.VmStart }, all.Select(e => e.Action).ToArray());
            Assert.IsFalse(all[0].Succeeded);
            Assert.IsFalse(string.IsNullOrEmpty(all[0].User), "User is filled in automatically");

            var onlyA = await log.ReadAsync(vmId: a);
            Assert.AreEqual(2, onlyA.Count);
            Assert.IsTrue(onlyA.All(e => e.VmId == a));

            Assert.AreEqual(1, (await log.ReadAsync(max: 1)).Count);
        }

        [TestMethod]
        public async Task Append_KeepsExistingContent_AndSkipsTornLines()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{\"action\":\"vm.create\",\"tool\":\"VMCreate\"}\n{\"action\":\"vm.st");   // torn last line
            var log = new JsonlAuditLog(_path);
            await log.AppendAsync(new AuditEntry { Tool = "Manager", Action = AuditActions.VmDelete });

            string text = File.ReadAllText(_path);
            StringAssert.StartsWith(text, "{\"action\":\"vm.create\"", "Existing lines are never rewritten");
            var entries = await log.ReadAsync();
            // The torn line is skipped; the entries before and after it both survive.
            CollectionAssert.AreEqual(new[] { AuditActions.VmDelete, AuditActions.VmCreate }, entries.Select(e => e.Action).ToArray());
        }

        [TestMethod]
        public async Task ConcurrentAppends_FromSeparateInstances_AllLand()
        {
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
                new JsonlAuditLog(_path).AppendAsync(new AuditEntry { Tool = "Manager", Action = AuditActions.VmStart, Detail = i.ToString() })));

            var entries = await new JsonlAuditLog(_path).ReadAsync();
            Assert.AreEqual(40, entries.Count);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 40).Select(i => i.ToString()).ToArray(), entries.Select(e => e.Detail).ToArray());
        }
    }
}

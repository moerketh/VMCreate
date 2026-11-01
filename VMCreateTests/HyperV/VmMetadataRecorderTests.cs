using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.HyperV;
using VMCreate.Metadata;

namespace VMCreate.Tests.HyperV
{
    [TestClass]
    public sealed class VmMetadataRecorderTests
    {
        private static readonly Guid VmId = Guid.Parse("39EFCF77-B4D6-4D1B-B7FE-CB8537EB987E");

        private string _dir = "";
        private JsonVmMetadataStore _store = null!;
        private Mock<IPowerShellExecutor> _executor = null!;
        private string _vmNotes = "";
        private string? _writtenNotes;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "VMCreate.Tests", "metadata-" + Guid.NewGuid().ToString("N"));
            _store = new JsonVmMetadataStore(Path.Combine(_dir, "metadata.json"));
            _executor = new Mock<IPowerShellExecutor>();
            _executor.Setup(e => e.RunCommandAsync("Get-VM", It.IsAny<IEnumerable<KeyValuePair<string, object?>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var vm = new PSObject();
                    vm.Properties.Add(new PSNoteProperty("Id", VmId));
                    vm.Properties.Add(new PSNoteProperty("Notes", _vmNotes));
                    return new PowerShellResult { Output = { vm } };
                });
            _executor.Setup(e => e.RunCommandAsync("Set-VM", It.IsAny<IEnumerable<KeyValuePair<string, object?>>>(), It.IsAny<CancellationToken>()))
                .Callback((string _, IEnumerable<KeyValuePair<string, object?>>? p, CancellationToken _) =>
                    _writtenNotes = p!.Single(kv => kv.Key == "Notes").Value as string)
                .ReturnsAsync(new PowerShellResult());
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }

        private VmMetadataRecorder CreateRecorder()
            => new(_executor.Object, _store, new DpapiCredentialProtector(), NullLogger<VmMetadataRecorder>.Instance);

        private static VmDeploymentPlan Plan(string? folder = null, params string[] tags)
            => VmDeploymentPlan.FromSettings(new VmSettings { VMName = "Kali_20261101", Folder = folder, Tags = tags });

        [TestMethod]
        public async Task RecordCredentials_EncryptsPasswordAndNeverTouchesNotes()
        {
            await CreateRecorder().RecordCredentialsAsync("Kali_20261101", "kali", "kali-pw", CancellationToken.None);

            var cred = (await _store.LoadAsync()).FindVm(VmId)!.Credential!;
            Assert.AreEqual("kali", cred.Username);
            Assert.AreNotEqual("kali-pw", cred.ProtectedPassword);
            Assert.AreEqual("kali-pw", new DpapiCredentialProtector().Unprotect(cred.ProtectedPassword!));
            _executor.Verify(e => e.RunCommandAsync("Set-VM", It.IsAny<IEnumerable<KeyValuePair<string, object?>>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task RecordCredentials_NoCredentials_IsNoOp()
        {
            await CreateRecorder().RecordCredentialsAsync("Kali_20261101", null, "", CancellationToken.None);
            Assert.IsFalse(File.Exists(_store.FilePath));
        }

        [TestMethod]
        public async Task RecordDeployment_WritesProvenanceFolderTags_AndNotesBlockWithoutSecrets()
        {
            _vmNotes = "user text";
            var recorder = CreateRecorder();
            await recorder.RecordCredentialsAsync("Kali_20261101", "kali", "kali-pw", CancellationToken.None);

            await recorder.RecordDeploymentAsync(Plan("Engagements/ACME", "kali", "external"),
                new GalleryItem { Name = "Kali Linux", Version = "2026.3", Checksum = "abc123", DiskUri = "x" }, CancellationToken.None);

            var doc = await _store.LoadAsync();
            var vm = doc.FindVm(VmId)!;
            Assert.AreEqual("Engagements/ACME", doc.GetFolderPath(vm.FolderId));
            CollectionAssert.AreEqual(new[] { "external", "kali" }, vm.Tags);
            Assert.AreEqual("Kali Linux", vm.Provenance!.GalleryItem);
            Assert.AreEqual("2026.3", vm.Provenance.Version);
            Assert.AreEqual("abc123", vm.Provenance.ImageChecksum);
            Assert.AreEqual("VMCreate", vm.Provenance.Tool);
            Assert.IsNotNull(vm.Credential, "credentials recorded earlier survive");

            var (free, payload) = NotesBlock.Parse(_writtenNotes);
            Assert.AreEqual("user text", free);
            Assert.AreEqual("Engagements/ACME", payload!.Folder);
            Assert.IsFalse(_writtenNotes!.Contains("kali-pw"));
        }

        [TestMethod]
        public void ApplyDeployment_NoFolderOrTags_KeepsExistingOnes()
        {
            var doc = new MetadataDocument();
            var vm = doc.GetOrAddVm(VmId);
            vm.FolderId = doc.EnsureFolderPath("Labs");
            vm.Tags.Add("keep");

            VmMetadataRecorder.ApplyDeployment(doc, vm, Plan(), new GalleryItem { Name = "Kali Linux", DiskUri = "x" }, DateTimeOffset.Now);

            Assert.AreEqual("Labs", doc.GetFolderPath(vm.FolderId));
            CollectionAssert.AreEqual(new[] { "keep" }, vm.Tags);
        }

        [TestMethod]
        public void FromSettings_CarriesFolderAndTags()
        {
            var plan = VmDeploymentPlan.FromSettings(new VmSettings { VMName = "x", Folder = "  A/B  ", Tags = new[] { "t" } });
            Assert.AreEqual("A/B", plan.Folder);
            CollectionAssert.AreEqual(new[] { "t" }, plan.Tags.ToArray());
            Assert.IsNull(VmDeploymentPlan.FromSettings(new VmSettings { VMName = "x", Folder = " " }).Folder);
        }
    }
}

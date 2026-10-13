using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;
using VMCreate.MediaHandlers;

namespace VMCreate.Tests.MediaHandlers
{
    /// <summary>
    /// Covers <see cref="DiskConverter.ConvertToVhdxAsync"/>'s source-format
    /// gate: the Rocky Hyper-V/Azure cloud image (<c>.vhdfixed</c>) must reach
    /// qemu's <c>vpc</c> driver, not be rejected with
    /// "Unsupported disk format: vpc" (the failure that broke the E2E run
    /// of 2026-10-12).
    /// </summary>
    [TestClass]
    public class DiskConverterTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "vmcreate-diskconverter-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public async Task ConvertToVhdxAsync_VhdFooter_IsNotRejectedByFormatGate()
        {
            string dir = NewTempDir();
            try
            {
                // A file whose last 512 bytes start with the VHD "conectix"
                // footer magic: ResolveSourceFormat must sniff "vpc", and the
                // format gate must let it through to qemu-img.
                string src = Path.Combine(dir, "Rocky-10-Azure-Base.x86_64.vhdfixed");
                using (var fs = new FileStream(src, FileMode.CreateNew, FileAccess.Write))
                {
                    fs.SetLength(1024 * 1024);
                    fs.Seek(-512, SeekOrigin.End);
                    fs.Write(new byte[] { (byte)'c', (byte)'o', (byte)'n', (byte)'e', (byte)'c', (byte)'t',
                                          (byte)'i', (byte)'x' });
                    fs.Write(new byte[504]);
                }

                var converter = new DiskConverter(NullLogger<DiskConverter>.Instance, "qemu-img-definitely-absent.exe");

                try
                {
                    await converter.ConvertToVhdxAsync(src, Path.Combine(dir, "out.vhdx"), null!, CancellationToken.None);
                    Assert.Fail("Expected a conversion failure (qemu-img binary absent).");
                }
                catch (NotSupportedException ex) when (ex.Message.Contains("Unsupported disk format"))
                {
                    Assert.Fail($"Format gate rejected vpc: {ex.Message}");
                }
                catch (Exception ex)
                {
                    StringAssert.DoesNotMatch(ex.Message, new Regex("Unsupported disk format"));
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ConvertToVhdxAsync_VhdfixedExtension_WithoutMagicBytes_IsNotRejectedByFormatGate()
        {
            string dir = NewTempDir();
            try
            {
                // No "conectix" footer, no qcow2/vmdk magic: sniffing falls
                // back to the ".vhdfixed" extension, which must also resolve
                // to vpc rather than trip the format gate.
                string src = Path.Combine(dir, "disk.vhdfixed");
                using (var fs = new FileStream(src, FileMode.CreateNew, FileAccess.Write))
                {
                    fs.SetLength(1024 * 1024);
                }

                var converter = new DiskConverter(NullLogger<DiskConverter>.Instance, "qemu-img-definitely-absent.exe");

                try
                {
                    await converter.ConvertToVhdxAsync(src, Path.Combine(dir, "out.vhdx"), null!, CancellationToken.None);
                    Assert.Fail("Expected a conversion failure (qemu-img binary absent).");
                }
                catch (NotSupportedException ex) when (ex.Message.Contains("Unsupported disk format"))
                {
                    Assert.Fail($"Format gate rejected vpc: {ex.Message}");
                }
                catch (Exception ex)
                {
                    StringAssert.DoesNotMatch(ex.Message, new Regex("Unsupported disk format"));
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public async Task ConvertToVhdxAsync_UnknownContentAndExtension_DoesThrowUnsupportedFormat()
        {
            string dir = NewTempDir();
            try
            {
                // Guard the other direction: content with no known magic and
                // an unrecognized extension must still be rejected.
                string src = Path.Combine(dir, "corrupt.img");
                using (var fs = new FileStream(src, FileMode.CreateNew, FileAccess.Write))
                {
                    fs.SetLength(4096);
                }

                var converter = new DiskConverter(NullLogger<DiskConverter>.Instance, "qemu-img-definitely-absent.exe");

                try
                {
                    await converter.ConvertToVhdxAsync(src, Path.Combine(dir, "out.vhdx"), null!, CancellationToken.None);
                    Assert.Fail("Expected NotSupportedException for unknown content/extension.");
                }
                catch (NotSupportedException ex)
                {
                    StringAssert.Contains(ex.Message, "disk format");
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

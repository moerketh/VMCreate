using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate
{
    public class DiskConverter : IDiskConverter
    {
        private readonly string _qemuImgPath;
        private readonly ILogger<DiskConverter> _logger;
        private const int BufferSize = 4 * 1024 * 1024; // 4MB buffer for file copy

        public DiskConverter(ILogger<DiskConverter> logger, string qemuImgPath = "C:\\Program Files\\qemu\\qemu-img.exe")
        {
            _qemuImgPath = qemuImgPath ?? throw new ArgumentNullException(nameof(qemuImgPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> ConvertToVhdxAsync(string sourcePath, string destinationPath, IProgress<CreateVMProgressInfo> progress, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Starting disk conversion from {SourcePath} to {DestinationPath}", sourcePath, destinationPath);

            if (!File.Exists(sourcePath))
            {
                _logger.LogError("Source file not found: {SourcePath}", sourcePath);
                throw new FileNotFoundException($"Source disk file not found: {sourcePath}");
            }

            // Format is resolved by content first (Ubuntu cloud images ship
            // qcow2 data in ".img" files), falling back to the extension.
            string sourceFormat = ResolveSourceFormat(sourcePath);
            if (sourceFormat is not ("vmdk" or "qcow2"))
            {
                _logger.LogError("Unsupported source format: {SourceFormat}. Supported formats: vmdk, qcow2", sourceFormat);
                throw new NotSupportedException($"Unsupported disk format: {sourceFormat}");
            }

            if (Path.GetExtension(destinationPath).ToLower() != ".vhdx")
            {
                _logger.LogError("Destination must be a .vhdx file, got: {DestinationPath}", destinationPath);
                throw new ArgumentException("Destination file must have .vhdx extension");
            }

            if (File.Exists(destinationPath))
            {
                _logger.LogInformation("Destination file already exists, deleting: {DestinationPath}", destinationPath);
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        File.Delete(destinationPath);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        _logger.LogWarning("File is locked, retrying delete in {Delay}s (attempt {Attempt}/5): {Path}",
                            attempt, attempt, destinationPath);
                        await Task.Delay(attempt * 1000);
                    }
                }
            }

            // Pre-flight: check available disk space on the destination drive.
            // The converted VHDX can be up to ~1.5× the source size; use that as a safety margin.
            var sourceSize = new FileInfo(sourcePath).Length;
            long requiredBytes = (long)(sourceSize * 1.5);
            string? destRoot = Path.GetPathRoot(destinationPath);
            if (!string.IsNullOrEmpty(destRoot))
            {
                var driveInfo = new DriveInfo(destRoot);
                if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < requiredBytes)
                {
                    long availableMB = driveInfo.AvailableFreeSpace / (1024 * 1024);
                    long requiredMB  = requiredBytes / (1024 * 1024);
                    string msg = $"Not enough disk space on {destRoot.TrimEnd('\\')} to convert the disk image. " +
                                 $"Available: {availableMB:N0} MB, estimated required: {requiredMB:N0} MB. " +
                                 $"Free up space or change the Hyper-V virtual hard disk path.";
                    _logger.LogError(msg);
                    throw new IOException(msg);
                }
                _logger.LogDebug("Disk space check OK on {Drive}: {Available} MB available, ~{Required} MB needed",
                    destRoot, driveInfo.AvailableFreeSpace / (1024 * 1024), requiredBytes / (1024 * 1024));
            }

            // Log qemu-img version for diagnostics
            try
            {
                var versionInfo = new ProcessStartInfo
                {
                    FileName = _qemuImgPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var vProc = Process.Start(versionInfo)!;
                var versionLine = await Task.Run(() => vProc.StandardOutput.ReadLine());
                vProc.WaitForExit();
                _logger.LogInformation("Using {QemuVersion}", versionLine);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not determine qemu-img version");
            }

            //string tmpDestinationPath = destinationPath + ".tmp";
            try
            {
                // Step 1: Run qemu-img conversion
                if (progress != null)
                {
                    var progressInfo = new CreateVMProgressInfo()
                    {
                        ProgressPercentage = 0,
                        Phase = VmDeploymentPhase.Convert,
                        URI = $"Starting conversion of {sourcePath}",
                        DownloadSpeed = -1,

                    };
                    progress.Report(progressInfo);
                }
                var processInfo = new ProcessStartInfo
                {
                    FileName = _qemuImgPath,
                    // -p for progress output on stdout
                    Arguments = $"convert -p -f {sourceFormat} -O vhdx \"{sourcePath}\" \"{destinationPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true })
                {
                    // Kill the qemu-img process if cancellation is requested.
                    using var ctReg = cancellationToken.Register(() =>
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                _logger.LogWarning("Cancellation requested — killing qemu-img process");
                                process.Kill();
                            }
                        }
                        catch (InvalidOperationException) { /* already exited */ }
                    });

                    // If cancellation was already requested, the Register callback above
                    // fired on a not-yet-started process and was swallowed. Bail out now
                    // before starting qemu-img.
                    cancellationToken.ThrowIfCancellationRequested();

                    var progressRegex = new Regex(@"\s+\((\d+)\.\d+/100%\)", RegexOptions.Compiled);
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            var match = progressRegex.Match(e.Data);
                            if (match.Success && double.TryParse(match.Groups[1].Value, out double percentage))
                            {
                                if (progress != null)
                                {
                                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = Convert.ToInt32(percentage), Phase = VmDeploymentPhase.Convert, URI = $"Converting from {sourcePath} to {destinationPath}", DownloadSpeed = -1 });
                                }
                                _logger.LogDebug("qemu-img progress: {Percentage}%", Convert.ToInt32(percentage));
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    string error = await Task.Run(() => process.StandardError.ReadToEnd());
                    await Task.Run(() => process.WaitForExit());

                    cancellationToken.ThrowIfCancellationRequested();

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("Conversion failed. Error: {Error}", error);

                        // Detect disk-full / write-error from qemu-img output
                        if (error != null && (error.Contains("error while writing") || error.Contains("No space left on device")))
                        {
                            string drive = Path.GetPathRoot(destinationPath)?.TrimEnd('\\') ?? "destination drive";
                            throw new IOException(
                                $"Disk conversion failed: ran out of disk space on {drive}. " +
                                $"Free up space and try again.\n\nOriginal error: {error}");
                        }

                        throw new Exception($"Disk conversion failed: {error}");
                    }

                    if (progress != null)
                    {
                        progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 100, Phase = VmDeploymentPhase.Convert, URI = $"Completed conversion of {sourcePath}", DownloadSpeed = -1 });
                    }
                    _logger.LogInformation("Successfully converted {SourcePath} to {DestinationPath}", sourcePath, destinationPath);
                }

                if (!File.Exists(destinationPath))
                {
                    _logger.LogError("Converted file not found: {DestinationPath}", destinationPath);
                    throw new FileNotFoundException($"Converted VHDX file not found: {destinationPath}");
                }

                // Step 2: De-sparsify the VHDX so Hyper-V can read every sector correctly.
                // qemu-img may produce a sparse file on NTFS; if the sparse regions are
                // not physically allocated, Hyper-V reads zeros and compressed (e.g. zstd)
                // filesystem data becomes corrupted.
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation("De-sparsifying VHDX: {Path}", destinationPath);
                if (progress != null)
                {
                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 0, Phase = VmDeploymentPhase.Convert, URI = $"{destinationPath}" });
                }

                await Task.Run(() =>
                {
                    if (!SparseFileUtility.MakeNonSparse(destinationPath))
                    {
                        throw new IOException(
                            $"Failed to de-sparsify the VHDX file '{destinationPath}'. " +
                            $"This can cause data corruption. Check disk space and permissions on {Path.GetPathRoot(destinationPath)?.TrimEnd('\\') ?? "the destination drive"}.");
                    }
                });

                cancellationToken.ThrowIfCancellationRequested();

                if (progress != null)
                {
                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 100, Phase = VmDeploymentPhase.Convert, URI = $"{destinationPath}" });
                }
                //File.Delete(tmpDestinationPath);
                _logger.LogInformation("Successfully created non-sparse file: {DestinationPath}", destinationPath);

                return destinationPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during conversion: {Message}", ex.Message);

                // Clean up partial VHDX so we don't leave orphaned files
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                        _logger.LogInformation("Deleted partial VHDX: {Path}", destinationPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to delete partial VHDX: {Path}", destinationPath);
                }

                throw;
            }
        }

        public async Task<long> GetVirtualSizeAsync(string diskPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(diskPath))
                throw new FileNotFoundException($"Disk file not found: {diskPath}");

            var processInfo = new ProcessStartInfo
            {
                FileName = _qemuImgPath,
                Arguments = $"info --output=json \"{diskPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo)!;
            string output = await Task.Run(() => process.StandardOutput.ReadToEnd());
            string error = await Task.Run(() => process.StandardError.ReadToEnd());
            await Task.Run(() => process.WaitForExit());

            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                _logger.LogError("qemu-img info failed for {Path}: {Error}", diskPath, error);
                throw new InvalidOperationException($"Failed to read disk info for {diskPath}: {error}");
            }

            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("virtual-size", out JsonElement sizeElement))
            {
                long virtualSize = sizeElement.GetInt64();
                _logger.LogInformation("Virtual size of {Path}: {SizeBytes} bytes ({SizeGB} GB)",
                    diskPath, virtualSize, (double)virtualSize / (1024 * 1024 * 1024));
                return virtualSize;
            }

            throw new InvalidOperationException($"Could not determine virtual size of {diskPath}");
        }

        private string GetQemuSourceFormat(string extension)
        {
            switch (extension.ToLower())
            {
                case ".vmdk":
                    return "vmdk";
                case ".qcow2":
                    return "qcow2";
                default:
                    _logger.LogError("Unknown disk format: {Extension}", extension);
                    throw new NotSupportedException($"Unknown disk format: {extension}");
            }
        }

        /// <summary>
        /// Resolves the qemu source format for a local file by sniffing magic
        /// bytes (qcow2: "QFI\xFB" at offset 0; VMDK: "KDMV" at offset 512),
        /// falling back to the extension for raw/unknown content. Needed
        /// because cloud images (Ubuntu) ship qcow2 data in ".img" files.
        /// </summary>
        private string ResolveSourceFormat(string sourcePath)
        {
            try
            {
                using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

                Span<byte> header = stackalloc byte[1024];
                int read = 0;
                while (read < header.Length)
                {
                    int n = stream.Read(header[read..]);
                    if (n == 0) break;
                    read += n;
                }

                if (read >= 4 && header[0] == (byte)'Q' && header[1] == (byte)'F' && header[2] == (byte)'I' && header[3] == 0xFB)
                    return "qcow2";
                if (read >= 516 && header[512] == (byte)'K' && header[513] == (byte)'D' && header[514] == (byte)'M' && header[515] == (byte)'V')
                    return "vmdk";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Magic-byte format sniffing failed for {Path}; falling back to extension", sourcePath);
            }

            return GetQemuSourceFormat(Path.GetExtension(sourcePath));
        }
    }
}
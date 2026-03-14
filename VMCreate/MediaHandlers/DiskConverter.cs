using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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

        public async Task<string> ConvertToVhdxAsync(string sourcePath, string destinationPath, IProgress<CreateVMProgressInfo> progress)
        {
            _logger.LogInformation("Starting disk conversion from {SourcePath} to {DestinationPath}", sourcePath, destinationPath);

            if (!File.Exists(sourcePath))
            {
                _logger.LogError("Source file not found: {SourcePath}", sourcePath);
                throw new FileNotFoundException($"Source disk file not found: {sourcePath}");
            }

            string sourceExtension = Path.GetExtension(sourcePath).ToLower();
            if (sourceExtension != ".vmdk" && sourceExtension != ".qcow2")
            {
                _logger.LogError("Unsupported source format: {SourceExtension}. Supported formats: .vmdk, .qcow2", sourceExtension);
                throw new NotSupportedException($"Unsupported disk format: {sourceExtension}");
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
            string destRoot = Path.GetPathRoot(destinationPath);
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
                using var vProc = Process.Start(versionInfo);
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
                        Phase = "Converting to VHDX...",
                        URI = $"Starting conversion of {sourcePath}",
                        DownloadSpeed = -1,
                        
                    };
                    progress.Report(progressInfo);
                }
                var processInfo = new ProcessStartInfo
                {
                    FileName = _qemuImgPath,
                    // -p for progress output on stdout
                    Arguments = $"convert -p -f {GetQemuSourceFormat(sourceExtension)} -O vhdx \"{sourcePath}\" \"{destinationPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = processInfo, EnableRaisingEvents = true })
                {
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
                                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = Convert.ToInt32(percentage), Phase = "Converting to VHDX...", URI = $"Converting from {sourcePath} to {destinationPath}", DownloadSpeed = -1 });
                                }
                                _logger.LogDebug("qemu-img progress: {Percentage}%", Convert.ToInt32(percentage));
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    string error = await Task.Run(() => process.StandardError.ReadToEnd());
                    await Task.Run(() => process.WaitForExit());

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
                        progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 100, Phase = "Converting to VHDX...", URI = $"Completed conversion of {sourcePath}", DownloadSpeed = -1 });
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
                _logger.LogInformation("De-sparsifying VHDX: {Path}", destinationPath);
                if (progress != null)
                {
                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 0, Phase = "Making file non-sparse...", URI = $"{destinationPath}" });
                }

                if (!SparseFileUtility.MakeNonSparse(destinationPath))
                {
                    throw new IOException(
                        $"Failed to de-sparsify the VHDX file '{destinationPath}'. " +
                        $"This can cause data corruption. Check disk space and permissions on {Path.GetPathRoot(destinationPath)?.TrimEnd('\\') ?? "the destination drive"}.");
                }

                if (progress != null)
                {
                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 100, Phase = "Making file non-sparse...", URI = $"{destinationPath}" });
                }
                //File.Delete(tmpDestinationPath);
                _logger.LogInformation("Successfully created non-sparse file: {DestinationPath}", destinationPath);

                return destinationPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during conversion: {Message}", ex.Message);
                throw;
            }
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
    }
}
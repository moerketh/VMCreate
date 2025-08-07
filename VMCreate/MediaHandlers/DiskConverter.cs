using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VMCreate;

namespace VMCreate
{
    public class DiskConverter
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
                File.Delete(destinationPath);
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
                    // -S 0 does not prevent a sparse file in latest QEMU, but keeping it for now
                    // -p for progress
                    Arguments = $"convert -S 0 -p -f {GetQemuSourceFormat(sourceExtension)} -O vhdx \"{sourcePath}\" \"{destinationPath}\"",
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
                                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = Convert.ToInt32(percentage), Phase = "Converting to VHDX...", URI = $"Converting from {sourcePath} to {destinationPath}" });
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
                        throw new Exception($"Disk conversion failed: {error}");
                    }

                    if (progress != null)
                    {
                        progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 100, Phase = "Converting to VHDX...", URI = $"Completed conversion of {sourcePath}" });
                    }
                    _logger.LogInformation("Successfully converted {SourcePath} to {DestinationPath}", sourcePath, destinationPath);
                }

                if (!File.Exists(destinationPath))
                {
                    _logger.LogError("Converted file not found: {DestinationPath}", destinationPath);
                    throw new FileNotFoundException($"Converted VHDX file not found: {destinationPath}");
                }

                // Step 2: Create non-sparse copy
                //_logger.LogInformation("Creating non-sparse copy of file {tmpDestinationPath} to {destinationPath}", destinationPath, destinationPath);
                if (progress != null)
                {
                    progress.Report(new CreateVMProgressInfo() { ProgressPercentage = 0, Phase = "Making file non-sparse...", URI = $"{destinationPath}" });
                }

                //using (var sourceStream = new FileStream(tmpDestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                //using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write))
                //{
                //    long totalBytes = sourceStream.Length;
                //    long bytesCopied = 0;
                //    byte[] buffer = new byte[BufferSize];
                //    int bytesRead;

                //    while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                //    {
                //        await destStream.WriteAsync(buffer, 0, bytesRead);
                //        bytesCopied += bytesRead;
                //        double copyPercentage = ((double)bytesCopied / (double)totalBytes);
                //        int percentage = Convert.ToInt32(Math.Round(copyPercentage * 100)); // Scale to 0-100 and round
                //        if (progress != null)
                //        {
                //            progress.Report(new CreateVMProgressInfo() { ProgressPercentage = percentage, Phase = "Copy to non-sparse file...", URI = $"Copying to {destinationPath}" });
                //        }
                //    }
                //}
                SparseFileUtility.MakeNonSparse(destinationPath);

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
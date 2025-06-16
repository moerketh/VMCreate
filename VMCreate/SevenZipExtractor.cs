using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SharpCompress.Archives;
using SharpCompress.Common;
using VMCreate;

namespace VMCreateVM
{
    public interface IExtractor
    {
        void Unpack7ZipAsync(string zipFilePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo);
    }

    public class SevenZipExtractor : IExtractor
    {
        private readonly string logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");

        private void WriteLog(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }

        public void Unpack7ZipAsync(string zipFilePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo)
        {
            try
            {
                WriteLog("Starting 7zip unpacking.");
                var progressInfo = new CreateVMProgressInfo
                {
                    Phase = "Unpacking...",
                    URI = zipFilePath,
                    DownloadSpeed = -1,
                    ProgressPercentage = 0
                };
                progressReportInfo.Report(progressInfo);
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                Directory.CreateDirectory(extractPath);
                using (var archive = ArchiveFactory.Open(zipFilePath))
                {
                    var totalSize = archive.TotalUncompressSize;
                    //report progress
                    archive.CompressedBytesRead += (sender, e) =>
                    {
                        var progress = ((double)e.CompressedBytesRead / (double)totalSize) * 100;
                        progressReportInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Unpacking...",
                            URI = zipFilePath,
                            DownloadSpeed = -1,
                            ProgressPercentage = Convert.ToInt32(progress)
                        });
                    };
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        //extract entry
                        entry.WriteToDirectory(extractPath, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });                        
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error unpacking 7zip: {ex.Message}");
                throw;
            }
        }
    }
}
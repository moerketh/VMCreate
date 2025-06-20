using System;
using System.IO;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using VMCreate;

namespace VMCreateVM
{
    public interface IExtractor
    {
        void Extract(string zipFilePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo);
    }

    public class SevenZipExtractor : IExtractor
    {
        public void Extract(string zipFilePath, string extractPath, CancellationToken cancellationToken, IProgress<CreateVMProgressInfo> progressReportInfo)
        {
            try
            {
                string filePart = "";
                double bytesRead = 0;
                double totalBytesRead = 0;                
                
                //WriteLog("Starting 7zip unpacking.");
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                Directory.CreateDirectory(extractPath);
                using (var archive = ArchiveFactory.Open(zipFilePath))
                {
                    archive.FilePartExtractionBegin += (sender, e) =>
                    {
                        filePart = e.Name;
                        totalBytesRead += bytesRead;
                    };

                    //progress handler
                    archive.CompressedBytesRead += (sender, e) =>
                    {                        
                        var progress = (((double)e.CompressedBytesRead + totalBytesRead) / (double)archive.TotalUncompressSize) * 100;
                        progressReportInfo.Report(new CreateVMProgressInfo
                        {
                            Phase = "Extracting...",
                            URI = Path.Combine(extractPath, filePart),
                            DownloadSpeed = -1,
                            ProgressPercentage = Convert.ToInt32(progress)
                        });
                        bytesRead = e.CompressedBytesRead;
                    };
                    archive.WriteToDirectory(extractPath, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                }
            }
            catch (Exception ex)
            {
                //WriteLog($"Error unpacking 7zip: {ex.Message}");
                throw;
            }
        }
    }
}
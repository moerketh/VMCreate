using System;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VMCreate;

namespace VMCreateVM.MediaHandlers
{
    public interface IMediaHandler
    {
        bool RequiresExtraction { get; }
        int VmGeneration { get; }
        Task PrepareMediaAsync(string sourceFile, string destinationPath, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken);
        Task AttachMediaAsync(PowerShell ps, string vmName, string mediaPath, GalleryItem item, ILogger logger);
    }
}
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Management.Automation;
using VMCreate;
using System;

namespace VMCreateVM.MediaHandlers
{
    public class IsoMediaHandler : MediaHandler
    {
        public IsoMediaHandler(ILogger<IsoMediaHandler> logger) : base(logger)
        {
        }

        public override bool RequiresExtraction => false;

        public override int VmGeneration => 2; // ISOs use UEFI (Gen2)

        public override async Task PrepareMediaAsync(string sourceFile, string destinationPath, GalleryItem item, IProgress<CreateVMProgressInfo> progressInfo, CancellationToken cancellationToken)
        {
            await base.PrepareMediaAsync(sourceFile, destinationPath, item, progressInfo, cancellationToken);
        }

        public override async Task AttachMediaAsync(PowerShell ps, string vmName, string mediaPath, GalleryItem item, ILogger logger)
        {
            logger.LogInformation("Checking for DVD drive on VM: {VMName}", vmName);
            ps.Commands.Clear();
            ps.AddCommand("Get-VMDvdDrive")
                .AddParameter("VMName", vmName);
            var results = await Task.Run(() => ps.Invoke());

            if (ps.HadErrors)
            {
                throw new Exception($"Failed to check DVD drive: {ps.Streams.Error[0]}");
            }

            if (results.Count == 0)
            {
                logger.LogInformation("No DVD drive found, adding one to VM: {VMName}", vmName);
                ps.Commands.Clear();
                ps.AddCommand("Add-VMDvdDrive")
                    .AddParameter("VMName", vmName)
                    .AddParameter("ControllerNumber", 0)
                    .AddParameter("ControllerLocation", 0);
                await Task.Run(() => ps.Invoke());

                if (ps.HadErrors)
                {
                    throw new Exception($"Failed to add DVD drive: {ps.Streams.Error[0]}");
                }
                logger.LogInformation("Added DVD drive to VM: {VMName}", vmName);
            }
            else
            {
                logger.LogInformation("DVD drive already exists on VM: {VMName}", vmName);
            }

            logger.LogInformation("Attaching ISO as DVD drive: {MediaPath}", mediaPath);
            ps.Commands.Clear();
            ps.AddCommand("Set-VMDvdDrive")
                .AddParameter("VMName", vmName)
                .AddParameter("Path", mediaPath);
            await Task.Run(() => ps.Invoke());
            logger.LogInformation("Attached ISO to DVD drive: {MediaPath}", mediaPath);

            if (ps.HadErrors)
            {
                throw new Exception($"Failed to attach ISO: {ps.Streams.Error[0]}");
            }
        }
    }
}
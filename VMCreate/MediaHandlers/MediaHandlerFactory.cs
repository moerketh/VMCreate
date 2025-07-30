using System;
using Microsoft.Extensions.Logging;

namespace VMCreate.MediaHandlers
{
    public class MediaHandlerFactory
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly DiskConverter _diskConverter;
        private readonly IPartitionSchemeDetector _partitionSchemeDetector;

        public MediaHandlerFactory(ILoggerFactory loggerFactory, DiskConverter diskConverter, IPartitionSchemeDetector partitionSchemeDetector)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
            _partitionSchemeDetector = partitionSchemeDetector ?? throw new ArgumentNullException(nameof(partitionSchemeDetector));
        }

        public IMediaHandler CreateHandler(string fileType)
        {
            switch (fileType.ToUpper())
            {
                case "VMDK":
                    return new VmdkMediaHandler(_loggerFactory.CreateLogger<VmdkMediaHandler>(), _diskConverter, _partitionSchemeDetector);
                case "QCOW2":
                    return new Qcow2MediaHandler(_loggerFactory.CreateLogger<Qcow2MediaHandler>(), _diskConverter, _partitionSchemeDetector);
                case "VHDX":
                    return new VhdxMediaHandler(_loggerFactory.CreateLogger<VhdxMediaHandler>(), _partitionSchemeDetector);                
                default:
                    throw new NotSupportedException($"Unsupported file type: {fileType}");
            }
        }
    }
}
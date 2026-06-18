using System;
using Microsoft.Extensions.Logging;

namespace VMCreate.MediaHandlers
{
    public class MediaHandlerFactory : IMediaHandlerFactory
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IDiskConverter _diskConverter;
        private readonly IVmGenerationResolver _generationResolver;

        public MediaHandlerFactory(ILoggerFactory loggerFactory, IDiskConverter diskConverter, IVmGenerationResolver generationResolver)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _diskConverter = diskConverter ?? throw new ArgumentNullException(nameof(diskConverter));
            _generationResolver = generationResolver ?? throw new ArgumentNullException(nameof(generationResolver));
        }

        public IMediaHandler CreateHandler(DiskImageFormat format)
        {
            return format switch
            {
                DiskImageFormat.Vmdk => new VmdkMediaHandler(_loggerFactory.CreateLogger<VmdkMediaHandler>(), _diskConverter, _generationResolver),
                DiskImageFormat.Qcow2 => new Qcow2MediaHandler(_loggerFactory.CreateLogger<Qcow2MediaHandler>(), _diskConverter, _generationResolver),
                DiskImageFormat.Vhdx => new VhdxMediaHandler(_loggerFactory.CreateLogger<VhdxMediaHandler>(), _generationResolver, _diskConverter),
                DiskImageFormat.Iso => new IsoMediaHandler(_loggerFactory.CreateLogger<IsoMediaHandler>()),
                _ => throw new NotSupportedException($"Unsupported file type: {format}")
            };
        }
    }
}

using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace VMCreate
{
    public interface IFileStreamProvider
    {
        Task<(Stream WriteStream, bool IsCached)> GetWriteStreamAsync(string filePath, bool useCache);
    }
    public class FileStreamProvider : IFileStreamProvider
    {
        private readonly ILogger<FileStreamProvider> _logger;
        private const int BufferSize = 65536;

        public FileStreamProvider(ILogger<FileStreamProvider> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<(Stream WriteStream, bool IsCached)> GetWriteStreamAsync(string filePath, bool useCache)
        {
            if (useCache && File.Exists(filePath))
            {
                _logger.LogInformation("Using cached file: {FilePath}", filePath);
                return Task.FromResult<(Stream, bool)>((null, true));
            }

            var writeStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
            return Task.FromResult<(Stream, bool)>((writeStream, false));
        }
    }
}

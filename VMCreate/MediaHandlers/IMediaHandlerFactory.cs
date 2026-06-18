namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Creates the appropriate media handler for a given file type.
    /// </summary>
    public interface IMediaHandlerFactory
    {
        /// <summary>
        /// Returns an <see cref="IMediaHandler"/> for the specified file type.
        /// </summary>
        IMediaHandler CreateHandler(DiskImageFormat format);
    }
}

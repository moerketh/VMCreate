namespace VMCreate.MediaHandlers
{
    /// <summary>
    /// Creates the appropriate media handler for a given file type.
    /// </summary>
    public interface IMediaHandlerFactory
    {
        /// <summary>
        /// Returns an <see cref="IMediaHandler"/> for the specified file type (VMDK, QCOW2, VHDX, etc.).
        /// </summary>
        IMediaHandler CreateHandler(string fileType);
    }
}

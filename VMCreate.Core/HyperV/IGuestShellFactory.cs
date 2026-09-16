namespace VMCreate
{
    /// <summary>
    /// Factory for creating <see cref="IGuestShell"/> instances with runtime parameters.
    /// </summary>
    public interface IGuestShellFactory
    {
        /// <summary>
        /// Creates a new guest shell connected to the specified VM using the given SSH key.
        /// Used for Linux VMs where SSH is the primary remote management transport.
        /// </summary>
        IGuestShell Create(string vmName, string privateKeyPath);

        /// <summary>
        /// Creates a new guest shell connected to the specified Windows VM using
        /// PowerShell Direct (Invoke-Command over VMBus). Does not require network
        /// connectivity or SSH — only needs VM name and Windows credentials.
        /// </summary>
        IGuestShell CreateForWindows(string vmName, string username, string password);
    }
}

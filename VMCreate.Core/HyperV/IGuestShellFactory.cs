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
        /// Connects as the pre-baked <c>vmcreate</c> automation user.
        /// </summary>
        IGuestShell Create(string vmName, string privateKeyPath);

        /// <summary>
        /// Creates a new guest shell that connects as a specific user. Used for
        /// distros whose installer creates the login user itself (e.g. Omarchy
        /// creates the seeded <c>omarchy</c> user instead of <c>vmcreate</c>).
        /// </summary>
        IGuestShell Create(string vmName, string privateKeyPath, string username);

        /// <summary>
        /// Creates a shell connected over the Hyper-V socket channel (vsock):
        /// no NIC, no IP discovery. Host string is the VM GUID, reached via
        /// hvc.exe as an ssh ProxyCommand. Used by the ISO boot-cycle.
        /// </summary>
        IGuestShell CreateVsock(string vmName, string privateKeyPath, string vmGuid);

        /// <summary>
        /// Creates a new guest shell connected to the specified Windows VM using
        /// PowerShell Direct (Invoke-Command over VMBus). Does not require network
        /// connectivity or SSH — only needs VM name and Windows credentials.
        /// </summary>
        IGuestShell CreateForWindows(string vmName, string username, string password);
    }
}

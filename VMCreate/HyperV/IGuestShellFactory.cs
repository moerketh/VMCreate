namespace VMCreate
{
    /// <summary>
    /// Factory for creating <see cref="IGuestShell"/> instances with runtime parameters.
    /// </summary>
    public interface IGuestShellFactory
    {
        /// <summary>
        /// Creates a new guest shell connected to the specified VM using the given SSH key.
        /// </summary>
        IGuestShell Create(string vmName, string privateKeyPath);
    }
}

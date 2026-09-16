using System;

namespace VMCreate.HyperV
{
    /// <summary>
    /// Thrown when the current user lacks the Hyper-V Administrators group membership
    /// required to manage virtual machines via WMI.
    /// </summary>
    public class HyperVPermissionException : Exception
    {
        public HyperVPermissionException(string message) : base(message) { }
    }
}
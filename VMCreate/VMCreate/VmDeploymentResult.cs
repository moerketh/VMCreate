using System;

namespace VMCreate
{
    /// <summary>
    /// Result of a completed (or failed) VM creation attempt. Returned by
    /// <see cref="IVmCreator.CreateAsync"/> so callers no longer need to infer
    /// the final VM name from progress reports.
    /// </summary>
    public sealed class VmDeploymentResult
    {
        public VmDeploymentResult(
            string vmName,
            bool success,
            string vmPath = null,
            string vhdxPath = null,
            string errorMessage = null,
            string deploymentLog = null)
        {
            VmName = vmName ?? throw new ArgumentNullException(nameof(vmName));
            Success = success;
            VmPath = vmPath;
            VhdxPath = vhdxPath;
            ErrorMessage = errorMessage;
            DeploymentLog = deploymentLog;
        }

        /// <summary>
        /// Effective VM name used during deployment.
        /// </summary>
        public string VmName { get; }

        /// <summary>
        /// True if the VM was created and started successfully.
        /// </summary>
        public bool Success { get; }

        /// <summary>
        /// Directory containing the VM configuration files, or null if unavailable.
        /// </summary>
        public string VmPath { get; }

        /// <summary>
        /// Path to the primary VHDX attached to the VM, or null if unavailable.
        /// </summary>
        public string VhdxPath { get; }

        /// <summary>
        /// Human-readable error message when <see cref="Success"/> is false.
        /// </summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// Structured deployment log captured during the deployment, or null if unavailable.
        /// </summary>
        public string DeploymentLog { get; }
    }
}

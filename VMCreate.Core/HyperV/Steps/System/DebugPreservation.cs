using System;

namespace VMCreate
{
    /// <summary>
    /// Debug preservation switch for deployments under investigation.
    /// When enabled, a finished deployment keeps its <c>vmcreate</c>
    /// automation user and SSH access (instead of removing both), and a
    /// failed deployment is kept instead of cleaned up — so the guest
    /// stays reachable for post-mortem debugging.
    /// <para>
    /// Debug builds are ALWAYS enabled (no debugger required — CLI runs
    /// from a Debug build keep the VM reachable). Release builds enable
    /// it when a debugger is attached or
    /// <c>VMCREATE_DEBUG_PRESERVE=1</c> is set.
    /// </para>
    /// </summary>
    public static class DebugPreservation
    {
        public const string EnvironmentVariable = "VMCREATE_DEBUG_PRESERVE";

        /// <summary>Tests pin specific enabled/disabled states with this;
        /// null (default) follows the build configuration.</summary>
        internal static bool? TestOverride;

        public static bool IsEnabled => TestOverride ?? BuildDefault();

        private static bool BuildDefault()
        {
#if DEBUG
            return true;
#else
            return ReleaseEnabled();
#endif
        }

        /// <summary>The Release-build rule; testable independently of the
        /// build configuration the tests themselves compile under.</summary>
        internal static bool ReleaseEnabled() =>
            System.Diagnostics.Debugger.IsAttached
            || Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "true";
    }
}

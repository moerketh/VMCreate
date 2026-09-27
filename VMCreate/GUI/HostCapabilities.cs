using System;
using System.Runtime.InteropServices;

namespace VMCreate
{
    /// <summary>
    /// Detects host hardware capabilities (RAM, logical processors) once per process
    /// so the wizard can cap resource sliders at what the machine actually has.
    /// Used only for slider bounds and hints — Hyper-V performs the authoritative
    /// checks at VM creation time.
    /// </summary>
    public static class HostCapabilities
    {
        private static readonly Lazy<ulong> _totalMemoryMB = new Lazy<ulong>(DetectTotalMemoryMB);

        /// <summary>Total physical memory of the host in megabytes (0 if detection failed).</summary>
        public static ulong TotalMemoryMB => _totalMemoryMB.Value;

        /// <summary>Number of logical processors on the host (at least 1).</summary>
        public static int LogicalProcessors => Math.Max(1, Environment.ProcessorCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

        private static ulong DetectTotalMemoryMB()
        {
            try
            {
                if (GetPhysicallyInstalledSystemMemory(out ulong kb))
                    return kb / 1024;
            }
            catch (Exception)
            {
                // Detection is best-effort; callers fall back to hardcoded limits.
            }
            return 0;
        }
    }
}

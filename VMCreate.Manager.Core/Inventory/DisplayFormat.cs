using System.Globalization;

namespace VMCreate.Manager.Inventory
{
    /// <summary>Display strings shared by the GUI and (later) the CLI.</summary>
    public static class DisplayFormat
    {
        public static string Memory(ulong megabytes)
            => megabytes >= 1024
                ? (megabytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
                : megabytes.ToString(CultureInfo.InvariantCulture) + " MB";

        public static string Uptime(TimeSpan uptime)
        {
            if (uptime <= TimeSpan.Zero)
                return "";
            return uptime.TotalDays >= 1
                ? $"{(int)uptime.TotalDays}d {uptime.Hours:00}:{uptime.Minutes:00}"
                : $"{uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        }

        public static string State(VmState state) => state switch
        {
            VmState.Off => "Off",
            VmState.Running => "Running",
            VmState.Saved => "Saved",
            VmState.Paused => "Paused",
            VmState.Starting => "Starting…",
            VmState.Stopping => "Stopping…",
            VmState.Saving => "Saving…",
            VmState.Pausing => "Pausing…",
            VmState.Resuming => "Resuming…",
            _ => "Unknown",
        };
    }
}

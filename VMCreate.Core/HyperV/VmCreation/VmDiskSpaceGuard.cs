using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace VMCreate.HyperV.VmCreation
{
    /// <summary>
    /// Pre-flight free-space check for the drive that hosts the VM's hard
    /// disks. Deployments that run out of mid-write die at the WORST place:
    /// Hyper-V pauses the VM with "Disk Full" (event 12635/18524), the vsock
    /// channel goes silent, and the host surfaces an SSH keepalive timeout
    /// — the deployment log reads like a network failure, not a full disk.
    /// This guard fails the deployment BEFORE the ISO cycle with the real
    /// numbers (free vs required, drive letter) so the user can free space
    /// or move the VM path.
    /// </summary>
    public static class VmDiskSpaceGuard
    {
        /// <summary>
        /// Headroom beyond the computed requirement: the ISO stage's 8 GB
        /// swap file (the dd fallback writes it non-sparse, so budget the
        /// full size), config drift, and filesystem metadata. Covers the
        /// swap with margin rather than exactly — this is a "will it
        /// survive" floor, not a comfort margin.
        /// </summary>
        public const long HeadroomBytes = 10L * 1024 * 1024 * 1024;

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> when the drive
        /// hosting <paramref name="path"/> has less than
        /// <paramref name="requiredBytes"/> free. The exception message
        /// carries the drive, free and required numbers in GB — it is the
        /// deployment error the user reads.
        /// </summary>
        public static void EnsureAvailable(
            string path,
            long requiredBytes,
            Func<string, (long FreeBytes, string DriveName)>? probe = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path must be provided.", nameof(path));

            probe ??= DefaultProbe;
            (long freeBytes, string driveName) = probe(path);

            if (freeBytes >= requiredBytes)
                return;

            throw new InvalidOperationException(
                $"Not enough free space on {driveName} for this deployment: " +
                $"{ToGB(freeBytes)} GB free, {ToGB(requiredBytes)} GB required " +
                $"(disk image + swap + headroom). Free up space on {driveName} or move the VM disk path, " +
                "then retry the deployment. A deployment that runs out of space mid-write is paused by " +
                "Hyper-V and reported as an SSH timeout — this check prevents that.");
        }

        /// <summary>
        /// The requirement for a disk-image deployment: the ISO cycle's
        /// clone writes a fresh boot disk that grows to (at most) the source
        /// image's data; the customize flow attaches the prepared image
        /// as-is and writes no second copy. Both add the stage's swap file
        /// and a small headroom on top.
        /// </summary>
        public static long RequiredBytes(string mediaPath, bool cloneWritesSecondDisk)
        {
            long sourceBytes = File.Exists(mediaPath) ? new FileInfo(mediaPath).Length : 0;
            return cloneWritesSecondDisk ? sourceBytes + HeadroomBytes : HeadroomBytes;
        }

        private static (long FreeBytes, string DriveName) DefaultProbe(string path)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return (long.MaxValue, root); // unreadable drive: don't block on a probe failure
            return (drive.AvailableFreeSpace, drive.Name);
        }

        private static string ToGB(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:0.#}";
    }
}

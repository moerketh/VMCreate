using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VMCreate
{
    public static class SparseFileUtility
    {
        // Constants from Windows API
        private const uint FSCTL_SET_SPARSE = 0x000900C4;
        private const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x000940CF;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;

        // Struct for input buffer (BOOLEAN SetSparse as byte: 0 = FALSE)
        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_SET_SPARSE_BUFFER
        {
            public byte SetSparse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_ALLOCATED_RANGE_BUFFER
        {
            public long FileOffset;
            public long Length;
        }

        // P/Invoke declarations
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref FILE_SET_SPARSE_BUFFER lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref FILE_ALLOCATED_RANGE_BUFFER lpInBuffer,
            uint nInBufferSize,
            [Out] FILE_ALLOCATED_RANGE_BUFFER[] lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        /// <summary>
        /// Makes the specified file non-sparse by unsetting the sparse attribute.
        /// When NTFS removes the sparse flag it physically allocates zero-filled blocks
        /// for every deallocated region, so the full file size is consumed on disk.
        /// </summary>
        /// <returns>True if the file is (now) non-sparse, false on failure.</returns>
        public static bool MakeNonSparse(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new ArgumentException("File not found.", nameof(filePath));
            }

            // Quick check: if the file is already non-sparse, nothing to do.
            if ((File.GetAttributes(filePath) & FileAttributes.SparseFile) == 0)
                return true;

            using (SafeFileHandle handle = CreateFile(filePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new IOException($"Failed to open file for de-sparsification: Win32 error {Marshal.GetLastWin32Error()}");
                }

                FILE_SET_SPARSE_BUFFER buffer = new FILE_SET_SPARSE_BUFFER { SetSparse = 0 };
                uint bytesReturned;

                bool success = DeviceIoControl(
                    handle.DangerousGetHandle(),
                    FSCTL_SET_SPARSE,
                    ref buffer,
                    (uint)Marshal.SizeOf<FILE_SET_SPARSE_BUFFER>(),
                    IntPtr.Zero,
                    0,
                    out bytesReturned,
                    IntPtr.Zero);

                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException(
                        $"FSCTL_SET_SPARSE failed with Win32 error {error}. " +
                        $"The VHDX may remain sparse and cause data corruption.");
                }

                return true;
            }
        }
    }
}
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
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint OPEN_EXISTING = 3;

        // Struct for input buffer (BOOLEAN SetSparse as byte: 0 = FALSE)
        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_SET_SPARSE_BUFFER
        {
            public byte SetSparse;
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

        /// <summary>
        /// Makes the specified file non-sparse by unsetting the sparse attribute and allocating space for sparse regions.
        /// This operation modifies the file in place and requires appropriate permissions (may need to run as administrator).
        /// </summary>
        /// <param name="filePath">The full path to the file to de-sparsify.</param>
        /// <returns>True if the operation succeeded, false otherwise.</returns>
        /// <exception cref="ArgumentException">Thrown if the file does not exist.</exception>
        /// <exception cref="IOException">Thrown if unable to open the file or other I/O errors occur.</exception>
        public static bool MakeNonSparse(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new ArgumentException("File not found.", nameof(filePath));
            }

            // Open file handle with read/write access
            using (SafeFileHandle handle = CreateFile(filePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new IOException($"Failed to open file: {Marshal.GetLastWin32Error()}");
                }

                // Prepare input buffer to unset sparse (SetSparse = 0)
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
                    // You can log or handle the error code here if needed, but for now, just return false
                    return false;
                }

                return true;
            }
        }
    }
}
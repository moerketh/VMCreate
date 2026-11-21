using System.Runtime.InteropServices;

namespace VMCreate.Manager.Deletion
{
    public interface IFileRemover
    {
        /// <summary>
        /// Removes a file: to the Recycle Bin, or for good when
        /// <paramref name="permanently"/>. Throws <see cref="IOException"/> when the
        /// file is still there afterwards, <see cref="OperationCanceledException"/>
        /// when the user declined.
        /// </summary>
        void Remove(string path, bool permanently);
    }

    /// <summary>
    /// Recycles through the shell, without confirmation or progress UI. A file too
    /// big for the Recycle Bin would be destroyed silently, so the shell is asked
    /// to warn first (FOF_WANTNUKEWARNING); declining leaves the file in place.
    /// </summary>
    public sealed class ShellFileRemover : IFileRemover
    {
        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        public void Remove(string path, bool permanently)
        {
            if (!File.Exists(path))
                return;
            if (permanently)
            {
                File.Delete(path);
                return;
            }

            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0", // the list is double-null terminated
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
            };
            int rc = SHFileOperation(ref op);
            if (op.fAnyOperationsAborted)
                throw new OperationCanceledException($"Kept '{path}': not moved to the Recycle Bin.");
            if (rc != 0 || File.Exists(path))
                throw new IOException($"Could not move '{path}' to the Recycle Bin (shell error 0x{rc:X}).");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
    }
}

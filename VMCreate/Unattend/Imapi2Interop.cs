#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace VMCreate
{
    /// <summary>
    /// Minimal COM interop for IMAPI2FS (<c>imapi2fs.h</c>), the in-box Windows
    /// component that builds ISO 9660 / Joliet / UDF disc images.
    ///
    /// This replaces the previous approach of driving IMAPI2FS from an inline
    /// PowerShell script (New-Object -ComObject plus an Add-Type helper that
    /// pumped an IStream into a file). The direct interop keeps the same
    /// observable behavior while removing the runtime PowerShell-engine and
    /// Add-Type (Roslyn) compilation dependency from the ISO creation path.
    ///
    /// All interface declarations below are translated verbatim from
    /// %WindowsSdk%\um\imapi2fs.h, including exact vtable order. Dual
    /// (IDispatch-derived) interfaces get the 7 IDispatch slots implicitly;
    /// every member between the first and the last one invoked must be
    /// declared in native order, or calls would land on wrong vtable slots.
    /// Members that exist only to preserve slot positions are marked
    /// "vtable filler" and are never invoked from this codebase.
    ///
    /// IMAPI2 objects are apartment-threaded; the public entry point runs the
    /// whole build on a dedicated STA thread so object creation and every call
    /// happen in the same apartment, avoiding cross-apartment proxying.
    /// </summary>
    internal static class Imapi2Interop
    {
        private const string FileSystemImageProgId = "IMAPI2FS.MsftFileSystemImage";

        /// <summary>
        /// Creates an ISO image containing the directory tree at
        /// <paramref name="sourceDirectory"/> and writes it to
        /// <paramref name="isoPath"/>. The image combines ISO9660 + Joliet +
        /// UDF file systems, matching the original PowerShell-based
        /// implementation.
        /// </summary>
        /// <param name="sourceDirectory">Directory whose contents are embedded.</param>
        /// <param name="isoPath">Output path of the .iso file.</param>
        /// <param name="volumeName">Volume label stored in the image.</param>
        public static void CreateIsoFromDirectory(string sourceDirectory, string isoPath, string volumeName)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "IMAPI2 ISO creation is only available on Windows.");
            }

            // Ensure the output directory exists (same as the previous
            // CreateIsoWithPowerShell implementation).
            string? isoDir = Path.GetDirectoryName(isoPath);
            if (!string.IsNullOrEmpty(isoDir))
            {
                Directory.CreateDirectory(isoDir);
            }

            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    CreateIsoOnSta(sourceDirectory, isoPath, volumeName);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = "VMCreate IMAPI2 ISO writer",
            };

            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join();

            if (failure is not null)
            {
                throw new InvalidOperationException(
                    $"IMAPI2 failed to create ISO at '{isoPath}': {failure.Message}", failure);
            }
        }

        /// <summary>
        /// Builds the image on the current (STA) thread: file system selection,
        /// volume name, tree import and result streaming — same call sequence
        /// the old PowerShell script performed.
        /// </summary>
        private static void CreateIsoOnSta(string sourceDirectory, string isoPath, string volumeName)
        {
            // throwOnError:true guarantees non-null (the annotation is Type? —
            // the CLR doesn't encode the guarantee), hence the forgiving !.
            Type fileSystemImageType = Type.GetTypeFromProgID(FileSystemImageProgId, throwOnError: true)!;
            var image = (IFileSystemImage)Activator.CreateInstance(fileSystemImageType)!;

            image.FileSystemsToCreate = FsiFileSystems.ISO9660 | FsiFileSystems.Joliet | FsiFileSystems.UDF;
            image.VolumeName = volumeName;

            // Embed the entire staging tree. AddTree's native signature is
            // (BSTR sourceDirectory, VARIANT_BOOL includeBaseDirectory) — exactly
            // two parameters; includeBaseDirectory=false matches the old script.
            image.Root.AddTree(sourceDirectory, includeBaseDirectory: false);

            IFileSystemImageResult result = image.CreateResultImage();
            IStream imageStream = result.ImageStream
                ?? throw new InvalidOperationException("IMAPI2 returned a null image stream.");

            using var output = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteStreamToFile(imageStream, output);
        }

        /// <summary>
        /// Copies an IStream (the raw ISO bytes IMAPI2 produced) into a file.
        /// Direct replacement of the old Add-Type IStreamHelper.
        /// </summary>
        private static void WriteStreamToFile(IStream source, FileStream destination)
        {
            byte[] buffer = new byte[32768];
            IntPtr bytesReadPtr = Marshal.AllocHGlobal(4);
            try
            {
                while (true)
                {
                    source.Read(buffer, buffer.Length, bytesReadPtr);
                    int bytesRead = Marshal.ReadInt32(bytesReadPtr);
                    if (bytesRead <= 0)
                    {
                        break;
                    }
                    destination.Write(buffer, 0, bytesRead);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(bytesReadPtr);
            }
        }
    }

    /// <summary>
    /// File system bit flags for IFileSystemImage.FileSystemsToCreate.
    /// Values per the FsiFileSystems enum in imapi2fs.h.
    /// </summary>
    [Flags]
    internal enum FsiFileSystems : int
    {
        None = 0,
        ISO9660 = 1,   // FsiFileSystemISO9660
        Joliet = 2,    // FsiFileSystemJoliet
        UDF = 4,        // FsiFileSystemUDF
    }

    /// <summary>
    /// IFsiDirectoryItem — directory in an IMAPI2 file system image
    /// (IID 2C941FDC-975B-59BE-A960-9A2A262853A5, dual/IDispatch-derived).
    /// Declared FLAT: the CLR's slot computation for ComImport interfaces
    /// that inherit from another user-declared ComImport interface (rather
    /// than from object/IUnknown) does not match the native vtable, which
    /// shifts every member and silently mis-targets calls. This layout
    /// embeds the twelve IFsiItem members (slots 7–18) directly followed
    /// by the ten IFsiDirectoryItem members (slots 19–28), matching the
    /// merged vtable MIDL produces for "IFsiDirectoryItem : IFsiItem".
    /// </summary>
    [ComImport]
    [Guid("2C941FDC-975B-59BE-A960-9A2A262853A5")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IFsiDirectoryItem
    {
        // ── IFsiItem members slots 7–18 (never invoked from this codebase) ──
        string Name
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            get;
        }

        string FullPath
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            get;
        }

        double CreationTime { get; set; }           // COM DATE
        double LastAccessedTime { get; set; }      // COM DATE
        double LastModifiedTime { get; set; }      // COM DATE (flat filler)
        bool IsHidden { get; set; }                 // VARIANT_BOOL

        [return: MarshalAs(UnmanagedType.BStr)]
        string FileSystemName(FsiFileSystems fileSystem);

        [return: MarshalAs(UnmanagedType.BStr)]
        string FileSystemPath(FsiFileSystems fileSystem);

        // ── IFsiDirectoryItem members slots 19–28 per imapi2fs.h ──
        // vtable order: get__NewEnum, get_Item, get_Count, get_EnumFsiItems,
        // AddDirectory, AddFile, AddTree, Add, Remove, RemoveTree.
        IntPtr NewEnum { get; }                     // get__NewEnum — filler

        IntPtr GetItem(string path);               // get_Item — filler
        int Count { get; }                          // get_Count — filler
        IntPtr GetEnumFsiItems();                   // get_EnumFsiItems — filler
        void AddDirectory(string path);            // AddDirectory — filler
        void AddFile(string path, IntPtr fileData); // AddFile(IStream*) — filler

        void AddTree(
            [MarshalAs(UnmanagedType.BStr)] string sourceDirectory,
            [MarshalAs(UnmanagedType.VariantBool)] bool includeBaseDirectory);

        void Add(IntPtr item);                      // Add(IFsiItem*) — filler
        void Remove(string path);                   // filler
        void RemoveTree(string path);               // filler
    }

    /// <summary>
    /// IFileSystemImage — builds the file system image
    /// (IID 2C941FE1-975B-59BE-A960-9A2A262853A5, dual/IDispatch-derived).
    /// All members up to and including CreateResultImage are declared in
    /// native vtable order; members after CreateResultImage are omitted
    /// (they can never be reached via this interface without first passing
    /// through an earlier slot).
    /// </summary>
    [ComImport]
    [Guid("2C941FE1-975B-59BE-A960-9A2A262853A5")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IFileSystemImage
    {
        // ── invoked members (also anchor the vtable start) ──
        IFsiDirectoryItem Root
        {
            [return: MarshalAs(UnmanagedType.Interface)]
            get;                                        // get_Root — invoked
        }

        // ── vtable filler, never invoked ──
        int SessionStartBlock { get; set; }
        int FreeMediaBlocks { get; set; }
        void SetMaxMediaBlocksFromDevice(IntPtr discRecorder);
        int UsedBlocks { get; }

        // ── invoked members ──
        // BSTR-valued volume name: default managed string marshaling in COM
        // vtable calls is LPWStr, which would hand IMAPI2 a bare pointer where
        // it expects a length-prefixed BSTR — hence the explicit attributes.
        string VolumeName
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            get;
            [param: MarshalAs(UnmanagedType.BStr)]
            set;
        }

        // ── vtable filler, never invoked ──
        string ImportedVolumeName
        {
            [return: MarshalAs(UnmanagedType.BStr)]
            get;
        }
        IntPtr BootImageOptions { get; set; }
        int FileCount { get; }
        int DirectoryCount { get; }
        string WorkingDirectory { get; set; }
        int ChangePoint { get; }
        bool StrictFileSystemCompliance { get; set; }
        bool UseRestrictedCharacterSet { get; set; }

        // ── invoked members ──
        FsiFileSystems FileSystemsToCreate { get; set; }   // get/put — put invoked

        // ── vtable filler, never invoked ──
        FsiFileSystems FileSystemsSupported { get; }
        void PutUdfRevision(int revision);                  // put before get in native vtable
        int GetUdfRevision();
        IntPtr GetUdfRevisionsSupported();
        void ChooseImageDefaults(IntPtr discRecorder);
        void ChooseImageDefaultsForMediaType(int mediaType);
        void PutIso9660InterchangeLevel(int level);        // put before get in native vtable
        int GetIso9660InterchangeLevel();
        IntPtr GetIso9660InterchangeLevelsSupported();

        // ── invoked members ──
        [return: MarshalAs(UnmanagedType.Interface)]
        IFileSystemImageResult CreateResultImage();        // last member we call
    }

    /// <summary>
    /// IFileSystemImageResult — the produced image
    /// (IID 2C941FD8-975B-59BE-A960-9A2A262853A5, dual/IDispatch-derived).
    /// get_ImageStream is the first member, so nothing else needs declaring.
    /// </summary>
    [ComImport]
    [Guid("2C941FD8-975B-59BE-A960-9A2A262853A5")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IFileSystemImageResult
    {
        IStream ImageStream
        {
            [return: MarshalAs(UnmanagedType.Interface)]
            get;
        }
    }
}
using VMCreate;

namespace VMCreate.Tests
{
    /// <summary>
    /// Regression tests for the MBR-cloning new-drive path.
    /// Bug: AddNewHardDrive used to build "{vmName}.vhdx" which collided with
    /// the converted source VHDX produced by VmdkMediaHandler (same name).
    /// Error 0x80070050 "The file exists" would crash VM creation.
    /// </summary>
    [TestClass]
    public sealed class NewDrivePathTests
    {
        /// <summary>
        /// The new boot drive path must never equal the converted source media
        /// path ("{vmPath}\{vmName}.vhdx") that VmdkMediaHandler produces.
        /// </summary>
        [TestMethod]
        public void NewDrivePath_DoesNotCollideWithConvertedSourceDisk()
        {
            string vmPath = @"C:\Hyper-V\Virtual hard disks";
            string vmName = "PwnCloudOS_20260309234601";

            // This is the path VmdkMediaHandler produces for the converted VMDK
            string convertedSourcePath = System.IO.Path.Combine(vmPath, $"{vmName}.vhdx");

            // This is the path AddNewHardDrive uses for the new boot drive
            string newDrivePath = PowerShellHyperVManager.GetNewDrivePath(vmPath, vmName);

            Assert.AreNotEqual(
                convertedSourcePath,
                newDrivePath,
                "New boot drive path must differ from the converted source VHDX to avoid 'file exists' error.");
        }

        [TestMethod]
        public void NewDrivePath_EndsWithBootVhdx()
        {
            string vmPath = @"C:\Hyper-V\Virtual hard disks";
            string vmName = "TestVM";

            string result = PowerShellHyperVManager.GetNewDrivePath(vmPath, vmName);

            Assert.IsTrue(
                result.EndsWith("_boot.vhdx"),
                $"Expected path ending with '_boot.vhdx', got: {result}");
        }

        [TestMethod]
        public void NewDrivePath_IsInsideExpectedDirectory()
        {
            string vmPath = @"C:\Hyper-V\Virtual hard disks";
            string vmName = "MyVM_20260101120000";

            string result = PowerShellHyperVManager.GetNewDrivePath(vmPath, vmName);

            Assert.IsTrue(
                result.StartsWith(vmPath),
                $"Expected path under '{vmPath}', got: {result}");
        }
    }
}

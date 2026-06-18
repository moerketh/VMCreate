using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace VMCreate.HyperV.Unattend
{
    /// <summary>
    /// Helper for editing offline Windows registry hives via reg.exe. Extracted from
    /// <see cref="UnattendInjector"/...gt; so it can be unit-tested in isolation.
    /// </summary>
    public interface IOfflineRegistryEditor
    {
        void LoadHive(string hivePath, string mountName);
        void UnloadHive(string mountName);
        void AddKey(string keyPath);
        void SetDword(string keyPath, string valueName, int value);
        void SetString(string keyPath, string valueName, string value);
        void SetServiceStart(string hiveMountName, string controlSet, string serviceName, int startValue);
    }

    public sealed class OfflineRegistryEditor : IOfflineRegistryEditor
    {
        private readonly IPowerShellExecutor _powerShell;
        private readonly ILogger<OfflineRegistryEditor> _logger;

        public OfflineRegistryEditor(IPowerShellExecutor powerShell, ILogger<OfflineRegistryEditor> logger)
        {
            _powerShell = powerShell ?? throw new ArgumentNullException(nameof(powerShell));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void LoadHive(string hivePath, string mountName)
        {
            if (!File.Exists(hivePath))
                throw new FileNotFoundException("Hive not found", hivePath);

            var result = _powerShell.RunCommand("reg",
                ("ArgumentList", new[] { "load", $"HKLM\\{mountName}", hivePath }));
            if (result.HadErrors)
                throw new InvalidOperationException($"Failed to load hive {hivePath}: {result.ErrorSummary}");
            _logger.LogDebug("Loaded hive {HivePath} as {MountName}", hivePath, mountName);
        }

        public void UnloadHive(string mountName)
        {
            var result = _powerShell.RunCommand("reg",
                ("ArgumentList", new[] { "unload", $"HKLM\\{mountName}" }));
            if (result.HadErrors)
                _logger.LogWarning("Failed to unload hive {MountName}: {Errors}", mountName, result.ErrorSummary);
        }

        public void AddKey(string keyPath)
        {
            _powerShell.RunCommand("reg", ("ArgumentList", new[] { "add", keyPath, "/f" }));
        }

        public void SetDword(string keyPath, string valueName, int value)
        {
            _powerShell.RunCommand("reg", ("ArgumentList", new[]
            {
                "add", keyPath, "/v", valueName, "/t", "REG_DWORD", "/d", value.ToString(), "/f"
            }));
        }

        public void SetString(string keyPath, string valueName, string value)
        {
            _powerShell.RunCommand("reg", ("ArgumentList", new[]
            {
                "add", keyPath, "/v", valueName, "/t", "REG_SZ", "/d", value, "/f"
            }));
        }

        public void SetServiceStart(string hiveMountName, string controlSet, string serviceName, int startValue)
        {
            string keyPath = $"HKLM\\{hiveMountName}\\{controlSet}\\Services\\{serviceName}";
            SetDword(keyPath, "Start", startValue);
            _logger.LogDebug("Set service {ServiceName} Start={StartValue} in {ControlSet}", serviceName, startValue, controlSet);
        }
    }
}

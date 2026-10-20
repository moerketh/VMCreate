using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace CreateVM.HyperV.vmbus
{
    /// <summary>
    /// <see cref="IKvpHeartbeatGate"/> implementation over the same WMI
    /// machinery as <see cref="KvpBase"/>. A running hv_kvp_daemon
    /// (Linux) or integration services (Windows) publishes guest-written
    /// entries into <c>Msvm_KvpExchangeComponent.GuestExchangeItems</c> /
    /// <c>GuestIntrinsicItems</c>; an empty pair of pools after boot means the
    /// guest side of the exchange is dead — installed but not started.
    /// <para>
    /// Caveat proven live on a 2025-era host: the HOST-side pools can lag the
    /// guest daemon by many minutes on first boot (daemon answering ~4 s after
    /// SSH became available, pools still empty after the 120 s default, filled
    /// ~19 min after boot). Callers must therefore treat a timeout as
    /// "unconfirmed", not "dead", and disambiguate with a guest-side witness
    /// probe before failing; <see cref="LastDiagnostics"/> carries what this
    /// gate observed for that message.
    /// </para>
    /// </summary>
    public class KvpHeartbeatGate : KvpBase, IKvpHeartbeatGate
    {
        /// <summary>
        /// Overridable WMI reader so unit tests can simulate live, dead and
        /// transiently-failing guests without a Hyper-V host.
        /// </summary>
        protected virtual Dictionary<string, string?> ReadGuestKvpItems(string vmGuid)
        {
            var kvps = new Dictionary<string, string?>();

            ManagementScope scope = new ManagementScope(@"root\virtualization\v2");
            ObjectQuery query = new ObjectQuery($"SELECT * FROM Msvm_KvpExchangeComponent WHERE SystemName = '{vmGuid}'");
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        // Both pools prove the guest daemon is answering:
                        // GuestExchangeItems carries daemon-published entries,
                        // GuestIntrinsicItems the OS identity block. Either
                        // being populated is a heartbeat.
                        AppendItems(kvps, (string[]?)obj["GuestExchangeItems"]);
                        AppendItems(kvps, (string[]?)obj["GuestIntrinsicItems"]);
                    }
                }
            }
            return kvps;
        }

        public KvpGateDiagnostics? LastDiagnostics { get; private set; }

        public async Task<bool> WaitForGuestKvpAsync(
            string vmName,
            CancellationToken cancellationToken,
            int timeoutSeconds = 420,
            int pollIntervalMs = 2000)
        {
            ValidateVmName(vmName);
            cancellationToken.ThrowIfCancellationRequested();

            DateTime startTime = DateTime.UtcNow;
            var diagnostics = new KvpGateDiagnostics { StartedUtc = startTime };
            LastDiagnostics = diagnostics;

            while (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.PollCount++;
                string? vmGuid = GetVMGuid(vmName);
                if (!string.IsNullOrEmpty(vmGuid))
                {
                    diagnostics.VmGuidResolved = true;
                    try
                    {
                        var items = ReadGuestKvpItems(vmGuid);
                        diagnostics.PoolReadCount++;
                        if (items.Count > 0)
                        {
                            diagnostics.ConfirmedGuestKvp = true;
                            diagnostics.LastPollUtc = DateTime.UtcNow;
                            return true;
                        }
                    }
                    catch (ManagementException ex)
                    {
                        // Transient WMI failure while the VM settles — keep polling.
                        diagnostics.LastException = ex.Message;
                    }
                }
                diagnostics.LastPollUtc = DateTime.UtcNow;

                if (timeoutSeconds > 0 && (DateTime.UtcNow - startTime).TotalSeconds > timeoutSeconds)
                    return false;

                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            return false;
        }

        /// <summary>Parses the XML-wrapped KVP entries the same way as <see cref="KvpBase"/>.</summary>
        private static void AppendItems(Dictionary<string, string?> target, string[]? items)
        {
            if (items == null)
                return;

            foreach (string item in items)
            {
                try
                {
                    var xml = System.Xml.Linq.XDocument.Parse(item);
                    var nameProp = xml.Descendants("PROPERTY").FirstOrDefault(p => (string?)p.Attribute("NAME") == "Name");
                    var dataProp = xml.Descendants("PROPERTY").FirstOrDefault(p => (string?)p.Attribute("NAME") == "Data");

                    string? key = nameProp?.Element("VALUE")?.Value;
                    if (!string.IsNullOrEmpty(key))
                        target[key!] = dataProp?.Element("VALUE")?.Value;
                }
                catch (System.Xml.XmlException)
                {
                    // Malformed entry — skip; other entries may still prove liveness.
                }
            }
        }
    }
}

using VMCreate.Manager.Inventory;

namespace VMCreate.Manager.Ssh
{
    public enum SshTransport
    {
        /// <summary>Plain TCP to the VM's first reported IP address.</summary>
        Network,

        /// <summary>
        /// AF_HYPERV (vsock) via <c>hvc.exe nc</c> as ProxyCommand. Needs no
        /// guest networking, so it reaches isolated lab VMs; the guest must run
        /// sshd on vsock port 22 (VMCreate ISO guests do).
        /// </summary>
        Vsock,
    }

    /// <summary>The executable and argument list for an interactive ssh session.</summary>
    public sealed record SshCommand(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>
    /// Builds ssh command lines. Kept separate from process launching so the
    /// quoting rules (ssh expands %-tokens inside ProxyCommand itself) are testable.
    /// </summary>
    public static class SshCommandBuilder
    {
        public static string SshExePath =>
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\OpenSSH\ssh.exe");

        public static string HvcExePath =>
            Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\hvc.exe");

        /// <summary>Network when the VM reports an IP address, otherwise vsock.</summary>
        public static SshTransport DefaultTransport(VmSummary vm)
            => vm.IpAddresses.Count > 0 ? SshTransport.Network : SshTransport.Vsock;

        public static SshCommand Build(VmSummary vm, string username, SshTransport transport, string? sshExe = null, string? hvcExe = null)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("A user name is required.", nameof(username));
            username = username.Trim();
            sshExe ??= File.Exists(SshExePath) ? SshExePath : "ssh.exe";

            if (transport == SshTransport.Network)
            {
                string ip = vm.IpAddresses.FirstOrDefault()
                    ?? throw new InvalidOperationException($"'{vm.Name}' reports no IP address. Is it running with guest services, or use SSH over vsock.");
                // IPv6 literals need brackets in user@host form.
                string host = ip.Contains(':') ? $"[{ip}]" : ip;
                return new SshCommand(sshExe, new[] { $"{username}@{host}" });
            }

            hvcExe ??= HvcExePath;
            if (!File.Exists(hvcExe))
                throw new InvalidOperationException($"SSH over vsock needs '{hvcExe}', which ships with the Hyper-V management tools.");
            // hvc resolves the VM by GUID; %h is replaced by ssh with the host
            // part of user@host. Quote the hvc path for ssh in case it has spaces.
            return new SshCommand(sshExe, new[]
            {
                "-o", $"ProxyCommand=\"{hvcExe}\" nc -t vsock %h 22",
                // The GUID is stable per VM; keep its host key under that name.
                "-o", $"HostKeyAlias=hyperv-{vm.Id:D}",
                $"{username}@{vm.Id:D}",
            });
        }
    }
}

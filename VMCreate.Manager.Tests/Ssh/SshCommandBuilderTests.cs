using VMCreate.Manager.Ssh;
using VMCreate.Manager.Inventory;

namespace VMCreate.Manager.Tests.Ssh
{
    [TestClass]
    public class SshCommandBuilderTests
    {
        private static readonly Guid Id = Guid.Parse("39EFCF77-B4D6-4D1B-B7FE-CB8537EB987E");

        private static VmSummary Vm(params string[] ips) => new()
        {
            Id = Id, Name = "HTB Academy", State = VmState.Running, IpAddresses = ips,
        };

        [TestMethod]
        public void Network_UsesFirstIp()
        {
            var cmd = SshCommandBuilder.Build(Vm("192.168.38.109", "2001:db8::5"), "kali", SshTransport.Network, sshExe: "ssh.exe");
            CollectionAssert.AreEqual(new[] { "kali@192.168.38.109" }, cmd.Arguments.ToArray());
        }

        [TestMethod]
        public void Network_Ipv6_IsBracketed()
        {
            var cmd = SshCommandBuilder.Build(Vm("2001:db8::5"), "root", SshTransport.Network, sshExe: "ssh.exe");
            Assert.AreEqual("root@[2001:db8::5]", cmd.Arguments[0]);
        }

        [TestMethod]
        public void Network_NoIp_Throws()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => SshCommandBuilder.Build(Vm(), "root", SshTransport.Network, sshExe: "ssh.exe"));
        }

        [TestMethod]
        public void Vsock_UsesHvcProxyCommandAndGuidHost()
        {
            string hvc = typeof(SshCommandBuilderTests).Assembly.Location; // any existing file
            var cmd = SshCommandBuilder.Build(Vm(), "user", SshTransport.Vsock, sshExe: "ssh.exe", hvcExe: hvc);

            CollectionAssert.Contains(cmd.Arguments.ToArray(), $"ProxyCommand=\"{hvc}\" nc -t vsock %h 22");
            Assert.AreEqual("user@39efcf77-b4d6-4d1b-b7fe-cb8537eb987e", cmd.Arguments[^1]);
        }

        [TestMethod]
        public void Vsock_MissingHvc_Throws()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                SshCommandBuilder.Build(Vm(), "user", SshTransport.Vsock, sshExe: "ssh.exe", hvcExe: @"C:\nope\hvc.exe"));
        }

        [TestMethod]
        public void DefaultTransport_PrefersNetworkWhenIpKnown()
        {
            Assert.AreEqual(SshTransport.Network, SshCommandBuilder.DefaultTransport(Vm("10.0.0.5")));
            Assert.AreEqual(SshTransport.Vsock, SshCommandBuilder.DefaultTransport(Vm()));
        }

        [TestMethod]
        public void EmptyUser_Throws()
        {
            Assert.ThrowsExactly<ArgumentException>(() => SshCommandBuilder.Build(Vm("10.0.0.5"), " ", SshTransport.Network, sshExe: "ssh.exe"));
        }
    }
}

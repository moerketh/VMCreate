using VMCreate.Manager.Inventory;

namespace VMCreate.Manager.Tests.Inventory
{
    [TestClass]
    public class DisplayFormatTests
    {
        [TestMethod]
        [DataRow(512UL, "512 MB")]
        [DataRow(1024UL, "1 GB")]
        [DataRow(16492UL, "16.1 GB")]
        public void Memory(ulong mb, string expected) => Assert.AreEqual(expected, DisplayFormat.Memory(mb));

        [TestMethod]
        public void Uptime()
        {
            Assert.AreEqual("", DisplayFormat.Uptime(TimeSpan.Zero));
            Assert.AreEqual("00:01:30", DisplayFormat.Uptime(TimeSpan.FromSeconds(90)));
            Assert.AreEqual("2d 03:04", DisplayFormat.Uptime(new TimeSpan(2, 3, 4, 5)));
        }
    }
}

using VMCreate.Manager.HyperV;

namespace VMCreate.Manager.Tests.Inventory
{
    [TestClass]
    public class ThumbnailTests
    {
        [TestMethod]
        [DataRow(1920, 1080, 640, 480, 640, 360)] // 16:9 guest, width-bound
        [DataRow(1024, 768, 640, 480, 640, 480)]  // 4:3 guest fills the box
        [DataRow(800, 1280, 640, 480, 300, 480)]  // portrait guest, height-bound
        public void FitThumbnail_KeepsAspectInsideBox(int gw, int gh, int mw, int mh, int ew, int eh)
        {
            Assert.AreEqual((ew, eh), WmiHyperVHost.FitThumbnail(gw, gh, mw, mh));
        }

        [TestMethod]
        public void ToThumbnail_TrimsTrailingBytes()
        {
            // Hyper-V returns width*height*2 + 4 bytes; observed 460804 for 640x360.
            var data = new byte[640 * 360 * 2 + 4];
            data[0] = 0xAB;
            var thumb = WmiHyperVHost.ToThumbnail(640, 360, data)!;
            Assert.AreEqual(640 * 360 * 2, thumb.Pixels.Length);
            Assert.AreEqual(0xAB, thumb.Pixels[0], "Pixel data starts at offset 0");
        }

        [TestMethod]
        public void ToThumbnail_ShortBuffer_IsNull()
        {
            Assert.IsNull(WmiHyperVHost.ToThumbnail(640, 360, new byte[100]));
        }
    }
}

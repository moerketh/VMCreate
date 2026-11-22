using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System.Net.Http;

namespace VMCreate.Tests.HyperV
{
    /// <summary>
    /// Pins HtbApiClient's parsing of GET /api/v5/connections, mirroring
    /// Athena OS htb-toolkit (src/vpn.rs): <c>data</c> is an array; entries
    /// carry <c>type</c>, an optional <c>available</c> flag and an
    /// <c>assigned_server</c>; only labs, starting_point and fortresses are
    /// fetched.
    /// </summary>
    [TestClass]
    public class HtbApiClientConnectionsTests
    {
        private const string Sample = """
            {
              "status": true,
              "data": [
                { "type": "labs", "location_type_friendly": "EU - VIP",
                  "assigned_server": { "id": 211, "friendly_name": "EU VIP 7", "current_clients": 12, "location": "EU" } },
                { "type": "starting_point", "location_type_friendly": "EU - Free",
                  "assigned_server": { "id": 412, "friendly_name": "EU StartingPoint 1", "current_clients": 3, "location": "EU" } },
                { "type": "fortresses", "assigned_server": null },
                { "type": "prolab", "assigned_server": { "id": 99, "friendly_name": "Dante", "current_clients": 1, "location": "EU" } },
                { "type": "competitive", "available": false,
                  "assigned_server": { "id": 77, "friendly_name": "EU Release Arena", "current_clients": 0, "location": "EU" } }
              ]
            }
            """;

        [TestMethod]
        public void ParseConnections_KeepsSupportedProductsWithAssignedServer()
        {
            var conns = HtbApiClient.ParseConnections(Sample);

            CollectionAssert.AreEqual(new[] { "labs", "starting_point" }, conns.Select(c => c.Product).ToArray());

            var labs = conns[0];
            Assert.AreEqual(211, labs.ServerId);
            Assert.AreEqual("Labs EU VIP 7", labs.DisplayName);
            Assert.AreEqual("labs_eu_vip_7.ovpn", labs.GuestFileName);

            Assert.AreEqual("starting_point_eu_startingpoint_1.ovpn", conns[1].GuestFileName);
        }

        [TestMethod]
        public void ParseConnections_SkipsUnavailableEntry()
        {
            var conns = HtbApiClient.ParseConnections("""
                { "data": [ { "type": "labs", "available": false,
                  "assigned_server": { "id": 5, "friendly_name": "US Free 1" } } ] }
                """);

            Assert.AreEqual(0, conns.Count);
        }

        [TestMethod]
        public void ParseConnections_SkipsNonPositiveServerId()
        {
            var conns = HtbApiClient.ParseConnections("""
                { "data": [ { "type": "fortresses", "assigned_server": { "id": 0, "friendly_name": "x" } } ] }
                """);

            Assert.AreEqual(0, conns.Count);
        }

        [TestMethod]
        public void ParseConnections_DataNotArray_ThrowsWithApiMessage()
        {
            // The old v4 shape (data as an object), or a removed-endpoint reply.
            var ex = Assert.Throws<HttpRequestException>(() =>
                HtbApiClient.ParseConnections("""{ "message": "This endpoint has been removed" }"""));

            StringAssert.Contains(ex.Message, "This endpoint has been removed");
        }

        [TestMethod]
        public void SanitizeFileName_KeepsOnlyAsciiLettersAndDigits()
        {
            Assert.AreEqual("eu_vip_7", HtbApiClient.SanitizeFileName("EU VIP 7"));
            Assert.AreEqual("us_free_1___", HtbApiClient.SanitizeFileName("US-Free 1/.."));
            Assert.AreEqual("unknown", HtbApiClient.SanitizeFileName("  "));
        }

        [DataTestMethod]
        [DataRow("labs", null, true)]
        [DataRow("labs", "", true)]
        [DataRow("labs", "machines", true)]
        [DataRow("starting_point", "labs, sp", true)]
        [DataRow("fortresses", "fort", true)]
        [DataRow("fortresses", "labs,sp", false)]
        [DataRow("academy", "academy", false)]
        public void MatchesProductFilter_UsesHtbToolkitAliases(string product, string? filter, bool expected)
        {
            Assert.AreEqual(expected, HtbApiClient.MatchesProductFilter(product, filter));
        }
    }
}

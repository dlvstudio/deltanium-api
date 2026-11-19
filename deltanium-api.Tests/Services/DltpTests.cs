using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using DeltaniumApi.Services.Dltp;
using FluentAssertions;
using Xunit;

namespace deltanium_api.Tests.Services
{
    public class DltpTests
    {
        [Theory]
        [InlineData("tcps://store-1.deltanium.com:7333", true)]
        [InlineData("tcp://localhost:7333", true)]
        [InlineData("tcp://store-1.deltanium.com:7333", false)] // plain TCP only for local dev
        [InlineData("https://store-1.deltanium.com", false)]
        [InlineData("tcps://store-1.deltanium.com", false)]       // port required
        [InlineData("", false)]
        public void Tcp_endpoint_validation(string value, bool ok) =>
            NodeAddress.IsAcceptableTcp(value).Should().Be(ok);

        [Fact]
        public void Https_endpoint_wins_when_a_node_has_both()
        {
            NodeAddress.Of("https://store-1.deltanium.com/", "tcps://store-1.deltanium.com:7333")
                .Should().Be("https://store-1.deltanium.com");
        }

        [Fact]
        public void Tcp_only_node_gets_a_routable_base_url()
        {
            NodeAddress.Of("", "tcps://store-9.example.com:7333").Should().Be("https://store-9.example.com:7333");
            NodeAddress.Of(null, null).Should().Be("");
        }

        [Fact]
        public void Endpoint_changes_keep_at_least_one_way_in()
        {
            var add = DeltaniumApi.Controllers.StoreNodeController.ApplyEndpoints("https://s1.example.com", null,
                new DeltaniumApi.Controllers.StoreNodeController.NodeEndpointsRequest { TcpEndpoint = "tcps://s1.example.com:7333" });
            add.Ok.Should().BeTrue();
            add.Https.Should().Be("https://s1.example.com");
            add.Tcp.Should().Be("tcps://s1.example.com:7333");

            var dropHttps = DeltaniumApi.Controllers.StoreNodeController.ApplyEndpoints("https://s1.example.com", "tcps://s1.example.com:7333",
                new DeltaniumApi.Controllers.StoreNodeController.NodeEndpointsRequest { Endpoint = "" });
            dropHttps.Ok.Should().BeTrue();
            dropHttps.Https.Should().BeEmpty();

            var dropAll = DeltaniumApi.Controllers.StoreNodeController.ApplyEndpoints("https://s1.example.com", null,
                new DeltaniumApi.Controllers.StoreNodeController.NodeEndpointsRequest { Endpoint = "" });
            dropAll.Ok.Should().BeFalse();
        }

        [Fact]
        public async Task Request_to_a_tcp_only_node_travels_over_dltp()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var server = Task.Run(async () =>
            {
                using var conn = await listener.AcceptTcpClientAsync();
                await using var s = conn.GetStream();
                await DltpWire.ExpectPrefaceAsync(s, default);
                await DltpWire.WritePrefaceAsync(s, default);
                var req = await DltpWire.ReadFrameAsync<DltpRequest>(s, default);
                var echo = $"{req.Method} {req.Path} host={req.Headers["Host"]} sig={req.Headers.GetValueOrDefault("X-Signature")}";
                await DltpWire.WriteFrameAsync(s, new DltpResponse
                {
                    Id = req.Id,
                    Status = 200,
                    Headers = { ["Content-Type"] = "text/plain" },
                    Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(echo))
                }, default);
            });

            try
            {
                var baseUrl = NodeAddress.Of("", $"tcp://localhost:{port}");
                using var http = new HttpClient(new DltpRoutingHandler(new HttpClientHandler()));
                using var msg = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/version?x=1");
                msg.Headers.Add("X-Signature", "abc");
                using var res = await http.SendAsync(msg);

                res.StatusCode.Should().Be(HttpStatusCode.OK);
                res.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
                (await res.Content.ReadAsStringAsync()).Should().Be($"GET /api/version?x=1 host=localhost:{port} sig=abc");
                await server;
            }
            finally { listener.Stop(); }
        }
    }
}

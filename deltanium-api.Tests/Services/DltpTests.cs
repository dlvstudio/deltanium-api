using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        [InlineData("tcp://localhost:7333", false)]                // local only when Central runs in Development
        [InlineData("tcp://store-1.deltanium.com:7333", false)] // plain TCP only for local dev
        [InlineData("https://store-1.deltanium.com", false)]
        [InlineData("tcps://store-1.deltanium.com", false)]       // port required
        [InlineData("", false)]
        [InlineData("tcps://203.0.113.7:7333", true)]             // public IP: home / personal computer
        [InlineData("tcps://[2001:db8::7]:7333", true)]
        [InlineData("tcps://192.168.1.20:7333", false)]           // private, loopback, CGNAT, link-local: never
        [InlineData("tcps://10.0.0.5:7333", false)]
        [InlineData("tcps://172.16.0.1:7333", false)]
        [InlineData("tcps://127.0.0.1:7333", false)]
        [InlineData("tcps://100.64.1.1:7333", false)]
        [InlineData("tcps://169.254.1.1:7333", false)]
        [InlineData("tcps://[fd00::1]:7333", false)]
        [InlineData("tcps://[::1]:7333", false)]
        [InlineData("tcps://auto:7333", false)]                   // must be resolved first
        public void Tcp_endpoint_validation(string value, bool ok) =>
            NodeAddress.IsAcceptableTcp(value).Should().Be(ok);

        [Fact]
        public void Local_endpoints_only_in_development()
        {
            NodeAddress.IsAcceptableHttps("http://localhost:5001").Should().BeFalse();
            NodeAddress.IsAcceptableHttps("https://127.0.0.1:5001").Should().BeFalse();
            NodeAddress.IsAcceptableHttps("https://10.0.0.5").Should().BeFalse();
            NodeAddress.IsAcceptableHttps("http://store-1.deltanium.com").Should().BeFalse();
            NodeAddress.IsAcceptableHttps("https://store-1.deltanium.com").Should().BeTrue();
            NodeAddress.IsAcceptableHttps("https://203.0.113.7").Should().BeTrue();
        }

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

        [Fact]
        public void Auto_endpoint_takes_the_callers_public_ip()
        {
            NodeAddress.ResolveAuto("tcps://auto:7333", IPAddress.Parse("203.0.113.7")).Should().Be("tcps://203.0.113.7:7333");
            NodeAddress.ResolveAuto("tcps://auto:7333", IPAddress.Parse("::ffff:203.0.113.7")).Should().Be("tcps://203.0.113.7:7333");
            NodeAddress.ResolveAuto("tcps://store.example.com:7333", IPAddress.Parse("203.0.113.7")).Should().Be("tcps://store.example.com:7333");

            var home = DeltaniumApi.Controllers.StoreNodeController.ApplySelfEndpoint("", null, "{\"tcpEndpoint\":\"tcps://auto:7333\"}", IPAddress.Parse("203.0.113.7"));
            home.Ok.Should().BeTrue();
            home.Tcp.Should().Be("tcps://203.0.113.7:7333");

            var lan = DeltaniumApi.Controllers.StoreNodeController.ApplySelfEndpoint("", null, "{\"tcpEndpoint\":\"tcps://auto:7333\"}", IPAddress.Parse("192.168.1.20"));
            lan.Ok.Should().BeFalse();

            var drop = DeltaniumApi.Controllers.StoreNodeController.ApplySelfEndpoint("https://s.example.com", "tcps://203.0.113.7:7333", "{\"tcpEndpoint\":\"\"}", null);
            drop.Ok.Should().BeTrue();
            drop.Tcp.Should().BeNull();
        }

        [Theory]
        [InlineData(true, true)]    // the registered node: accepted
        [InlineData(false, false)]  // someone else's key on that address: refused
        public async Task Ip_endpoint_is_authenticated_with_the_node_key(bool realNode, bool accepted)
        {
            var nodeKey = new NBitcoin.Key();
            var signingKey = realNode ? nodeKey : new NBitcoin.Key();
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var made = new CertificateRequest("CN=deltanium-node", ec, HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cert = new X509Certificate2(made.Export(X509ContentType.Pkcs12));

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var conn = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(conn.GetStream());
                await ssl.AuthenticateAsServerAsync(cert);
                await DltpWire.ExpectPrefaceAsync(ssl, default);
                await DltpWire.WritePrefaceAsync(ssl, default);
                var hello = await DltpWire.ReadFrameAsync<DltpRequest>(ssl, default);
                hello.Method.Should().Be("HELLO");
                var msg = DltpIdentity.ProofMessage(DltpIdentity.CertHash(cert));
                var proof = Convert.ToBase64String(signingKey.Sign(new NBitcoin.uint256(SHA256.HashData(Encoding.UTF8.GetBytes(msg)))).ToDER());
                await DltpWire.WriteFrameAsync(ssl, new DltpResponse
                {
                    Id = 0, Status = 200,
                    Headers = { ["X-Node-PubKey"] = nodeKey.PubKey.ToHex(), ["X-Node-Proof"] = proof }
                }, default);
                var req = await DltpWire.ReadFrameAsync<DltpRequest>(ssl, default);
                await DltpWire.WriteFrameAsync(ssl, new DltpResponse { Id = req.Id, Status = 200, Body = Convert.ToBase64String(Encoding.UTF8.GetBytes("ok")) }, default);
            });

            try
            {
                // 127.0.0.1 stands in for a public IP here; the target carries the key Central has on record.
                var target = new DltpTarget(true, "127.0.0.1", port, nodeKey.PubKey.ToHex());
                target.KeyMode.Should().BeTrue();
                using var msg = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/api/version");
                if (accepted)
                {
                    using var res = await DltpRoutingHandler.SendDltpAsync(target, msg, default);
                    (await res.Content.ReadAsStringAsync()).Should().Be("ok");
                    await server;
                }
                else
                {
                    var act = () => DltpRoutingHandler.SendDltpAsync(target, msg, default);
                    await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*did not prove*");
                }
            }
            finally { listener.Stop(); }
        }
    }
}

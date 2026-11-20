using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DeltaniumApi.Services.Dltp
{
    /// <summary>
    /// HttpClient handler that sends requests for TCP-only nodes over DLTP and everything else over HTTP(S)
    /// as before. One connection per request for now (nodes answer quickly; pooling can come later).
    /// </summary>
    public class DltpRoutingHandler : DelegatingHandler
    {
        private static long _nextId;

        public DltpRoutingHandler() { }
        public DltpRoutingHandler(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri == null || !NodeAddress.TryGetTcp(request.RequestUri, out var target))
                return await base.SendAsync(request, ct);
            return await SendDltpAsync(target, request, ct);
        }

        public static Task<HttpResponseMessage> SendDltpAsync(bool tls, string host, int port, HttpRequestMessage request, CancellationToken ct) =>
            SendDltpAsync(new DltpTarget(tls, host, port, null), request, ct);

        public static async Task<HttpResponseMessage> SendDltpAsync(DltpTarget target, HttpRequestMessage request, CancellationToken ct)
        {
            var (tls, host, port) = (target.Tls, target.Host, target.Port);
            if (target.KeyMode && string.IsNullOrWhiteSpace(target.NodePublicKey))
                throw new HttpRequestException($"DLTP {host}:{port}: an IP endpoint needs the node's public key to authenticate it");
            var dreq = new DltpRequest
            {
                Id = Interlocked.Increment(ref _nextId),
                Method = request.Method.Method,
                Path = request.RequestUri!.PathAndQuery,
            };
            // The authority the caller addressed (host:port): Central signatures bind to it, and the node
            // passes it on as the Host header when it hands the request to its HTTP pipeline.
            dreq.Headers["Host"] = request.RequestUri.Authority;
            foreach (var h in request.Headers) dreq.Headers[h.Key] = string.Join(",", h.Value);
            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers) dreq.Headers[h.Key] = string.Join(",", h.Value);
                var bytes = await request.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 0) dreq.Body = Convert.ToBase64String(bytes);
            }

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host.Trim('[', ']'), port, ct);
            await using var raw = tcp.GetStream();
            System.IO.Stream stream = raw;
            SslStream? ssl = null;
            try
            {
                if (tls)
                {
                    var options = new SslClientAuthenticationOptions { TargetHost = host.Trim('[', ']') };
                    // IP endpoint: the certificate is self-signed; the node key vouches for it below (HELLO).
                    if (target.KeyMode) options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null;
                    ssl = new SslStream(raw, leaveInnerStreamOpen: true);
                    await ssl.AuthenticateAsClientAsync(options, ct);
                    stream = ssl;
                }
                await DltpWire.WritePrefaceAsync(stream, ct);
                await DltpWire.ExpectPrefaceAsync(stream, ct);
                if (target.KeyMode) await ProveNodeAsync(stream, ssl!, target, ct);
                await DltpWire.WriteFrameAsync(stream, dreq, ct);
                var dres = await DltpWire.ReadFrameAsync<DltpResponse>(stream, ct);
                if (dres.Id != dreq.Id) throw new HttpRequestException("DLTP response id mismatch");

                var response = new HttpResponseMessage((HttpStatusCode)dres.Status) { RequestMessage = request };
                var body = string.IsNullOrEmpty(dres.Body) ? Array.Empty<byte>() : Convert.FromBase64String(dres.Body);
                response.Content = new ByteArrayContent(body);
                foreach (var (k, v) in dres.Headers)
                {
                    if (!response.Headers.TryAddWithoutValidation(k, v))
                        response.Content.Headers.TryAddWithoutValidation(k, v);
                }
                return response;
            }
            finally
            {
                if (ssl != null) await ssl.DisposeAsync();
            }
        }

        /// <summary>HELLO: the node must sign this connection's TLS certificate with its registered key.</summary>
        private static async Task ProveNodeAsync(System.IO.Stream stream, SslStream ssl, DltpTarget target, CancellationToken ct)
        {
            var cert = ssl.RemoteCertificate ?? throw new HttpRequestException("DLTP: no server certificate");
            await DltpWire.WriteFrameAsync(stream, new DltpRequest { Id = 0, Method = DltpIdentity.HelloMethod, Path = "/" }, ct);
            var hello = await DltpWire.ReadFrameAsync<DltpResponse>(stream, ct);
            var h = new System.Collections.Generic.Dictionary<string, string>(hello.Headers, StringComparer.OrdinalIgnoreCase);
            h.TryGetValue("X-Node-PubKey", out var pk);
            h.TryGetValue("X-Node-Proof", out var proof);
            if (hello.Id != 0 || hello.Status != 200 ||
                !DltpIdentity.Verify(target.NodePublicKey!, pk, proof, DltpIdentity.CertHash(cert)))
                throw new HttpRequestException($"DLTP {target.Host}:{target.Port}: the node did not prove its registered key");
        }
    }
}

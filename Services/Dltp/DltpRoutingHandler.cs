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
            return await SendDltpAsync(target.Tls, target.Host, target.Port, request, ct);
        }

        public static async Task<HttpResponseMessage> SendDltpAsync(bool tls, string host, int port, HttpRequestMessage request, CancellationToken ct)
        {
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
                    ssl = new SslStream(raw, leaveInnerStreamOpen: true);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host.Trim('[', ']') }, ct);
                    stream = ssl;
                }
                await DltpWire.WritePrefaceAsync(stream, ct);
                await DltpWire.ExpectPrefaceAsync(stream, ct);
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
    }
}

using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Signs Central's calls to Store internal endpoints (/api/proof/*) with the Central key, replacing the
    /// shared X-Internal-Token: a Store operator who knows the token could otherwise call every other Store's
    /// internal endpoints (write blocks, read metadata, drain egress counters).
    ///
    /// Message: "DELTANIUM-CENTRAL\n{METHOD}\n{host[:port]}\n{path}\n{unixSeconds}\n{sha256hex(body)}", signed over
    /// NetworkScope like every other signature. The target host is part of the message, so a Store that receives
    /// a signed request cannot replay it against another Store. Stores verify it in CentralRequestAuth.
    /// </summary>
    public static class CentralRequestSigner
    {
        public const string TimestampHeader = "X-Central-Timestamp";
        public const string SignatureHeader = "X-Central-Signature";

        public static string BuildMessage(string method, string host, string path, string timestamp, string bodyHashHex) =>
            $"DELTANIUM-CENTRAL\n{method.ToUpperInvariant()}\n{host.ToLowerInvariant()}\n{path}\n{timestamp}\n{bodyHashHex}";

        public static string BodyHash(string? body) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body ?? ""))).ToLowerInvariant();

        /// <summary>Add the Central signature headers; <paramref name="body"/> must be the exact request body.</summary>
        public static void Sign(HttpRequestMessage req, string? body, KeyService keys, DateTimeOffset? now = null)
        {
            var uri = req.RequestUri ?? throw new InvalidOperationException("Request URI required");
            var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
            var message = BuildMessage(req.Method.Method, uri.Authority, uri.AbsolutePath, ts, BodyHash(body));
            req.Headers.TryAddWithoutValidation(TimestampHeader, ts);
            req.Headers.TryAddWithoutValidation(SignatureHeader, keys.SignDecision(message));
        }
    }
}

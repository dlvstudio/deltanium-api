using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NBitcoin;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Shared request authentication: X-User-PubKey + X-Timestamp + X-Signature
    /// over message = METHOD + path+query + timestamp + sha256hex(body).
    /// </summary>
    public class SignatureAuthHelper
    {
        private readonly KeyService _keyService;
        private readonly IStorageService _storage;

        /// <summary>Signatures accept a timestamp up to this far from now (either direction).</summary>
        public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Signed messages (signer + exact signed text) already accepted for state-changing requests, kept until they could no longer pass the
        /// timestamp check. Without it a captured POST (a transfer, a tip) could be sent again within the
        /// window and run twice. Process-wide: Central runs as one IIS worker.
        /// </summary>
        private static readonly ConcurrentDictionary<string, DateTime> UsedSignatures = new();
        private static DateTime _nextSweep = DateTime.MinValue;

        /// <summary>
        /// Record a signed message; false when it was already used, so a replay of a signed POST is rejected
        /// even inside the timestamp window. Keyed by signer and signed text rather than the signature bytes:
        /// an ECDSA signature can be rewritten into another valid encoding, the signed text cannot.
        /// </summary>
        public static bool TryConsumeSignature(string signer, string signedMessage, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (now >= _nextSweep)
            {
                _nextSweep = now.AddMinutes(1);
                foreach (var kv in UsedSignatures)
                    if (kv.Value < now) UsedSignatures.TryRemove(kv.Key, out _);
            }
            // Keep it for twice the skew: a timestamp can be up to MaxClockSkew in the future.
            var id = NormalizePubKey(signer.Trim()).ToLowerInvariant() + "|" +
                     Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedMessage)));
            return UsedSignatures.TryAdd(id, now + MaxClockSkew + MaxClockSkew);
        }

        private static bool IsStateChanging(string method) =>
            !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

        public SignatureAuthHelper(KeyService keyService, IStorageService storage)
        {
            _keyService = keyService;
            _storage = storage;
        }

        public async Task<(bool Ok, string? PublicKey, string Error)> ValidateAsync(
            HttpRequest request,
            string requestBody,
            bool requireRegisteredUser = true)
        {
            if (!request.Headers.TryGetValue("X-User-PubKey", out var pkHeader) ||
                !request.Headers.TryGetValue("X-Timestamp", out var tsHeader) ||
                !request.Headers.TryGetValue("X-Signature", out var sigHeader))
            {
                return (false, null, "Missing auth headers");
            }

            var publicKey = pkHeader.FirstOrDefault();
            var timestamp = tsHeader.FirstOrDefault();
            var signature = sigHeader.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(publicKey) ||
                string.IsNullOrWhiteSpace(timestamp) ||
                string.IsNullOrWhiteSpace(signature))
            {
                return (false, null, "Empty auth headers");
            }

            if (!_keyService.ValidatePublicKey(publicKey))
                return (false, null, "Invalid public key");

            if (!long.TryParse(timestamp, out var tsLong))
                return (false, null, "Invalid timestamp");

            var ageMinutes = Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(tsLong)).TotalMinutes);
            if (ageMinutes > MaxClockSkew.TotalMinutes)
                return (false, null, "Timestamp expired");

            if (requireRegisteredUser)
            {
                var registered = await _storage.IsPublicKeyRegisteredAsync(publicKey);
                if (!registered)
                    return (false, null, "Public key not registered");
            }

            var bodyHash = "";
            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.Method, "PUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.Method, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(requestBody ?? ""));
                bodyHash = Convert.ToHexString(hash).ToLowerInvariant();
            }

            var dataToSign = $"{request.Method}{request.Path}{request.QueryString}{timestamp}{bodyHash}";
            if (!_keyService.VerifySignature(publicKey, dataToSign, signature))
                return (false, null, "Invalid signature");

            if (IsStateChanging(request.Method) && !TryConsumeSignature(publicKey, dataToSign))
                return (false, null, "Request already used (replayed signature)");

            return (true, publicKey, "");
        }

        /// <summary>
        /// Auth for Central-only ops: X-Timestamp + X-Signature over the same message format as
        /// ValidateAsync, verified against KeyService.GetCentralPublicKey().
        /// Optional X-User-PubKey / X-Central-PubKey must match the central public key when present.
        /// </summary>
        public (bool Ok, string Error) ValidateCentral(HttpRequest request, string requestBody)
        {
            var (ok, err, dataToSign, _) = TryBuildSignedMessage(request, requestBody);
            if (!ok) return (false, err);

            if (!request.Headers.TryGetValue("X-Signature", out var sigHeader) ||
                string.IsNullOrWhiteSpace(sigHeader.FirstOrDefault()))
                return (false, "Missing central auth headers (X-Timestamp, X-Signature)");

            var centralPk = _keyService.GetCentralPublicKey();
            var claimedPk = request.Headers.TryGetValue("X-Central-PubKey", out var cPk)
                ? cPk.FirstOrDefault()
                : request.Headers.TryGetValue("X-User-PubKey", out var uPk)
                    ? uPk.FirstOrDefault()
                    : null;
            if (!string.IsNullOrWhiteSpace(claimedPk) &&
                !string.Equals(NormalizePubKey(claimedPk), NormalizePubKey(centralPk), StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Public key is not Central");
            }

            if (!_keyService.VerifySignature(centralPk, dataToSign!, sigHeader.First()!))
                return (false, "Invalid Central signature");
            if (IsStateChanging(request.Method) && !TryConsumeSignature(centralPk, dataToSign!))
                return (false, "Request already used (replayed signature)");

            return (true, "");
        }

        /// <summary>
        /// Dual control: Central + Super Admin must both sign the same message.
        /// Headers: X-Timestamp, X-Signature (Central), X-SuperAdmin-Signature.
        /// </summary>
        public (bool Ok, string Error) ValidateCentralAndSuperAdmin(HttpRequest request, string requestBody)
        {
            var (ok, err, dataToSign, _) = TryBuildSignedMessage(request, requestBody);
            if (!ok) return (false, err);

            if (!request.Headers.TryGetValue("X-Signature", out var centralSigHeader) ||
                string.IsNullOrWhiteSpace(centralSigHeader.FirstOrDefault()))
                return (false, "Missing X-Signature (Central)");

            if (!request.Headers.TryGetValue("X-SuperAdmin-Signature", out var saSigHeader) ||
                string.IsNullOrWhiteSpace(saSigHeader.FirstOrDefault()))
                return (false, "Missing X-SuperAdmin-Signature");

            var centralPk = _keyService.GetCentralPublicKey();
            var superPk = _keyService.GetSuperAdminPublicKey();

            if (!_keyService.VerifySignature(centralPk, dataToSign!, centralSigHeader.First()!))
                return (false, "Invalid Central signature");
            if (!_keyService.VerifySignature(superPk, dataToSign!, saSigHeader.First()!))
                return (false, "Invalid Super Admin signature");
            if (IsStateChanging(request.Method) && !TryConsumeSignature("central+superadmin:" + superPk, dataToSign!))
                return (false, "Request already used (replayed signature)");

            return (true, "");
        }

        /// <summary>
        /// Verify Super Admin signature over an explicit path (e.g. admin approve signs the complete path).
        /// Headers: X-Timestamp, X-SuperAdmin-Signature.
        /// </summary>
        public (bool Ok, string Error, string? Message, string? Timestamp) ValidateSuperAdminOverPath(
            HttpRequest request,
            string requestBody,
            string method,
            string pathAndQuery)
        {
            if (!request.Headers.TryGetValue("X-Timestamp", out var tsHeader) ||
                string.IsNullOrWhiteSpace(tsHeader.FirstOrDefault()))
                return (false, "Missing X-Timestamp", null, null);

            if (!request.Headers.TryGetValue("X-SuperAdmin-Signature", out var saSigHeader) ||
                string.IsNullOrWhiteSpace(saSigHeader.FirstOrDefault()))
                return (false, "Missing X-SuperAdmin-Signature", null, null);

            var timestamp = tsHeader.First()!;
            if (!long.TryParse(timestamp, out var tsLong))
                return (false, "Invalid timestamp", null, null);

            var ageMinutes = Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(tsLong)).TotalMinutes);
            if (ageMinutes > MaxClockSkew.TotalMinutes)
                return (false, "Timestamp expired", null, null);

            var message = BuildSignedMessage(method, pathAndQuery, timestamp, requestBody);
            var superPk = _keyService.GetSuperAdminPublicKey();
            if (!_keyService.VerifySignature(superPk, message, saSigHeader.First()!))
                return (false, "Invalid Super Admin signature", null, null);

            return (true, "", message, timestamp);
        }

        private static (bool Ok, string Error, string? DataToSign, string? Timestamp) TryBuildSignedMessage(
            HttpRequest request,
            string requestBody)
        {
            if (!request.Headers.TryGetValue("X-Timestamp", out var tsHeader) ||
                string.IsNullOrWhiteSpace(tsHeader.FirstOrDefault()))
                return (false, "Missing X-Timestamp", null, null);

            var timestamp = tsHeader.First();
            if (!long.TryParse(timestamp, out var tsLong))
                return (false, "Invalid timestamp", null, null);

            var ageMinutes = Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(tsLong)).TotalMinutes);
            if (ageMinutes > MaxClockSkew.TotalMinutes)
                return (false, "Timestamp expired", null, null);

            var dataToSign = BuildSignedMessage(
                request.Method,
                $"{request.Path}{request.QueryString}",
                timestamp!,
                requestBody);
            return (true, "", dataToSign, timestamp);
        }

        /// <summary>
        /// Super Admin request signature (X-Timestamp + X-SuperAdmin-Request-Signature over
        /// METHOD + path+query + timestamp + sha256hex(body)); a state-changing request is accepted once.
        /// The private key never reaches the server: requests are signed on the Super Admin's machine.
        /// </summary>
        public (bool Ok, string Error) ValidateSuperAdminRequest(HttpRequest request, string requestBody)
        {
            if (!request.Headers.TryGetValue("X-SuperAdmin-Request-Signature", out var sig) ||
                !request.Headers.TryGetValue("X-Timestamp", out var tsHeader) ||
                !long.TryParse(tsHeader.FirstOrDefault(), out var ts))
                return (false, "Super Admin request signature required");
            if ((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > MaxClockSkew)
                return (false, "Admin timestamp expired");
            var message = BuildSignedMessage(request.Method, $"{request.Path}{request.QueryString}", tsHeader.First()!, requestBody);
            var superPk = _keyService.GetSuperAdminPublicKey();
            if (string.IsNullOrWhiteSpace(superPk) || !_keyService.VerifySignature(superPk, message, sig.FirstOrDefault() ?? ""))
                return (false, "Invalid Super Admin request signature");
            if (!HttpMethods.IsGet(request.Method) && !TryConsumeSignature("superadmin:" + superPk, message))
                return (false, "Request already used (replayed signature)");
            return (true, "");
        }

        public static string BuildSignedMessage(string method, string pathAndQuery, string timestamp, string requestBody)
        {
            var bodyHash = "";
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(requestBody ?? ""));
                bodyHash = Convert.ToHexString(hash).ToLowerInvariant();
            }
            return $"{method}{pathAndQuery}{timestamp}{bodyHash}";
        }

        public static string NormalizePubKey(string publicKey)
        {
            try
            {
                if (publicKey.Length == 66 && (publicKey.StartsWith("02") || publicKey.StartsWith("03")))
                    return new PubKey(publicKey).Decompress().ToHex();
                return publicKey;
            }
            catch
            {
                return publicKey;
            }
        }
    }
}

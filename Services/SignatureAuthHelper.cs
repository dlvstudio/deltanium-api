using System;
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
            if (ageMinutes > 5)
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

            return (true, publicKey, "");
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

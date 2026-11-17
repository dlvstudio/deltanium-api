using System;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// PRE proxy tokens are issued by Central (fetch-rekey, mode=proxy) and later redeemed by a Store
    /// (validate-token). They used to be bare base64 JSON, so anyone able to call validate-token could forge
    /// one for any follower/author pair. Now: "{base64(json)}.{Central decision-key signature}".
    /// </summary>
    public static class PreTokenSigner
    {
        private static string Message(string payloadB64) => "DELTANIUM-PRE-TOKEN\n" + payloadB64;

        public static string Issue(string payloadB64, KeyService keys) =>
            payloadB64 + "." + keys.SignDecision(Message(payloadB64));

        /// <summary>The signed payload (base64 JSON), or null when the token is unsigned or not Central's.</summary>
        public static string? Verify(string token, KeyService keys)
        {
            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1) return null;
            var payload = token[..dot];
            var sig = token[(dot + 1)..];
            return keys.VerifySignature(keys.GetCentralPublicKey(), Message(payload), sig) ? payload : null;
        }
    }
}

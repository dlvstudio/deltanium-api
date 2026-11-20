using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DeltaniumApi.Services.Dltp
{
    /// <summary>
    /// Node identity on a DLTP connection to an IP address (no domain, so no CA certificate).
    ///
    /// The node serves TLS with a self-signed certificate and, asked with a HELLO frame
    /// ({"id":0,"method":"HELLO","path":"/"}), answers with headers
    ///   X-Node-PubKey:  its registered secp256k1 public key (hex)
    ///   X-Node-Proof:   base64 DER signature by that key over Scope("DLTP-ID|" + sha256hex(certificate DER))
    /// The client hashes the certificate it actually received in the TLS handshake, so the proof only checks
    /// out when the node that holds the registered key also holds that TLS certificate: nobody in between can
    /// relay it.
    /// </summary>
    public static class DltpIdentity
    {
        public const string HelloMethod = "HELLO";

        public static string CertHash(X509Certificate cert) =>
            Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();

        public static string ProofMessage(string certSha256Hex) =>
            NetworkScope.Scope("DLTP-ID|" + certSha256Hex.ToLowerInvariant());

        public static bool Verify(string expectedPublicKey, string? presentedPublicKey, string? proofBase64, string certSha256Hex)
        {
            if (string.IsNullOrWhiteSpace(presentedPublicKey) || string.IsNullOrWhiteSpace(proofBase64)) return false;
            if (!string.Equals(SignatureAuthHelper.NormalizePubKey(expectedPublicKey.Trim()),
                               SignatureAuthHelper.NormalizePubKey(presentedPublicKey.Trim()), StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                var pub = new NBitcoin.PubKey(expectedPublicKey.Trim());
                var hash = new NBitcoin.uint256(SHA256.HashData(Encoding.UTF8.GetBytes(ProofMessage(certSha256Hex))));
                return pub.Verify(hash, Convert.FromBase64String(proofBase64));
            }
            catch { return false; }
        }
    }
}

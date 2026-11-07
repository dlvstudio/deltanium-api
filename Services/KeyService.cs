using NBitcoin;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeltaniumApi.Services
{
    public class KeyService
    {
        /// <summary>
        /// Local untracked file (gitignored). Prefer env CENTRAL_PRIVATE_KEY / CENTRAL_PUBLIC_KEY.
        /// </summary>
        private static readonly string CentralKeyFile =
            Environment.GetEnvironmentVariable("CENTRAL_KEYS_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".deltanium",
                "central_keys.json");

        public (string mnemonic, string publicKey) GenerateKeyPair()
        {
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var masterKey = mnemonic.DeriveExtKey();
            var publicKey = masterKey.PrivateKey.PubKey.ToString();
            return (mnemonic.ToString(), publicKey);
        }

        public bool ValidatePublicKey(string publicKey)
        {
            try
            {
                _ = new PubKey(publicKey);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool VerifySignature(string publicKey, string message, string signature)
        {
            try
            {
                var pubKey = new PubKey(publicKey);
                using var sha256 = SHA256.Create();
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(message));
                var signatureBytes = Convert.FromBase64String(signature);
                var hashLE = new uint256(hashBytes);
                var hashBE = new uint256(hashBytes, lendian: false);
                var resultLE = pubKey.Verify(hashLE, signatureBytes);
                var resultBE = pubKey.Verify(hashBE, signatureBytes);
                var ecdsaSig = NBitcoin.Crypto.ECDSASignature.FromDER(signatureBytes).MakeCanonical();
                var resultLE2 = pubKey.Verify(hashLE, ecdsaSig);
                var resultBE2 = pubKey.Verify(hashBE, ecdsaSig);
                return resultLE || resultBE || resultLE2 || resultBE2;
            }
            catch
            {
                return false;
            }
        }

        public string GetCentralPublicKey()
        {
            var pair = GetOrCreateCentralKeyPair();
            return pair.publicKey;
        }

        public string SignDecision(string message)
        {
            var pair = GetOrCreateCentralKeyPair();
            var key = new Key(Convert.FromHexString(pair.privateKey));
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(message));
            var sig = key.Sign(new uint256(hashBytes));
            return Convert.ToBase64String(sig.ToDER());
        }

        private (string publicKey, string privateKey) GetOrCreateCentralKeyPair()
        {
            var envPub = Environment.GetEnvironmentVariable("CENTRAL_PUBLIC_KEY");
            var envPriv = Environment.GetEnvironmentVariable("CENTRAL_PRIVATE_KEY");
            if (!string.IsNullOrWhiteSpace(envPub) && !string.IsNullOrWhiteSpace(envPriv))
                return (envPub.Trim(), envPriv.Trim());

            try
            {
                if (File.Exists(CentralKeyFile))
                {
                    var json = File.ReadAllText(CentralKeyFile);
                    var info = JsonSerializer.Deserialize<CentralKeyInfo>(json);
                    if (info != null && !string.IsNullOrWhiteSpace(info.PrivateKey))
                        return (info.PublicKey, info.PrivateKey);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KeyService] Error reading {CentralKeyFile}: {ex.Message}");
            }

            // Dev-only fallback: write outside repo (gitignored path under home)
            var newKey = new Key();
            var newInfo = new CentralKeyInfo
            {
                PublicKey = newKey.PubKey.ToHex(),
                PrivateKey = newKey.ToHex()
            };
            var dir = Path.GetDirectoryName(CentralKeyFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(CentralKeyFile, JsonSerializer.Serialize(newInfo, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[KeyService] Generated new central keys at {CentralKeyFile} (not for production)");
            return (newInfo.PublicKey, newInfo.PrivateKey);
        }

        private class CentralKeyInfo
        {
            public string PublicKey { get; set; } = string.Empty;
            public string PrivateKey { get; set; } = string.Empty;
        }
    }
}

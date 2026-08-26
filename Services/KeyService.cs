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
        private const string CentralKeyFile = "central_keys.json";

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
                var key = new PubKey(publicKey);
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
                Console.WriteLine($"[KeyService.VerifySignature] publicKey: {publicKey} (len={publicKey.Length})");
                Console.WriteLine($"[KeyService.VerifySignature] message: '{message}' (len={message.Length})");
                Console.WriteLine($"[KeyService.VerifySignature] signature (base64): {signature} (len={signature.Length})");
                
                var pubKey = new PubKey(publicKey);
                Console.WriteLine($"[KeyService.VerifySignature] PubKey parsed OK, compressed: {pubKey.IsCompressed}");
                
                // Create SHA256 hash of the message
                using var sha256 = SHA256.Create();
                var messageBytes = Encoding.UTF8.GetBytes(message);
                var hashBytes = sha256.ComputeHash(messageBytes);
                Console.WriteLine($"[KeyService.VerifySignature] messageHash (hex): {Convert.ToHexString(hashBytes).ToLower()}");
                
                // Convert signature from base64 to bytes
                var signatureBytes = Convert.FromBase64String(signature);
                Console.WriteLine($"[KeyService.VerifySignature] signatureBytes len: {signatureBytes.Length}");
                Console.WriteLine($"[KeyService.VerifySignature] signatureBytes (hex): {Convert.ToHexString(signatureBytes).ToLower()}");
                
                // Build both endian variants to ensure compatibility
                var hashLE = new uint256(hashBytes); // treat input as little-endian
                var hashBE = new uint256(hashBytes, lendian: false); // treat input as big-endian

                // Verify with DER directly (core will parse)
                var resultLE = pubKey.Verify(hashLE, signatureBytes);
                var resultBE = pubKey.Verify(hashBE, signatureBytes);
                Console.WriteLine($"[KeyService.VerifySignature] Verify(hashLE, DER): {resultLE}");
                Console.WriteLine($"[KeyService.VerifySignature] Verify(hashBE, DER): {resultBE}");

                // Also verify using parsed signature explicitly, in case parsing differences occur
                var ecdsaSig = NBitcoin.Crypto.ECDSASignature.FromDER(signatureBytes).MakeCanonical();
                var resultLE2 = pubKey.Verify(hashLE, ecdsaSig);
                var resultBE2 = pubKey.Verify(hashBE, ecdsaSig);
                Console.WriteLine($"[KeyService.VerifySignature] Verify(hashLE, ECDSA): {resultLE2}");
                Console.WriteLine($"[KeyService.VerifySignature] Verify(hashBE, ECDSA): {resultBE2}");

                var finalResult = resultLE || resultBE || resultLE2 || resultBE2;
                Console.WriteLine($"[KeyService.VerifySignature] Verification result: {finalResult}");
                return finalResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KeyService.VerifySignature] Exception: {ex.Message}");
                Console.WriteLine($"[KeyService.VerifySignature] StackTrace: {ex.StackTrace}");
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
            var der = sig.ToDER();
            return Convert.ToBase64String(der);
        }

        private (string publicKey, string privateKey) GetOrCreateCentralKeyPair()
        {
            try
            {
                if (File.Exists(CentralKeyFile))
                {
                    var json = File.ReadAllText(CentralKeyFile);
                    var info = JsonSerializer.Deserialize<CentralKeyInfo>(json);
                    if (info != null && !string.IsNullOrWhiteSpace(info.PrivateKey))
                    {
                        return (info.PublicKey, info.PrivateKey);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KeyService] Error reading {CentralKeyFile}: {ex.Message}");
            }

            var newKey = new Key();
            var newInfo = new CentralKeyInfo
            {
                PublicKey = newKey.PubKey.ToHex(),
                PrivateKey = newKey.ToHex()
            };
            File.WriteAllText(CentralKeyFile, JsonSerializer.Serialize(newInfo, new JsonSerializerOptions { WriteIndented = true }));
            return (newInfo.PublicKey, newInfo.PrivateKey);
        }

        private class CentralKeyInfo
        {
            public string PublicKey { get; set; } = string.Empty;
            public string PrivateKey { get; set; } = string.Empty;
        }
    }
} 
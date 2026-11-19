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

        /// <summary>
        /// Super Admin: prefer env SUPER_ADMIN_PUBLIC_KEY (+ optional SUPER_ADMIN_PRIVATE_KEY for local signing tools).
        /// Production should store only the public key on the API host; private key stays with the human Super Admin.
        /// </summary>
        private static readonly string SuperAdminKeyFile =
            Environment.GetEnvironmentVariable("SUPER_ADMIN_KEYS_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".deltanium",
                "super_admin_keys.json");

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
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(NetworkScope.Scope(message)));
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
            return SignWithPrivateHex(pair.privateKey, message);
        }

        public string GetSuperAdminPublicKey()
        {
            var pair = LoadSuperAdminKeyPair();
            return pair.publicKey;
        }

        /// <summary>
        /// True when this process can sign as Super Admin (dev / Super Admin workstation only).
        /// Production API should have public key only → returns false.
        /// </summary>
        public bool CanSignAsSuperAdmin()
        {
            var pair = LoadSuperAdminKeyPair();
            return !string.IsNullOrWhiteSpace(pair.privateKey);
        }

        public string SignAsSuperAdmin(string message)
        {
            var pair = LoadSuperAdminKeyPair();
            if (string.IsNullOrWhiteSpace(pair.privateKey))
                throw new InvalidOperationException(
                    "Super Admin private key is not available on this host (expected for production).");
            return SignWithPrivateHex(pair.privateKey, message);
        }

        /// <summary>
        /// Explicit one-shot generate for Scripts / <c>dotnet run -- --generate-super-admin-keys</c>.
        /// Writes JSON + PDF backup. Does not run during normal API startup.
        /// </summary>
        public static (string JsonPath, string PdfPath) GenerateSuperAdminKeysFile(
            string? outputPath = null, bool overwrite = false)
        {
            var path = string.IsNullOrWhiteSpace(outputPath) ? SuperAdminKeyFile : outputPath;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                path += ".json";
            var pdfPath = Path.ChangeExtension(path, ".pdf");

            if ((File.Exists(path) || File.Exists(pdfPath)) && !overwrite)
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing {path} / {pdfPath}. Pass --overwrite or delete the files.");

            var newKey = new Key();
            var info = new KeyPairInfo
            {
                PublicKey = newKey.PubKey.ToHex(),
                PrivateKey = newKey.ToHex()
            };
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
            SuperAdminKeyPdf.Write(pdfPath, info.PublicKey, info.PrivateKey);
            return (path, pdfPath);
        }

        private static string SignWithPrivateHex(string privateKeyHex, string message)
        {
            var key = new Key(Convert.FromHexString(privateKeyHex));
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(NetworkScope.Scope(message)));
            var sig = key.Sign(new uint256(hashBytes));
            return Convert.ToBase64String(sig.ToDER());
        }

        private static readonly object CentralKeyLock = new();
        private static (string publicKey, string privateKey)? _centralKeyPair;

        /// <summary>
        /// Loaded (or, without env/file, generated) once per process under a lock. Re-reading on every call let
        /// two concurrent first calls each generate and write a different key, so a signature made with one
        /// failed to verify against the public key read back a moment later.
        /// </summary>
        private (string publicKey, string privateKey) GetOrCreateCentralKeyPair()
        {
            if (_centralKeyPair is { } cached) return cached;
            lock (CentralKeyLock)
            {
                _centralKeyPair ??= LoadOrCreateKeyPair(
                    "CENTRAL_PUBLIC_KEY",
                    "CENTRAL_PRIVATE_KEY",
                    CentralKeyFile,
                    "central");
                return _centralKeyPair.Value;
            }
        }

        /// <summary>
        /// Load only — never auto-creates. Generate via
        /// <c>dotnet run --project deltanium-api -- --generate-super-admin-keys</c>
        /// or Scripts/Generate-SuperAdminKeys.ps1.
        /// </summary>
        private (string publicKey, string privateKey) LoadSuperAdminKeyPair()
        {
            var envPub = Environment.GetEnvironmentVariable("SUPER_ADMIN_PUBLIC_KEY");
            var envPriv = Environment.GetEnvironmentVariable("SUPER_ADMIN_PRIVATE_KEY");

            // Production shape: public key only on API host
            if (!string.IsNullOrWhiteSpace(envPub) && string.IsNullOrWhiteSpace(envPriv))
                return (envPub.Trim(), "");

            if (!string.IsNullOrWhiteSpace(envPub) && !string.IsNullOrWhiteSpace(envPriv))
                return (envPub.Trim(), envPriv.Trim());

            try
            {
                if (File.Exists(SuperAdminKeyFile))
                {
                    var json = File.ReadAllText(SuperAdminKeyFile);
                    var info = JsonSerializer.Deserialize<KeyPairInfo>(json);
                    if (info != null && !string.IsNullOrWhiteSpace(info.PublicKey))
                        return (info.PublicKey, info.PrivateKey ?? "");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KeyService] Error reading {SuperAdminKeyFile}: {ex.Message}");
            }

            throw new InvalidOperationException(
                "Super Admin public key not configured. On your laptop run: " +
                "dotnet run --project deltanium-api -- --generate-super-admin-keys " +
                $"(writes {SuperAdminKeyFile}). Then set SUPER_ADMIN_PUBLIC_KEY on API + blockers; keep PrivateKey offline.");
        }

        private static (string publicKey, string privateKey) LoadOrCreateKeyPair(
            string envPubName,
            string envPrivName,
            string filePath,
            string label)
        {
            var envPub = Environment.GetEnvironmentVariable(envPubName);
            var envPriv = Environment.GetEnvironmentVariable(envPrivName);
            if (!string.IsNullOrWhiteSpace(envPub) && !string.IsNullOrWhiteSpace(envPriv))
                return (envPub.Trim(), envPriv.Trim());

            try
            {
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    var info = JsonSerializer.Deserialize<KeyPairInfo>(json);
                    if (info != null && !string.IsNullOrWhiteSpace(info.PrivateKey))
                        return (info.PublicKey, info.PrivateKey);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KeyService] Error reading {filePath}: {ex.Message}");
            }

            var newKey = new Key();
            var newInfo = new KeyPairInfo
            {
                PublicKey = newKey.PubKey.ToHex(),
                PrivateKey = newKey.ToHex()
            };
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(newInfo, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[KeyService] Generated new {label} keys at {filePath} (not for production)");
            return (newInfo.PublicKey, newInfo.PrivateKey);
        }

        private class KeyPairInfo
        {
            public string PublicKey { get; set; } = string.Empty;
            public string PrivateKey { get; set; } = string.Empty;
        }
    }
}

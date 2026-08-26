using System;
using System.Collections.Generic;
using System.Text;
using DeltaniumApi.Models;
using DeltaniumApi.Models.Requests;
using NBitcoin;

namespace deltanium_api.Tests.Helpers
{
    public static class TestDataBuilder
    {
        /// <summary>
        /// Creates a valid user for testing
        /// </summary>
        public static User CreateUser(
            string? publicKey = null,
            string? email = null,
            string? fullName = null,
            DateTime? dateOfBirth = null,
            string? signature = null,
            long? timestamp = null)
        {
            var (_, pk) = GenerateKeyPair();
            return new User
            {
                Id = Guid.NewGuid().ToString(),
                PublicKey = publicKey ?? pk,
                Email = email ?? $"test{Guid.NewGuid():N}@example.com",
                FullName = fullName ?? "Test User",
                DateOfBirth = dateOfBirth ?? new DateTime(1990, 1, 1),
                Bio = "Test bio",
                AvatarUrl = "https://example.com/avatar.jpg",
                Signature = signature ?? "test_signature",
                RegistrationTimestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a valid SignedRegisterRequest for testing
        /// </summary>
        public static SignedRegisterRequest CreateRegisterRequest(
            string? publicKey = null,
            string? email = null,
            string? fullName = null,
            DateTime? dateOfBirth = null,
            string? signature = null,
            long? timestamp = null)
        {
            var (_, pk) = GenerateKeyPair();
            var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var dob = dateOfBirth ?? new DateTime(1990, 1, 1);
            var dobString = dob.ToString("yyyy-MM-dd");

            return new SignedRegisterRequest
            {
                PublicKey = publicKey ?? pk,
                Email = email ?? $"test{Guid.NewGuid():N}@example.com",
                FullName = fullName ?? "Test User",
                DateOfBirth = dob,
                Bio = "Test bio",
                AvatarUrl = "https://example.com/avatar.jpg",
                Timestamp = ts,
                Signature = signature ?? "test_signature"
            };
        }

        /// <summary>
        /// Creates a valid StoreNode for testing
        /// </summary>
        public static StoreNode CreateStoreNode(
            string? publicKey = null,
            string? endpoint = null)
        {
            var (_, pk) = GenerateKeyPair();
            return new StoreNode
            {
                Id = Guid.NewGuid().ToString(),
                PublicKey = publicKey ?? pk,
                Endpoint = endpoint ?? "http://localhost:5000",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a valid BlockCreatorNode for testing
        /// </summary>
        public static BlockCreatorNode CreateBlockCreatorNode(
            string? publicKey = null,
            string? endpoint = null)
        {
            var (_, pk) = GenerateKeyPair();
            return new BlockCreatorNode
            {
                Id = Guid.NewGuid().ToString(),
                PublicKey = publicKey ?? pk,
                Endpoint = endpoint ?? "http://localhost:5001",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                StakeAmount = 1000000
            };
        }

        /// <summary>
        /// Creates a valid Voucher for testing
        /// </summary>
        public static Voucher CreateVoucher(
            string? code = null,
            bool used = false)
        {
            return new Voucher
            {
                Code = code ?? $"VOUCHER-{Guid.NewGuid():N}",
                Used = used
            };
        }

        /// <summary>
        /// Creates a valid UserFollow for testing
        /// </summary>
        public static UserFollow CreateUserFollow(
            string? followerPublicKey = null,
            string? followingPublicKey = null,
            string? signature = null,
            long? timestamp = null)
        {
            var (_, pk1) = GenerateKeyPair();
            var (_, pk2) = GenerateKeyPair();
            return new UserFollow
            {
                FollowerPublicKey = followerPublicKey ?? pk1,
                FollowingPublicKey = followingPublicKey ?? pk2,
                Signature = signature ?? "test_signature",
                Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a valid StoredFile for testing
        /// </summary>
        public static StoredFile CreateStoredFile(
            string? ownerPublicKey = null,
            string? storeNodeId = null,
            string? fileId = null)
        {
            var (_, pk) = GenerateKeyPair();
            return new StoredFile
            {
                Id = Guid.NewGuid().ToString(),
                OwnerPublicKey = ownerPublicKey ?? pk,
                StoreNodeId = storeNodeId ?? Guid.NewGuid().ToString(),
                FileId = fileId ?? Guid.NewGuid().ToString(),
                IsActive = true
            };
        }

        /// <summary>
        /// Creates a valid UserRekey for testing
        /// </summary>
        public static UserRekey CreateUserRekey(
            string? followingPubKey = null,
            string? followerPubKey = null,
            string? tag = null,
            string? scope = null,
            string? fileId = null,
            bool isActive = true)
        {
            var (_, followingKey) = GenerateKeyPair();
            var (_, followerKey) = GenerateKeyPair();
            return new UserRekey
            {
                FollowingPubKey = followingPubKey ?? followingKey,
                FollowerPubKey = followerPubKey ?? followerKey,
                Tag = tag ?? "followers:2025",
                Scope = scope ?? "tag",
                FileId = fileId,
                RkBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("test_rekey_data")),
                CreatedAt = DateTime.UtcNow,
                IsActive = isActive
            };
        }

        /// <summary>
        /// Generates a key pair for testing
        /// </summary>
        public static (string mnemonic, string publicKey) GenerateKeyPair()
        {
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var hdRoot = mnemonic.DeriveExtKey();
            var privateKey = hdRoot.PrivateKey;
            var publicKey = privateKey.PubKey;
            return (mnemonic.ToString(), publicKey.ToHex());
        }

        /// <summary>
        /// Creates a valid compressed public key (66 chars)
        /// </summary>
        public static string CreateCompressedPublicKey()
        {
            var (_, pk) = GenerateKeyPair();
            var pubKey = new PubKey(pk);
            return pubKey.ToString(); // Returns compressed format
        }

        /// <summary>
        /// Creates a valid uncompressed public key (130 chars)
        /// </summary>
        public static string CreateUncompressedPublicKey()
        {
            var (_, pk) = GenerateKeyPair();
            var pubKey = new PubKey(pk);
            return pubKey.ToHex(); // Returns uncompressed format
        }

        /// <summary>
        /// Creates an invalid public key for negative testing
        /// </summary>
        public static string CreateInvalidPublicKey()
        {
            return "invalid_public_key_" + Guid.NewGuid().ToString("N");
        }
    }
}


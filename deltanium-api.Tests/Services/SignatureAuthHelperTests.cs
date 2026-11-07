using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DeltaniumApi.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using NBitcoin;
using Xunit;

namespace deltanium_api.Tests.Services
{
    public class SignatureAuthHelperTests
    {
        private readonly KeyService _keyService = new();
        private readonly Mock<IStorageService> _storage = new();

        private SignatureAuthHelper CreateSut() => new(_keyService, _storage.Object);

        private static (Key key, string publicKey) CreateKey()
        {
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var key = mnemonic.DeriveExtKey().PrivateKey;
            return (key, key.PubKey.ToString());
        }

        private static string Sign(Key key, string message)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(message));
            var sig = key.Sign(new uint256(hash));
            return Convert.ToBase64String(sig.ToDER());
        }

        private static DefaultHttpContext CreateRequest(
            string method,
            string path,
            string publicKey,
            string timestamp,
            string signature,
            string body)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            ctx.Request.Headers["X-User-PubKey"] = publicKey;
            ctx.Request.Headers["X-Timestamp"] = timestamp;
            ctx.Request.Headers["X-Signature"] = signature;
            if (!string.IsNullOrEmpty(body))
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Request.Body = new MemoryStream(bytes);
                ctx.Request.ContentLength = bytes.Length;
            }
            return ctx;
        }

        [Fact]
        public async Task ValidateAsync_ValidSignedPost_ReturnsOk()
        {
            var (key, pk) = CreateKey();
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(pk)).ReturnsAsync(true);

            var body = "{\"fileId\":\"abc\"}";
            using var sha = SHA256.Create();
            var bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = $"POST/api/storedfile/register{ts}{bodyHash}";
            var sig = Sign(key, message);

            var ctx = CreateRequest("POST", "/api/storedfile/register", pk, ts, sig, body);
            var sut = CreateSut();

            var (ok, caller, err) = await sut.ValidateAsync(ctx.Request, body);

            ok.Should().BeTrue(err);
            caller.Should().Be(pk);
            err.Should().BeEmpty();
        }

        [Fact]
        public async Task ValidateAsync_MissingHeaders_ReturnsUnauthorized()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/tx/submit";
            var sut = CreateSut();

            var (ok, _, err) = await sut.ValidateAsync(ctx.Request, "{}");

            ok.Should().BeFalse();
            err.Should().Contain("Missing");
        }

        [Fact]
        public async Task ValidateAsync_ExpiredTimestamp_ReturnsError()
        {
            var (key, pk) = CreateKey();
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(pk)).ReturnsAsync(true);

            var body = "{}";
            using var sha = SHA256.Create();
            var bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
            var message = $"POST/api/tx/submit{ts}{bodyHash}";
            var sig = Sign(key, message);

            var ctx = CreateRequest("POST", "/api/tx/submit", pk, ts, sig, body);
            var sut = CreateSut();

            var (ok, _, err) = await sut.ValidateAsync(ctx.Request, body);

            ok.Should().BeFalse();
            err.Should().Contain("expired");
        }

        [Fact]
        public async Task ValidateAsync_UnregisteredUser_ReturnsError()
        {
            var (key, pk) = CreateKey();
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(pk)).ReturnsAsync(false);

            var body = "{}";
            using var sha = SHA256.Create();
            var bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = $"POST/api/policy/upload-rekey{ts}{bodyHash}";
            var sig = Sign(key, message);

            var ctx = CreateRequest("POST", "/api/policy/upload-rekey", pk, ts, sig, body);
            var sut = CreateSut();

            var (ok, _, err) = await sut.ValidateAsync(ctx.Request, body);

            ok.Should().BeFalse();
            err.Should().Contain("not registered");
        }

        [Fact]
        public async Task ValidateAsync_AllowUnregistered_WhenFlagFalse()
        {
            var (key, pk) = CreateKey();
            var body = "{}";
            using var sha = SHA256.Create();
            var bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = $"POST/api/tx/submit{ts}{bodyHash}";
            var sig = Sign(key, message);

            var ctx = CreateRequest("POST", "/api/tx/submit", pk, ts, sig, body);
            var sut = CreateSut();

            var (ok, caller, err) = await sut.ValidateAsync(ctx.Request, body, requireRegisteredUser: false);

            ok.Should().BeTrue(err);
            caller.Should().Be(pk);
            _storage.Verify(s => s.IsPublicKeyRegisteredAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void NormalizePubKey_Compressed_ExpandsConsistently()
        {
            var (_, pk) = CreateKey();
            var compressed = new PubKey(pk).Compress().ToString();
            var a = SignatureAuthHelper.NormalizePubKey(compressed);
            var b = SignatureAuthHelper.NormalizePubKey(a);
            a.Should().Be(b);
            a.Length.Should().BeGreaterThan(66);
        }
    }
}

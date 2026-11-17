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

        [Fact]
        public void ValidateCentral_ValidSignature_ReturnsOk()
        {
            var body = "{\"orderId\":\"abc\"}";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = SignatureAuthHelper.BuildSignedMessage("POST", "/api/wallet/topup/complete", ts, body);
            var sig = _keyService.SignDecision(message);

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/wallet/topup/complete";
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = sig;
            ctx.Request.Headers["X-Central-PubKey"] = _keyService.GetCentralPublicKey();

            var (ok, err) = CreateSut().ValidateCentral(ctx.Request, body);
            ok.Should().BeTrue(err);
        }

        [Fact]
        public void ValidateCentral_WrongKeyClaim_ReturnsFalse()
        {
            var (key, pk) = CreateKey();
            var body = "{\"orderId\":\"abc\"}";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = SignatureAuthHelper.BuildSignedMessage("POST", "/api/wallet/topup/complete", ts, body);
            var sig = Sign(key, message);

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/wallet/topup/complete";
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = sig;
            ctx.Request.Headers["X-Central-PubKey"] = pk;

            var (ok, err) = CreateSut().ValidateCentral(ctx.Request, body);
            ok.Should().BeFalse();
            err.Should().Contain("not Central");
        }

        private static KeyService CreateKeyServiceWithSuperAdmin(out Key saKey, out string saPub)
        {
            saKey = new Key();
            saPub = saKey.PubKey.ToHex();
            Environment.SetEnvironmentVariable("SUPER_ADMIN_PUBLIC_KEY", saPub);
            Environment.SetEnvironmentVariable("SUPER_ADMIN_PRIVATE_KEY", saKey.ToHex());
            return new KeyService();
        }

        [Fact]
        public void ValidateCentralAndSuperAdmin_BothValid_ReturnsOk()
        {
            var keys = CreateKeyServiceWithSuperAdmin(out var saKey, out _);
            keys.CanSignAsSuperAdmin().Should().BeTrue();

            var body = "{\"orderId\":\"abc\"}";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = SignatureAuthHelper.BuildSignedMessage("POST", "/api/wallet/topup/complete", ts, body);

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/wallet/topup/complete";
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = keys.SignDecision(message);
            ctx.Request.Headers["X-SuperAdmin-Signature"] = keys.SignAsSuperAdmin(message);

            var sut = new SignatureAuthHelper(keys, _storage.Object);
            var (ok, err) = sut.ValidateCentralAndSuperAdmin(ctx.Request, body);
            ok.Should().BeTrue(err);
        }

        [Fact]
        public void ValidateCentralAndSuperAdmin_MissingSuperAdmin_ReturnsFalse()
        {
            var keys = CreateKeyServiceWithSuperAdmin(out _, out _);
            var body = "{\"orderId\":\"abc\"}";
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = SignatureAuthHelper.BuildSignedMessage("POST", "/api/wallet/topup/complete", ts, body);

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/wallet/topup/complete";
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = keys.SignDecision(message);

            var sut = new SignatureAuthHelper(keys, _storage.Object);
            var (ok, err) = sut.ValidateCentralAndSuperAdmin(ctx.Request, body);
            ok.Should().BeFalse();
            err.Should().Contain("SuperAdmin");
        }

        [Fact]
        public async Task ValidateAsync_SameSignedPostTwice_SecondIsRejectedAsReplay()
        {
            var (key, pk) = CreateKey();
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(pk)).ReturnsAsync(true);
            var body = "{\"toPublicKey\":\"bob\",\"amount\":5}";
            var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var sig = Sign(key, $"POST/api/wallet/transfer{ts}{bodyHash}");
            var sut = CreateSut();

            var first = await sut.ValidateAsync(CreateRequest("POST", "/api/wallet/transfer", pk, ts, sig, body).Request, body);
            var replay = await sut.ValidateAsync(CreateRequest("POST", "/api/wallet/transfer", pk, ts, sig, body).Request, body);

            first.Ok.Should().BeTrue(first.Error);
            replay.Ok.Should().BeFalse();
            replay.Error.Should().Contain("replay");
        }

        [Fact]
        public async Task ValidateAsync_SignedGet_CanBeRepeated()
        {
            var (key, pk) = CreateKey();
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(pk)).ReturnsAsync(true);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var sig = Sign(key, $"GET/api/wallet/balance{ts}");
            var sut = CreateSut();

            (await sut.ValidateAsync(CreateRequest("GET", "/api/wallet/balance", pk, ts, sig, "").Request, "")).Ok.Should().BeTrue();
            (await sut.ValidateAsync(CreateRequest("GET", "/api/wallet/balance", pk, ts, sig, "").Request, "")).Ok.Should().BeTrue();
        }

        [Fact]
        public void Consumed_signature_is_forgotten_once_it_could_no_longer_pass_the_timestamp_check()
        {
            var t0 = DateTime.UtcNow.AddDays(1); // isolated from other tests' entries
            SignatureAuthHelper.TryConsumeSignature("pk-x", "msg-x", t0).Should().BeTrue();
            SignatureAuthHelper.TryConsumeSignature("pk-x", "msg-x", t0.AddMinutes(1)).Should().BeFalse();
            SignatureAuthHelper.TryConsumeSignature("pk-x", "msg-x", t0.AddMinutes(12)).Should().BeTrue();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumApi.Controllers;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using NBitcoin;
using Xunit;

namespace deltanium_api.Tests.Controllers
{
    public class PreTokenTests
    {
        private readonly KeyService _keys = new();
        private readonly Mock<IStorageService> _storage = new();
        private readonly Key _store = new();

        private static string Payload(string follower = "02bb", string following = "02aa") =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                followingPubKey = following, followerPublicKey = follower, tag = "followers", exp = DateTime.UtcNow.AddMinutes(5)
            })));

        [Fact]
        public void Central_signed_token_verifies_and_forged_or_tampered_tokens_do_not()
        {
            var payload = Payload();
            var token = PreTokenSigner.Issue(payload, _keys);
            PreTokenSigner.Verify(token, _keys).Should().Be(payload);

            PreTokenSigner.Verify(payload, _keys).Should().BeNull("an unsigned (old-style) token is refused");
            PreTokenSigner.Verify(Payload(follower: "02cc") + token[token.IndexOf('.')..], _keys).Should().BeNull("payload swapped");
            var other = new Key();
            var forgedSig = Convert.ToBase64String(other.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes("DELTANIUM-PRE-TOKEN\n" + payload)))).ToDER());
            PreTokenSigner.Verify(payload + "." + forgedSig, _keys).Should().BeNull("not Central's key");
        }

        private PolicyController Controller(string body, Key signer, string path = "/api/policy/validate-token", string? claimedPubKey = null)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var msg = SignatureAuthHelper.BuildSignedMessage("POST", path, ts, body);
            var sig = Convert.ToBase64String(signer.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope(msg))))).ToDER());
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = path;
            ctx.Request.Headers["X-User-PubKey"] = claimedPubKey ?? signer.PubKey.ToHex();
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = sig;
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var config = new ConfigurationBuilder().Build();
            return new PolicyController(_storage.Object, new SignatureAuthHelper(_keys, _storage.Object), config, null!, null!, _keys)
            {
                ControllerContext = new ControllerContext { HttpContext = ctx }
            };
        }

        private void StoreIsActive(bool active) =>
            _storage.Setup(s => s.GetActiveNodesAsync()).ReturnsAsync(active
                ? new List<StoreNode> { new() { Id = "s1", PublicKey = _store.PubKey.ToHex(), IsActive = true } }
                : new List<StoreNode>());

        private static int Status(IActionResult r) => r switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => 0
        };

        [Fact]
        public async Task Unregistered_signer_cannot_redeem_a_token()
        {
            StoreIsActive(false);
            var body = JsonSerializer.Serialize(new { token = PreTokenSigner.Issue(Payload(), _keys) });
            Status(await Controller(body, _store).ValidateToken()).Should().Be(403);
        }

        [Fact]
        public async Task Unsigned_request_is_refused()
        {
            StoreIsActive(true);
            var c = Controller("{}", _store);
            c.Request.Headers.Remove("X-Signature");
            Status(await c.ValidateToken()).Should().Be(401);
        }

        [Fact]
        public async Task Registered_store_with_unsigned_token_is_refused()
        {
            StoreIsActive(true);
            var body = JsonSerializer.Serialize(new { token = Payload() });
            Status(await Controller(body, _store).ValidateToken()).Should().Be(401);
        }

        [Fact]
        public async Task Registered_store_with_Central_token_gets_the_rekey()
        {
            StoreIsActive(true);
            _storage.Setup(s => s.GetRekeyAsync("02bb", "02aa", "followers"))
                .ReturnsAsync(new UserRekey { RkBase64 = "cms=", Scope = "s", Tag = "followers", IsActive = true });
            var body = JsonSerializer.Serialize(new { token = PreTokenSigner.Issue(Payload(), _keys) });
            var r = await Controller(body, _store).ValidateToken();
            Status(r).Should().Be(200);
            JsonSerializer.Serialize(((ObjectResult)r).Value).Should().Contain("cms=");
        }
    
        private readonly Key _follower = new();
        private readonly Key _author = new();

        private string FetchBody() => JsonSerializer.Serialize(new { followingPubKey = _author.PubKey.ToHex(), tag = "followers", mode = "client" });

        private void FollowerMayFetch()
        {
            var f = _follower.PubKey.ToHex();
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(f)).ReturnsAsync(true);
            _storage.Setup(s => s.IsFollowingAsync(f, _author.PubKey.ToHex())).ReturnsAsync(true);
            _storage.Setup(s => s.GetRekeyAsync(f, _author.PubKey.ToHex(), "followers"))
                .ReturnsAsync(new UserRekey { FollowerPubKey = f, FollowingPubKey = _author.PubKey.ToHex(), Tag = "followers", RkBase64 = "rk", IsActive = true });
        }

        [Fact]
        public async Task Follower_signed_fetch_rekey_returns_the_rekey()
        {
            FollowerMayFetch();
            var r = await Controller(FetchBody(), _follower, "/api/policy/fetch-rekey").FetchRekey();
            Status(r).Should().Be(200);
        }

        [Fact]
        public async Task Fetch_rekey_refuses_someone_else_claiming_the_followers_key()
        {
            FollowerMayFetch();
            var attacker = new Key();
            var r = await Controller(FetchBody(), attacker, "/api/policy/fetch-rekey", claimedPubKey: _follower.PubKey.ToHex()).FetchRekey();
            Status(r).Should().Be(401);
            _storage.Verify(s => s.GetRekeyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Fetch_rekey_refuses_a_header_without_signature()
        {
            FollowerMayFetch();
            var c = Controller(FetchBody(), _follower, "/api/policy/fetch-rekey");
            c.ControllerContext.HttpContext.Request.Headers.Remove("X-Signature");
            Status(await c.FetchRekey()).Should().Be(401);
        }
    }
}

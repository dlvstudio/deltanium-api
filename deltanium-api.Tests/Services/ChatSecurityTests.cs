using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DeltaniumApi.Controllers;
using DeltaniumApi.Data;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using deltanium_api.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Xunit;

namespace deltanium_api.Tests.Services
{
    public class ChatSecurityTests : IDisposable
    {
        private readonly ApiDbContext _db;
        private readonly MySqlStorageService _storage;
        private readonly KeyService _keys = new();
        private readonly ChatController _controller;
        private readonly string _alicePk;
        private readonly Key _aliceKey;
        private readonly string _bobPk;
        private readonly Key _bobKey;
        private readonly string _evePk;
        private readonly Key _eveKey;

        public ChatSecurityTests()
        {
            _db = InMemoryDb.CreateContext();
            _storage = new MySqlStorageService(_db);
            _controller = new ChatController(_keys, _storage, new SignatureAuthHelper(_keys, _storage));

            (_aliceKey, _alicePk) = CreateKey();
            (_bobKey, _bobPk) = CreateKey();
            (_eveKey, _evePk) = CreateKey();
        }

        public void Dispose() => _db.Dispose();

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
            return Convert.ToBase64String(key.Sign(new uint256(hash)).ToDER());
        }

        private async Task SeedUsersAsync()
        {
            await _storage.SaveUserAsync(TestDataBuilder.CreateUser(publicKey: _alicePk, email: "alice@it.test"));
            await _storage.SaveUserAsync(TestDataBuilder.CreateUser(publicKey: _bobPk, email: "bob@it.test"));
            await _storage.SaveUserAsync(TestDataBuilder.CreateUser(publicKey: _evePk, email: "eve@it.test"));
        }

        private void BindSignedRequest(string method, string path, Key key, string publicKey, string body)
        {
            using var sha = SHA256.Create();
            var bodyHash = "";
            if (method is "POST" or "PUT" or "PATCH")
                bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = $"{method}{path}{ts}{bodyHash}";
            var sig = Sign(key, message);

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            ctx.Request.Headers["X-User-PubKey"] = publicKey;
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = sig;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
            _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        }

        [Fact]
        public async Task GetMessages_NonParticipant_IsForbidden()
        {
            await SeedUsersAsync();
            var conv = await _storage.CreateOrGetConversationAsync(_alicePk, _bobPk);

            BindSignedRequest("GET", $"/api/chat/messages/{conv.ConversationId}", _eveKey, _evePk, "");
            var result = await _controller.GetMessages(conv.ConversationId);
            result.Should().BeOfType<ForbidResult>();
        }

        [Fact]
        public async Task RegisterMessage_NonParticipant_IsForbidden()
        {
            await SeedUsersAsync();
            var conv = await _storage.CreateOrGetConversationAsync(_alicePk, _bobPk);
            var body = $"{{\"conversationId\":\"{conv.ConversationId}\",\"recipientPubKey\":\"{_bobPk}\",\"storeNodeId\":\"n1\",\"blockId\":\"b1\",\"fileId\":\"f1\"}}";

            BindSignedRequest("POST", "/api/chat/message", _eveKey, _evePk, body);
            var result = await _controller.RegisterMessage();
            result.Should().BeOfType<ForbidResult>();
        }

        [Fact]
        public async Task RegisterMessage_Participant_Succeeds()
        {
            await SeedUsersAsync();
            var conv = await _storage.CreateOrGetConversationAsync(_alicePk, _bobPk);
            var body = $"{{\"conversationId\":\"{conv.ConversationId}\",\"recipientPubKey\":\"{_bobPk}\",\"storeNodeId\":\"n1\",\"blockId\":\"b1\",\"fileId\":\"f1\"}}";

            BindSignedRequest("POST", "/api/chat/message", _aliceKey, _alicePk, body);
            var result = await _controller.RegisterMessage();
            var ok = result.Should().BeOfType<OkObjectResult>().Subject;
            ok.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task GetConversationById_ReturnsConversation()
        {
            await SeedUsersAsync();
            var conv = await _storage.CreateOrGetConversationAsync(_alicePk, _bobPk);
            var loaded = await _storage.GetConversationByIdAsync(conv.ConversationId);
            loaded.Should().NotBeNull();
            loaded!.Participants.Should().HaveCount(2);
        }
    }
}

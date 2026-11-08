using System.IO;
using System.Security.Cryptography;
using System.Text;
using DeltaniumApi.Controllers;
using DeltaniumApi.Data;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using deltanium_api.Tests.Helpers;
using FluentAssertions;
using NBitcoin;
using Xunit;

namespace deltanium_api.Tests.Integration
{
    /// <summary>
    /// Real MySQL cases (remote host from User Secrets). Skips if secrets missing.
    /// </summary>
    public class RealMysqlStorageIntegrationTests : IDisposable
    {
        private readonly ApiDbContext? _db;
        private readonly MySqlStorageService? _sut;
        private readonly string _tag;

        public RealMysqlStorageIntegrationTests()
        {
            _db = RealMysql.CreateApiContextOrNull();
            if (_db != null)
                _sut = new MySqlStorageService(_db);
            _tag = $"it_{Guid.NewGuid():N}".Substring(0, 16);
        }

        public void Dispose()
        {
            if (_db == null) return;
            try
            {
                // Cleanup rows created by this run
                var users = _db.Users.Where(u => u.Email != null && u.Email.Contains(_tag)).ToList();
                var pks = users.Select(u => u.PublicKey).ToHashSet();
                _db.UserFollows.RemoveRange(_db.UserFollows.Where(f =>
                    pks.Contains(f.FollowerPublicKey) || pks.Contains(f.FollowingPublicKey)));
                _db.UserRekeys.RemoveRange(_db.UserRekeys.Where(r =>
                    pks.Contains(r.FollowerPubKey) || pks.Contains(r.FollowingPubKey)));
                _db.StoredFiles.RemoveRange(_db.StoredFiles.Where(f => pks.Contains(f.OwnerPublicKey)));
                _db.StoreNodes.RemoveRange(_db.StoreNodes.Where(n => n.Endpoint != null && n.Endpoint.Contains(_tag)));
                _db.Vouchers.RemoveRange(_db.Vouchers.Where(v => v.Code.StartsWith(_tag)));
                _db.Users.RemoveRange(users);
                _db.TransactionEnvelopes.RemoveRange(
                    _db.TransactionEnvelopes.Where(e => e.TransactionJson.Contains(_tag)));
                _db.SaveChanges();
            }
            catch
            {
                // best-effort cleanup
            }
            finally
            {
                _db.Dispose();
            }
        }

        private void RequireDb()
        {
            Assert.True(_db != null && _sut != null,
                "Missing MySQL connection string (User Secrets deltanium-api-local-secrets or DELTANIUM_MYSQL_CS)");
        }

        [Fact]
        public async Task RealDb_RegisterFollowRekey_AndPullTransactions()
        {
            RequireDb();

            var alice = TestDataBuilder.CreateUser(email: $"{_tag}_alice@example.com", fullName: "Alice IT");
            var bob = TestDataBuilder.CreateUser(email: $"{_tag}_bob@example.com", fullName: "Bob IT");

            await _sut!.SaveUserAsync(alice);
            await _sut.SaveUserAsync(bob);

            (await _sut.IsPublicKeyRegisteredAsync(alice.PublicKey)).Should().BeTrue();
            (await _sut.FollowUserAsync(alice.PublicKey, bob.PublicKey, "sig", DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                .Should().BeTrue();
            (await _sut.IsFollowingAsync(alice.PublicKey, bob.PublicKey)).Should().BeTrue();
            (await _sut.GetFollowersCountAsync(bob.PublicKey)).Should().Be(1);

            await _sut.SaveRekeyAsync(new UserRekey
            {
                FollowingPubKey = bob.PublicKey,
                FollowerPubKey = alice.PublicKey,
                Tag = "followers:it",
                Scope = "tag",
                FileId = "",
                RkBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(_tag)),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            (await _sut.GetRekeyAsync(alice.PublicKey, bob.PublicKey, "followers:it")).Should().NotBeNull();

            var voucherCode = $"{_tag}-VOUCHER";
            _db!.Vouchers.Add(new Voucher { Code = voucherCode, Used = false });
            await _db.SaveChangesAsync();
            (await _sut.IsVoucherValidAsync(voucherCode)).Should().BeTrue();
            await _sut.MarkVoucherUsedAsync(voucherCode);
            (await _sut.IsVoucherValidAsync(voucherCode)).Should().BeFalse();

            var node = TestDataBuilder.CreateStoreNode(endpoint: $"http://{_tag}.local:5001");
            await _sut.SaveNodeAsync(node);
            await _sut.SaveStoredFileAsync(new StoredFile
            {
                FileId = $"{_tag}-file",
                OwnerPublicKey = alice.PublicKey,
                StoreNodeId = node.Id,
                FileHash = "abc",
                FileSize = 42,
                IsActive = true
            });
            (await _sut.GetUserFilesOnNodeAsync(alice.PublicKey, node.Id)).Should().ContainSingle();

            await _sut.AddTransactionAsync(new BlockchainTransaction
            {
                Type = "UserRegistration",
                From = alice.PublicKey,
                Signature = "it-sig",
                Data = new Dictionary<string, object> { ["tag"] = _tag }
            });
            var pulled = await _sut.PullPendingTransactionsAsync(10);
            pulled.Should().Contain(t => t.From == alice.PublicKey);
            (await _sut.PullPendingTransactionsAsync(10)).Should().NotContain(t => t.From == alice.PublicKey);

            var conv = await _sut.CreateOrGetConversationAsync(alice.PublicKey, bob.PublicKey);
            await _sut.SaveChatMessageAsync(new ChatMessageMetadata
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ConversationId = conv.ConversationId,
                SenderPubKey = alice.PublicKey,
                RecipientPubKey = bob.PublicKey,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });
            (await _sut.GetUnreadCountForUserAsync(bob.PublicKey)).Should().BeGreaterThan(0);
            await _sut.MarkMessagesAsReadAsync(conv.ConversationId, bob.PublicKey);
            (await _sut.GetUnreadCountForUserAsync(bob.PublicKey)).Should().Be(0);

            // cleanup chat/conversation for this run
            _db.ChatMessages.RemoveRange(_db.ChatMessages.Where(m => m.ConversationId == conv.ConversationId));
            _db.Conversations.RemoveRange(_db.Conversations.Where(c => c.ConversationId == conv.ConversationId));
            await _db.SaveChangesAsync();
        }

        [Fact]
        public async Task RealDb_HttpRegister_WithValidSignature_RoundTrip()
        {
            RequireDb();

            // Also exercise crypto the same way the API does
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var key = mnemonic.DeriveExtKey().PrivateKey;
            var publicKey = key.PubKey.ToString();
            var email = $"{_tag}_http@example.com";
            var fullName = "Http IT";
            var dob = new DateTime(1991, 2, 3);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var data = $"{email}:{fullName}:{dob:yyyy-MM-dd}:{ts}";
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
            var sig = key.Sign(new uint256(hash));
            var signature = Convert.ToBase64String(sig.ToDER());

            var keyService = new KeyService();
            keyService.VerifySignature(publicKey, data, signature).Should().BeTrue();

            await _sut!.SaveUserAsync(new User
            {
                PublicKey = publicKey,
                Email = email,
                FullName = fullName,
                DateOfBirth = dob,
                Signature = signature,
                RegistrationTimestamp = ts,
                Bio = "it",
                AvatarUrl = ""
            });

            (await _sut.IsPublicKeyRegisteredAsync(publicKey)).Should().BeTrue();
            var loaded = (await _sut.GetUsersAsync()).Single(u => u.PublicKey == publicKey);
            loaded.Email.Should().Be(email);
            loaded.Signature.Should().Be(signature);
        }

        [Fact]
        public async Task RealDb_TryMarkVoucherUsed_IsAtomic()
        {
            RequireDb();

            var code = $"{_tag}-ATOMIC";
            _db!.Vouchers.Add(new Voucher { Code = code, Used = false });
            await _db.SaveChangesAsync();

            (await _sut!.TryMarkVoucherUsedAsync(code)).Should().BeTrue();
            (await _sut.TryMarkVoucherUsedAsync(code)).Should().BeFalse();
            (await _sut.IsVoucherValidAsync(code)).Should().BeFalse();
        }

        [Fact]
        public async Task RealDb_SaveStoredFilesBatch_RegistersNofMReplicas()
        {
            RequireDb();

            var owner = TestDataBuilder.CreateUser(email: $"{_tag}_owner@example.com");
            await _sut!.SaveUserAsync(owner);

            var nodeA = TestDataBuilder.CreateStoreNode(endpoint: $"http://{_tag}-a.local:5001");
            var nodeB = TestDataBuilder.CreateStoreNode(endpoint: $"http://{_tag}-b.local:5001");
            await _sut.SaveNodeAsync(nodeA);
            await _sut.SaveNodeAsync(nodeB);

            var fileId = $"{_tag}-replica-file";
            await _sut.SaveStoredFilesBatchAsync(new List<StoredFile>
            {
                new()
                {
                    FileId = fileId,
                    OwnerPublicKey = owner.PublicKey,
                    StoreNodeId = nodeA.Id,
                    FileHash = "hash-nofm",
                    FileSize = 99,
                    IsActive = true,
                    StoredAt = DateTime.UtcNow
                },
                new()
                {
                    FileId = fileId,
                    OwnerPublicKey = owner.PublicKey,
                    StoreNodeId = nodeB.Id,
                    FileHash = "hash-nofm",
                    FileSize = 99,
                    IsActive = true,
                    StoredAt = DateTime.UtcNow
                }
            });

            var replicas = (await _sut.GetStoredFilesAsync()).Where(f => f.FileId == fileId).ToList();
            replicas.Should().HaveCount(2);
            replicas.Select(f => f.StoreNodeId).Should().BeEquivalentTo(new[] { nodeA.Id, nodeB.Id });

            var nodes = await _sut.GetNodesStoringUserFilesAsync(owner.PublicKey);
            nodes.Select(n => n.Id).Should().BeEquivalentTo(new[] { nodeA.Id, nodeB.Id });
        }

        [Fact]
        public async Task RealDb_SignatureAuth_AgainstRegisteredUser()
        {
            RequireDb();

            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var key = mnemonic.DeriveExtKey().PrivateKey;
            var publicKey = key.PubKey.ToString();

            await _sut!.SaveUserAsync(new User
            {
                PublicKey = publicKey,
                Email = $"{_tag}_auth@example.com",
                FullName = "Auth IT",
                DateOfBirth = new DateTime(1990, 1, 1),
                Signature = "it",
                RegistrationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Bio = "it",
                AvatarUrl = ""
            });

            var body = "{\"fileId\":\"x\"}";
            using var sha = SHA256.Create();
            var bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var message = $"POST/api/storedfile/register-replicas{ts}{bodyHash}";
            var sig = Convert.ToBase64String(key.Sign(new uint256(sha.ComputeHash(Encoding.UTF8.GetBytes(message)))).ToDER());

            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/storedfile/register-replicas";
            ctx.Request.Headers["X-User-PubKey"] = publicKey;
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = sig;

            var auth = new SignatureAuthHelper(new KeyService(), _sut);
            var (ok, caller, err) = await auth.ValidateAsync(ctx.Request, body);

            ok.Should().BeTrue(err);
            caller.Should().Be(publicKey);
        }

        [Fact]
        public async Task RealDb_Chat_Idor_BlocksNonParticipant_AllowsParticipant()
        {
            RequireDb();

            var alice = TestDataBuilder.CreateUser(email: $"{_tag}_chat_a@example.com");
            var bob = TestDataBuilder.CreateUser(email: $"{_tag}_chat_b@example.com");
            var eve = TestDataBuilder.CreateUser(email: $"{_tag}_chat_e@example.com");
            await _sut!.SaveUserAsync(alice);
            await _sut.SaveUserAsync(bob);
            await _sut.SaveUserAsync(eve);

            var conv = await _sut.CreateOrGetConversationAsync(alice.PublicKey, bob.PublicKey);
            var byId = await _sut.GetConversationByIdAsync(conv.ConversationId);
            byId.Should().NotBeNull();
            byId!.Participants.Should().Contain(new[] { alice.PublicKey, bob.PublicKey });

            // Participant can save a message metadata row
            await _sut.SaveChatMessageAsync(new ChatMessageMetadata
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ConversationId = conv.ConversationId,
                SenderPubKey = alice.PublicKey,
                RecipientPubKey = bob.PublicKey,
                StoreNodeId = $"{_tag}-node",
                BlockId = $"{_tag}-block",
                FileId = $"{_tag}-file",
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });

            var forAlice = await _sut.GetConversationMessagesAsync(conv.ConversationId, 0, 10);
            forAlice.Should().Contain(m => m.SenderPubKey == alice.PublicKey);

            // HTTP-level IDOR: Eve signed request must be Forbid
            var eveMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            // Use eve's stored key? CreateUser generated its own key — sign with a fresh key matching eve.PublicKey is hard.
            // Instead exercise controller with SignatureAuthHelper against real DB registration of Eve.
            var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var eveKey = mnemonic.DeriveExtKey().PrivateKey;
            var evePk = eveKey.PubKey.ToString();
            await _sut.SaveUserAsync(new User
            {
                PublicKey = evePk,
                Email = $"{_tag}_chat_eve2@example.com",
                FullName = "Eve2",
                DateOfBirth = new DateTime(1990, 1, 1),
                Signature = "it",
                RegistrationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Bio = "it",
                AvatarUrl = ""
            });

            var body = $"{{\"conversationId\":\"{conv.ConversationId}\",\"recipientPubKey\":\"{bob.PublicKey}\",\"storeNodeId\":\"n\",\"blockId\":\"b\",\"fileId\":\"f\"}}";
            using var sha = SHA256.Create();
            var bodyHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var path = "/api/chat/message";
            var message = $"POST{path}{ts}{bodyHash}";
            var sig = Convert.ToBase64String(eveKey.Sign(new uint256(sha.ComputeHash(Encoding.UTF8.GetBytes(message)))).ToDER());

            var controller = new ChatController(
                new KeyService(), _sut, new SignatureAuthHelper(new KeyService(), _sut));
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = path;
            ctx.Request.Headers["X-User-PubKey"] = evePk;
            ctx.Request.Headers["X-Timestamp"] = ts;
            ctx.Request.Headers["X-Signature"] = sig;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };

            var forbidden = await controller.RegisterMessage();
            forbidden.Should().BeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();

            // Alice (participant) succeeds
            var aliceMnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve);
            var aliceKey = aliceMnemonic.DeriveExtKey().PrivateKey;
            var alicePk = aliceKey.PubKey.ToString();
            await _sut.SaveUserAsync(new User
            {
                PublicKey = alicePk,
                Email = $"{_tag}_chat_alice2@example.com",
                FullName = "Alice2",
                DateOfBirth = new DateTime(1990, 1, 1),
                Signature = "it",
                RegistrationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Bio = "it",
                AvatarUrl = ""
            });
            var conv2 = await _sut.CreateOrGetConversationAsync(alicePk, bob.PublicKey);
            var bodyOk = $"{{\"conversationId\":\"{conv2.ConversationId}\",\"recipientPubKey\":\"{bob.PublicKey}\",\"storeNodeId\":\"n\",\"blockId\":\"b\",\"fileId\":\"f2\"}}";
            var bodyHashOk = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(bodyOk))).ToLowerInvariant();
            var tsOk = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var msgOk = $"POST{path}{tsOk}{bodyHashOk}";
            var sigOk = Convert.ToBase64String(aliceKey.Sign(new uint256(sha.ComputeHash(Encoding.UTF8.GetBytes(msgOk)))).ToDER());

            var ctxOk = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctxOk.Request.Method = "POST";
            ctxOk.Request.Path = path;
            ctxOk.Request.Headers["X-User-PubKey"] = alicePk;
            ctxOk.Request.Headers["X-Timestamp"] = tsOk;
            ctxOk.Request.Headers["X-Signature"] = sigOk;
            var bytesOk = Encoding.UTF8.GetBytes(bodyOk);
            ctxOk.Request.Body = new MemoryStream(bytesOk);
            ctxOk.Request.ContentLength = bytesOk.Length;
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctxOk };

            var allowed = await controller.RegisterMessage();
            allowed.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();

            // cleanup chat rows for this run
            _db!.ChatMessages.RemoveRange(_db.ChatMessages.Where(m =>
                m.ConversationId == conv.ConversationId || m.ConversationId == conv2.ConversationId));
            _db.Conversations.RemoveRange(_db.Conversations.Where(c =>
                c.ConversationId == conv.ConversationId || c.ConversationId == conv2.ConversationId));
            await _db.SaveChangesAsync();
        }
    }
}

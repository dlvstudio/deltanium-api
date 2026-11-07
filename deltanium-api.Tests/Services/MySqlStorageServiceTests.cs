using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DeltaniumApi.Data;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using deltanium_api.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace deltanium_api.Tests.Services
{
    public class MySqlStorageServiceTests : IDisposable
    {
        private readonly ApiDbContext _db;
        private readonly MySqlStorageService _sut;

        public MySqlStorageServiceTests()
        {
            _db = InMemoryDb.CreateContext();
            _sut = new MySqlStorageService(_db);
        }

        public void Dispose() => _db.Dispose();

        [Fact]
        public async Task SaveUserAsync_ThenGetUsers_ReturnsUser()
        {
            var user = TestDataBuilder.CreateUser();

            await _sut.SaveUserAsync(user);

            var users = await _sut.GetUsersAsync();
            users.Should().ContainSingle(u => u.PublicKey == user.PublicKey);
            (await _sut.IsPublicKeyRegisteredAsync(user.PublicKey)).Should().BeTrue();
        }

        [Fact]
        public async Task UpdateUserAsync_UpdatesFields()
        {
            var user = TestDataBuilder.CreateUser(email: "old@example.com", fullName: "Old");
            await _sut.SaveUserAsync(user);

            user.Email = "new@example.com";
            user.FullName = "New Name";
            await _sut.UpdateUserAsync(user);

            var updated = (await _sut.GetUsersAsync()).Single(u => u.PublicKey == user.PublicKey);
            updated.Email.Should().Be("new@example.com");
            updated.FullName.Should().Be("New Name");
        }

        [Fact]
        public async Task FollowAndUnfollow_Works()
        {
            var a = TestDataBuilder.CreateUser();
            var b = TestDataBuilder.CreateUser();
            await _sut.SaveUserAsync(a);
            await _sut.SaveUserAsync(b);

            (await _sut.FollowUserAsync(a.PublicKey, b.PublicKey, "sig", 1)).Should().BeTrue();
            (await _sut.FollowUserAsync(a.PublicKey, b.PublicKey)).Should().BeFalse();
            (await _sut.IsFollowingAsync(a.PublicKey, b.PublicKey)).Should().BeTrue();
            (await _sut.GetFollowersCountAsync(b.PublicKey)).Should().Be(1);
            (await _sut.GetFollowingCountAsync(a.PublicKey)).Should().Be(1);

            (await _sut.UnfollowUserAsync(a.PublicKey, b.PublicKey)).Should().BeTrue();
            (await _sut.IsFollowingAsync(a.PublicKey, b.PublicKey)).Should().BeFalse();
        }

        [Fact]
        public async Task FollowUserAsync_Self_ReturnsFalse()
        {
            var user = TestDataBuilder.CreateUser();
            (await _sut.FollowUserAsync(user.PublicKey, user.PublicKey)).Should().BeFalse();
        }

        [Fact]
        public async Task Voucher_ValidThenMarkUsed()
        {
            _db.Vouchers.Add(new Voucher { Code = "TEST-CODE", Used = false });
            await _db.SaveChangesAsync();

            (await _sut.IsVoucherValidAsync("TEST-CODE")).Should().BeTrue();
            await _sut.MarkVoucherUsedAsync("TEST-CODE");
            (await _sut.IsVoucherValidAsync("TEST-CODE")).Should().BeFalse();
        }

        [Fact]
        public async Task TryMarkVoucherUsedAsync_FirstCallSucceeds_SecondFails()
        {
            var code = $"ATOMIC-{Guid.NewGuid():N}";
            _db.Vouchers.Add(new Voucher { Code = code, Used = false });
            await _db.SaveChangesAsync();

            (await _sut.TryMarkVoucherUsedAsync(code)).Should().BeTrue();
            (await _sut.TryMarkVoucherUsedAsync(code)).Should().BeFalse();
            (await _sut.IsVoucherValidAsync(code)).Should().BeFalse();
        }

        [Fact]
        public async Task TryMarkVoucherUsedAsync_UnknownCode_ReturnsFalse()
        {
            (await _sut.TryMarkVoucherUsedAsync("NO-SUCH-VOUCHER")).Should().BeFalse();
        }

        [Fact]
        public async Task SaveStoredFilesBatchAsync_RegistersReplicasForSameFileId()
        {
            var nodeA = TestDataBuilder.CreateStoreNode();
            var nodeB = TestDataBuilder.CreateStoreNode();
            await _sut.SaveNodeAsync(nodeA);
            await _sut.SaveNodeAsync(nodeB);
            var owner = TestDataBuilder.CreateCompressedPublicKey();
            var fileId = Guid.NewGuid().ToString("N");

            await _sut.SaveStoredFilesBatchAsync(new List<StoredFile>
            {
                new()
                {
                    FileId = fileId,
                    OwnerPublicKey = owner,
                    StoreNodeId = nodeA.Id,
                    FileHash = "h1",
                    FileSize = 42,
                    IsActive = true,
                    StoredAt = DateTime.UtcNow
                },
                new()
                {
                    FileId = fileId,
                    OwnerPublicKey = owner,
                    StoreNodeId = nodeB.Id,
                    FileHash = "h1",
                    FileSize = 42,
                    IsActive = true,
                    StoredAt = DateTime.UtcNow
                }
            });

            var files = await _sut.GetStoredFilesAsync();
            files.Where(f => f.FileId == fileId).Should().HaveCount(2);
            files.Where(f => f.FileId == fileId).Select(f => f.StoreNodeId)
                .Should().BeEquivalentTo(new[] { nodeA.Id, nodeB.Id });

            var nodes = await _sut.GetNodesStoringUserFilesAsync(owner);
            nodes.Select(n => n.Id).Should().BeEquivalentTo(new[] { nodeA.Id, nodeB.Id });
        }

        [Fact]
        public async Task SaveNode_AndGetActiveNodes()
        {
            var active = TestDataBuilder.CreateStoreNode();
            var inactive = TestDataBuilder.CreateStoreNode();
            inactive.IsActive = false;

            await _sut.SaveNodeAsync(active);
            await _sut.SaveNodeAsync(inactive);

            var nodes = await _sut.GetActiveNodesAsync();
            nodes.Should().ContainSingle(n => n.Id == active.Id);
            nodes.Should().NotContain(n => n.Id == inactive.Id);
        }

        [Fact]
        public async Task StoredFiles_FilterByOwnerAndNode()
        {
            var node = TestDataBuilder.CreateStoreNode();
            await _sut.SaveNodeAsync(node);
            var owner = TestDataBuilder.CreateCompressedPublicKey();

            await _sut.SaveStoredFileAsync(new StoredFile
            {
                FileId = "f1",
                OwnerPublicKey = owner,
                StoreNodeId = node.Id,
                FileHash = "hash1",
                FileSize = 10,
                IsActive = true
            });
            await _sut.SaveStoredFileAsync(new StoredFile
            {
                FileId = "f2",
                OwnerPublicKey = owner,
                StoreNodeId = "other",
                FileHash = "hash2",
                FileSize = 20,
                IsActive = true
            });

            var onNode = await _sut.GetUserFilesOnNodeAsync(owner, node.Id);
            onNode.Should().ContainSingle(f => f.FileId == "f1");

            var nodes = await _sut.GetNodesStoringUserFilesAsync(owner);
            nodes.Should().ContainSingle(n => n.Id == node.Id);
        }

        [Fact]
        public async Task PullPendingTransactions_MarksDelivered()
        {
            await _sut.AddTransactionAsync(new BlockchainTransaction
            {
                Type = "UserRegistration",
                From = "pk1",
                Signature = "sig"
            });
            await _sut.AddTransactionAsync(new BlockchainTransaction
            {
                Type = "Follow",
                From = "pk2",
                Signature = "sig2"
            });

            var first = await _sut.PullPendingTransactionsAsync(10);
            first.Should().HaveCount(2);

            var second = await _sut.PullPendingTransactionsAsync(10);
            second.Should().BeEmpty();
        }

        [Fact]
        public async Task Rekey_SaveGetRevoke()
        {
            var author = TestDataBuilder.CreateCompressedPublicKey();
            var follower = TestDataBuilder.CreateCompressedPublicKey();
            await _sut.FollowUserAsync(follower, author);

            var rekey = new UserRekey
            {
                FollowingPubKey = author,
                FollowerPubKey = follower,
                Tag = "posts",
                Scope = "tag",
                FileId = "",
                RkBase64 = "rk==",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            await _sut.SaveRekeyAsync(rekey);

            var loaded = await _sut.GetRekeyAsync(follower, author, "posts");
            loaded.Should().NotBeNull();
            loaded!.RkBase64.Should().Be("rk==");

            (await _sut.GetFollowersWithoutRekeyAsync(author, "posts")).Should().BeEmpty();

            await _sut.RevokeRekeysForFollowerAsync(author, follower);
            (await _sut.GetRekeyAsync(follower, author, "posts")).Should().BeNull();
        }

        [Fact]
        public async Task BlockCreatorNode_SaveUpdateGet()
        {
            var node = TestDataBuilder.CreateBlockCreatorNode();
            await _sut.SaveBlockCreatorNodeAsync(node);

            var byKey = await _sut.GetBlockCreatorNodeByPublicKeyAsync(node.PublicKey);
            byKey.Should().NotBeNull();

            node.IsActive = false;
            node.StakeAmount = 42;
            await _sut.UpdateBlockCreatorNodeAsync(node);

            var active = await _sut.GetActiveBlockCreatorNodesAsync();
            active.Should().NotContain(n => n.Id == node.Id);
        }

        [Fact]
        public async Task Conversation_CreateGetAndMessages()
        {
            var a = TestDataBuilder.CreateCompressedPublicKey();
            var b = TestDataBuilder.CreateCompressedPublicKey();

            var conv = await _sut.CreateOrGetConversationAsync(a, b);
            var again = await _sut.CreateOrGetConversationAsync(b, a);
            again.ConversationId.Should().Be(conv.ConversationId);

            await _sut.SaveChatMessageAsync(new ChatMessageMetadata
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ConversationId = conv.ConversationId,
                SenderPubKey = a,
                RecipientPubKey = b,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });

            (await _sut.GetUnreadCountForUserAsync(b)).Should().Be(1);
            await _sut.MarkMessagesAsReadAsync(conv.ConversationId, b);
            (await _sut.GetUnreadCountForUserAsync(b)).Should().Be(0);
        }

        [Fact]
        public async Task OnlineSession_SetAndCleanup()
        {
            var pk = TestDataBuilder.CreateCompressedPublicKey();
            await _sut.SetUserOnlineAsync(pk);
            (await _sut.IsUserOnlineAsync(pk)).Should().BeTrue();

            await _sut.SetUserOfflineAsync(pk);
            (await _sut.IsUserOnlineAsync(pk)).Should().BeFalse();
        }

        [Fact]
        public async Task Signaling_SaveAndConsume()
        {
            var to = TestDataBuilder.CreateCompressedPublicKey();
            var from = TestDataBuilder.CreateCompressedPublicKey();
            await _sut.SaveSignalingMessageAsync(new SignalingMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                FromPubKey = from,
                ToPubKey = to,
                Type = "offer",
                Payload = "{}",
                IsConsumed = false,
                CreatedAt = DateTime.UtcNow
            });

            var pending = await _sut.GetPendingSignalingMessagesAsync(to);
            pending.Should().ContainSingle();

            var again = await _sut.GetPendingSignalingMessagesAsync(to);
            again.Should().BeEmpty();
        }
    }
}

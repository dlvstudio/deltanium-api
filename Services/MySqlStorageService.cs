using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DeltaniumApi.Data;
using DeltaniumApi.Models;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace DeltaniumApi.Services
{
    public class MySqlStorageService : IStorageService
    {
        private readonly ApiDbContext _db;

        public MySqlStorageService(ApiDbContext db)
        {
            _db = db;
        }

        public Task<List<User>> GetUsersAsync() =>
            _db.Users.AsNoTracking().ToListAsync();

        public Task<List<StoreNode>> GetNodesAsync() =>
            _db.StoreNodes.AsNoTracking().ToListAsync();

        public Task<List<StoredFile>> GetStoredFilesAsync() =>
            _db.StoredFiles.AsNoTracking().ToListAsync();

        public Task<List<Voucher>> GetVouchersAsync() =>
            _db.Vouchers.AsNoTracking().ToListAsync();

        public Task<List<UserFollow>> GetUserFollowsAsync() =>
            _db.UserFollows.AsNoTracking().ToListAsync();

        public async Task<List<TransactionEnvelope>> GetTransactionEnvelopesAsync()
        {
            var records = await _db.TransactionEnvelopes.AsNoTracking().ToListAsync();
            return records.Select(r => r.ToModel()).ToList();
        }

        public async Task AddTransactionAsync(BlockchainTransaction tx)
        {
            _db.TransactionEnvelopes.Add(TransactionEnvelopeRecord.FromTransaction(tx));
            await _db.SaveChangesAsync();
        }

        public async Task<List<BlockchainTransaction>> PullPendingTransactionsAsync(int limit)
        {
            var pending = await _db.TransactionEnvelopes
                .Where(e => !e.Delivered)
                .OrderBy(e => e.ReceivedAt)
                .Take(limit)
                .ToListAsync();

            if (pending.Count == 0)
                return new List<BlockchainTransaction>();

            var now = DateTime.UtcNow;
            foreach (var env in pending)
            {
                env.Delivered = true;
                env.DeliveredAt = now;
            }

            await _db.SaveChangesAsync();
            return pending.Select(e => e.ToModel().Transaction).ToList();
        }

        public async Task SaveUserAsync(User user)
        {
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
        }

        public async Task UpdateUserAsync(User updatedUser)
        {
            var existing = await _db.Users.FirstOrDefaultAsync(u => u.PublicKey == updatedUser.PublicKey);
            if (existing == null) return;

            existing.Email = updatedUser.Email;
            existing.FullName = updatedUser.FullName;
            existing.DateOfBirth = updatedUser.DateOfBirth;
            existing.Bio = updatedUser.Bio;
            existing.AvatarUrl = updatedUser.AvatarUrl;
            existing.Signature = updatedUser.Signature;
            existing.RegistrationTimestamp = updatedUser.RegistrationTimestamp;
            existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task SaveNodeAsync(StoreNode node)
        {
            _db.StoreNodes.Add(node);
            await _db.SaveChangesAsync();
        }

        public async Task SaveStoredFileAsync(StoredFile file)
        {
            _db.StoredFiles.Add(file);
            await _db.SaveChangesAsync();
        }

        public async Task<bool> IsPublicKeyRegisteredAsync(string publicKey)
        {
            if (await _db.Users.AsNoTracking().AnyAsync(u => u.PublicKey == publicKey))
                return true;

            try
            {
                if (publicKey.Length == 66 && (publicKey.StartsWith("02") || publicKey.StartsWith("03")))
                {
                    var uncompressedKey = new PubKey(publicKey).Decompress().ToHex();
                    return await _db.Users.AsNoTracking().AnyAsync(u => u.PublicKey == uncompressedKey);
                }

                if (publicKey.Length == 130 && publicKey.StartsWith("04"))
                {
                    var compressedKey = new PubKey(publicKey).Compress().ToHex();
                    return await _db.Users.AsNoTracking().AnyAsync(u => u.PublicKey == compressedKey);
                }
            }
            catch
            {
                // ignore conversion errors; treat as not registered
            }

            return false;
        }

        public Task<List<StoreNode>> GetActiveNodesAsync() =>
            _db.StoreNodes.AsNoTracking().Where(n => n.IsActive).ToListAsync();

        public async Task<List<StoreNode>> GetNodesStoringUserFilesAsync(string ownerPublicKey)
        {
            var nodeIds = await _db.StoredFiles.AsNoTracking()
                .Where(f => f.OwnerPublicKey == ownerPublicKey && f.IsActive)
                .Select(f => f.StoreNodeId)
                .Distinct()
                .ToListAsync();

            return await _db.StoreNodes.AsNoTracking()
                .Where(n => n.IsActive && nodeIds.Contains(n.Id))
                .ToListAsync();
        }

        public Task<List<StoredFile>> GetUserFilesOnNodeAsync(string ownerPublicKey, string nodeId) =>
            _db.StoredFiles.AsNoTracking()
                .Where(f => f.OwnerPublicKey == ownerPublicKey && f.StoreNodeId == nodeId && f.IsActive)
                .ToListAsync();

        public Task<bool> IsVoucherValidAsync(string code) =>
            _db.Vouchers.AsNoTracking().AnyAsync(v => v.Code == code && !v.Used);

        public async Task MarkVoucherUsedAsync(string code)
        {
            await TryMarkVoucherUsedAsync(code);
        }

        public async Task<bool> TryMarkVoucherUsedAsync(string code)
        {
            // Atomic consume on relational providers (MySQL).
            // InMemory (unit tests) has no SQL — fall back to tracked entity update.
            if (_db.Database.IsRelational())
            {
                var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE vouchers SET Used = 1 WHERE Code = {code} AND Used = 0");
                return affected == 1;
            }

            var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Code == code && !v.Used);
            if (voucher == null) return false;
            voucher.Used = true;
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task SaveStoredFilesBatchAsync(List<StoredFile> files)
        {
            if (files == null || files.Count == 0) return;
            _db.StoredFiles.AddRange(files);
            await _db.SaveChangesAsync();
        }

        public async Task<bool> FollowUserAsync(string followerPublicKey, string followingPublicKey, string signature = null, long timestamp = 0)
        {
            if (followerPublicKey == followingPublicKey)
                return false;

            var exists = await _db.UserFollows.AnyAsync(f =>
                f.FollowerPublicKey == followerPublicKey && f.FollowingPublicKey == followingPublicKey);
            if (exists) return false;

            _db.UserFollows.Add(new UserFollow
            {
                FollowerPublicKey = followerPublicKey,
                FollowingPublicKey = followingPublicKey,
                Timestamp = timestamp,
                Signature = signature
            });
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> UnfollowUserAsync(string followerPublicKey, string followingPublicKey)
        {
            var follow = await _db.UserFollows.FirstOrDefaultAsync(f =>
                f.FollowerPublicKey == followerPublicKey && f.FollowingPublicKey == followingPublicKey);
            if (follow == null) return false;

            _db.UserFollows.Remove(follow);
            await _db.SaveChangesAsync();
            return true;
        }

        public Task<bool> IsFollowingAsync(string followerPublicKey, string followingPublicKey) =>
            _db.UserFollows.AsNoTracking().AnyAsync(f =>
                f.FollowerPublicKey == followerPublicKey && f.FollowingPublicKey == followingPublicKey);

        public Task<List<string>> GetFollowingPublicKeysAsync(string userPublicKey) =>
            _db.UserFollows.AsNoTracking()
                .Where(f => f.FollowerPublicKey == userPublicKey)
                .Select(f => f.FollowingPublicKey)
                .ToListAsync();

        public Task<List<string>> GetFollowersPublicKeysAsync(string userPublicKey) =>
            _db.UserFollows.AsNoTracking()
                .Where(f => f.FollowingPublicKey == userPublicKey)
                .Select(f => f.FollowerPublicKey)
                .ToListAsync();

        public Task<int> GetFollowersCountAsync(string userPublicKey) =>
            _db.UserFollows.AsNoTracking().CountAsync(f => f.FollowingPublicKey == userPublicKey);

        public Task<int> GetFollowingCountAsync(string userPublicKey) =>
            _db.UserFollows.AsNoTracking().CountAsync(f => f.FollowerPublicKey == userPublicKey);

        public Task<List<UserRekey>> GetUserRekeysAsync() =>
            _db.UserRekeys.AsNoTracking().ToListAsync();

        public async Task SaveRekeyAsync(UserRekey rekey)
        {
            var existing = await _db.UserRekeys.FirstOrDefaultAsync(r =>
                r.FollowingPubKey == rekey.FollowingPubKey &&
                r.FollowerPubKey == rekey.FollowerPubKey &&
                r.Tag == rekey.Tag &&
                r.Scope == rekey.Scope &&
                r.FileId == rekey.FileId);

            if (existing != null)
            {
                existing.RkBase64 = rekey.RkBase64;
                existing.CreatedAt = rekey.CreatedAt;
                existing.ExpiresAt = rekey.ExpiresAt;
                existing.IsActive = rekey.IsActive;
            }
            else
            {
                _db.UserRekeys.Add(rekey);
            }

            await _db.SaveChangesAsync();
        }

        public Task<UserRekey?> GetRekeyAsync(string followerPubKey, string followingPubKey, string tag) =>
            _db.UserRekeys.AsNoTracking().FirstOrDefaultAsync(r =>
                r.FollowerPubKey == followerPubKey &&
                r.FollowingPubKey == followingPubKey &&
                r.Tag == tag &&
                r.IsActive);

        public async Task<List<string>> GetFollowersWithoutRekeyAsync(string authorPubKey, string tag)
        {
            var followers = await GetFollowersPublicKeysAsync(authorPubKey);
            var followersWithRekeys = await _db.UserRekeys.AsNoTracking()
                .Where(r => r.FollowingPubKey == authorPubKey && r.Tag == tag && r.IsActive)
                .Select(r => r.FollowerPubKey)
                .ToListAsync();

            var withSet = followersWithRekeys.ToHashSet();
            return followers.Where(f => !withSet.Contains(f)).ToList();
        }

        public Task<List<UserFollow>> GetNewFollowersSinceAsync(string authorPubKey, DateTime since) =>
            _db.UserFollows.AsNoTracking()
                .Where(f => f.FollowingPublicKey == authorPubKey && f.CreatedAt > since)
                .OrderBy(f => f.CreatedAt)
                .ToListAsync();

        public async Task RevokeRekeysForFollowerAsync(string followingPubKey, string followerPubKey)
        {
            var rekeys = await _db.UserRekeys
                .Where(r => r.FollowingPubKey == followingPubKey && r.FollowerPubKey == followerPubKey && r.IsActive)
                .ToListAsync();

            if (rekeys.Count == 0) return;

            foreach (var rekey in rekeys)
                rekey.IsActive = false;

            await _db.SaveChangesAsync();
        }

        public async Task SaveRekeysBatchAsync(List<UserRekey> newRekeys)
        {
            foreach (var rekey in newRekeys)
            {
                var existing = await _db.UserRekeys.FirstOrDefaultAsync(r =>
                    r.FollowingPubKey == rekey.FollowingPubKey &&
                    r.FollowerPubKey == rekey.FollowerPubKey &&
                    r.Tag == rekey.Tag &&
                    r.Scope == rekey.Scope &&
                    r.FileId == rekey.FileId);

                if (existing != null)
                {
                    existing.RkBase64 = rekey.RkBase64;
                    existing.CreatedAt = rekey.CreatedAt;
                    existing.ExpiresAt = rekey.ExpiresAt;
                    existing.IsActive = rekey.IsActive;
                }
                else
                {
                    _db.UserRekeys.Add(rekey);
                }
            }

            await _db.SaveChangesAsync();
        }

        public Task<List<BlockCreatorNode>> GetBlockCreatorNodesAsync() =>
            _db.BlockCreatorNodes.AsNoTracking().ToListAsync();

        public Task<List<BlockCreatorNode>> GetActiveBlockCreatorNodesAsync() =>
            _db.BlockCreatorNodes.AsNoTracking().Where(n => n.IsActive).ToListAsync();

        public async Task SaveBlockCreatorNodeAsync(BlockCreatorNode node)
        {
            _db.BlockCreatorNodes.Add(node);
            await _db.SaveChangesAsync();
        }

        public async Task UpdateBlockCreatorNodeAsync(BlockCreatorNode updatedNode)
        {
            var existing = await _db.BlockCreatorNodes.FirstOrDefaultAsync(n =>
                n.Id == updatedNode.Id || n.PublicKey == updatedNode.PublicKey);
            if (existing == null) return;

            existing.Endpoint = updatedNode.Endpoint;
            existing.IsActive = updatedNode.IsActive;
            existing.LastSeen = updatedNode.LastSeen;
            existing.StakeAmount = updatedNode.StakeAmount;
            existing.PublicKey = updatedNode.PublicKey;
            await _db.SaveChangesAsync();
        }

        public Task<BlockCreatorNode?> GetBlockCreatorNodeByPublicKeyAsync(string publicKey) =>
            _db.BlockCreatorNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicKey == publicKey);

        public Task<List<Conversation>> GetConversationsAsync() =>
            _db.Conversations.AsNoTracking().ToListAsync();

        public async Task<Conversation?> GetConversationByParticipantsAsync(string pubKeyA, string pubKeyB)
        {
            var conversations = await _db.Conversations.AsNoTracking().ToListAsync();
            return conversations.FirstOrDefault(c =>
                c.Participants.Contains(pubKeyA) && c.Participants.Contains(pubKeyB));
        }

        public async Task<Conversation> CreateOrGetConversationAsync(string pubKeyA, string pubKeyB)
        {
            var existing = await GetConversationByParticipantsAsync(pubKeyA, pubKeyB);
            if (existing != null) return existing;

            var conversation = new Conversation
            {
                Participants = new List<string> { pubKeyA, pubKeyB }
            };
            _db.Conversations.Add(conversation);
            await _db.SaveChangesAsync();
            return conversation;
        }

        public async Task<List<Conversation>> GetUserConversationsAsync(string userPubKey)
        {
            var conversations = await _db.Conversations.AsNoTracking().ToListAsync();
            return conversations
                .Where(c => c.Participants.Contains(userPubKey))
                .OrderByDescending(c => c.LastMessageAt)
                .ToList();
        }

        public async Task UpdateConversationLastMessageAsync(string conversationId)
        {
            var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.ConversationId == conversationId);
            if (conv == null) return;
            conv.LastMessageAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public Task<List<ChatMessageMetadata>> GetChatMessagesAsync() =>
            _db.ChatMessages.AsNoTracking().ToListAsync();

        public async Task SaveChatMessageAsync(ChatMessageMetadata message)
        {
            _db.ChatMessages.Add(message);
            await _db.SaveChangesAsync();
        }

        public Task<List<ChatMessageMetadata>> GetConversationMessagesAsync(string conversationId, int page = 0, int pageSize = 50) =>
            _db.ChatMessages.AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.CreatedAt)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToListAsync();

        public Task<List<ChatMessageMetadata>> GetUnreadMessagesForUserAsync(string userPubKey) =>
            _db.ChatMessages.AsNoTracking()
                .Where(m => m.RecipientPubKey == userPubKey && !m.IsRead)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

        public Task<int> GetUnreadCountForUserAsync(string userPubKey) =>
            _db.ChatMessages.AsNoTracking().CountAsync(m => m.RecipientPubKey == userPubKey && !m.IsRead);

        public async Task MarkMessagesAsReadAsync(string conversationId, string userPubKey)
        {
            var messages = await _db.ChatMessages
                .Where(m => m.ConversationId == conversationId && m.RecipientPubKey == userPubKey && !m.IsRead)
                .ToListAsync();

            if (messages.Count == 0) return;

            foreach (var msg in messages)
                msg.IsRead = true;

            await _db.SaveChangesAsync();
        }

        public Task<List<OnlineSession>> GetOnlineSessionsAsync() =>
            _db.OnlineSessions.AsNoTracking().ToListAsync();

        public async Task SetUserOnlineAsync(string userPubKey)
        {
            var existing = await _db.OnlineSessions.FirstOrDefaultAsync(s => s.UserPubKey == userPubKey);
            if (existing != null)
            {
                existing.LastHeartbeat = DateTime.UtcNow;
            }
            else
            {
                _db.OnlineSessions.Add(new OnlineSession { UserPubKey = userPubKey });
            }

            await _db.SaveChangesAsync();
        }

        public async Task SetUserOfflineAsync(string userPubKey)
        {
            var existing = await _db.OnlineSessions.FirstOrDefaultAsync(s => s.UserPubKey == userPubKey);
            if (existing == null) return;
            _db.OnlineSessions.Remove(existing);
            await _db.SaveChangesAsync();
        }

        public async Task<bool> IsUserOnlineAsync(string userPubKey)
        {
            var session = await _db.OnlineSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserPubKey == userPubKey);
            if (session == null) return false;
            return (DateTime.UtcNow - session.LastHeartbeat).TotalSeconds < 60;
        }

        public async Task CleanupStaleSessionsAsync()
        {
            var staleThreshold = DateTime.UtcNow.AddSeconds(-60);
            var stale = await _db.OnlineSessions.Where(s => s.LastHeartbeat < staleThreshold).ToListAsync();
            if (stale.Count == 0) return;
            _db.OnlineSessions.RemoveRange(stale);
            await _db.SaveChangesAsync();
        }

        public Task<List<SignalingMessage>> GetSignalingMessagesAsync() =>
            _db.SignalingMessages.AsNoTracking().ToListAsync();

        public async Task SaveSignalingMessageAsync(SignalingMessage message)
        {
            _db.SignalingMessages.Add(message);
            await _db.SaveChangesAsync();
        }

        public async Task<List<SignalingMessage>> GetPendingSignalingMessagesAsync(string toPubKey)
        {
            var pending = await _db.SignalingMessages
                .Where(m => m.ToPubKey == toPubKey && !m.IsConsumed)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            if (pending.Count == 0)
                return pending;

            foreach (var msg in pending)
                msg.IsConsumed = true;

            await _db.SaveChangesAsync();
            return pending;
        }

        public async Task CleanupOldSignalingMessagesAsync()
        {
            var threshold = DateTime.UtcNow.AddMinutes(-5);
            var old = await _db.SignalingMessages.Where(m => m.CreatedAt < threshold).ToListAsync();
            if (old.Count == 0) return;
            _db.SignalingMessages.RemoveRange(old);
            await _db.SaveChangesAsync();
        }
    }
}

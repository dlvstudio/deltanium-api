using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;

namespace DeltaniumApi.Services
{
    public interface IStorageService
    {
        Task<List<User>> GetUsersAsync();
        Task<List<StoreNode>> GetNodesAsync();
        Task<List<StoredFile>> GetStoredFilesAsync();
        Task<List<Voucher>> GetVouchersAsync();
        Task<List<UserFollow>> GetUserFollowsAsync();
        Task<List<TransactionEnvelope>> GetTransactionEnvelopesAsync();
        Task AddTransactionAsync(BlockchainTransaction tx);
        Task<List<BlockchainTransaction>> PullPendingTransactionsAsync(int limit);
        Task SaveUserAsync(User user);
        Task UpdateUserAsync(User updatedUser);
        Task SaveNodeAsync(StoreNode node);
        Task SaveStoredFileAsync(StoredFile file);
        Task<bool> IsPublicKeyRegisteredAsync(string publicKey);
        Task<List<StoreNode>> GetActiveNodesAsync();
        Task<List<StoreNode>> GetNodesStoringUserFilesAsync(string ownerPublicKey);
        Task<List<StoredFile>> GetUserFilesOnNodeAsync(string ownerPublicKey, string nodeId);
        Task<bool> IsVoucherValidAsync(string code);
        Task MarkVoucherUsedAsync(string code);
        /// <summary>Atomically mark voucher used; returns false if missing or already used.</summary>
        Task<bool> TryMarkVoucherUsedAsync(string code);
        Task SaveStoredFilesBatchAsync(System.Collections.Generic.List<StoredFile> files);
        Task<bool> FollowUserAsync(string followerPublicKey, string followingPublicKey, string signature = null, long timestamp = 0);
        Task<bool> UnfollowUserAsync(string followerPublicKey, string followingPublicKey);
        Task<bool> IsFollowingAsync(string followerPublicKey, string followingPublicKey);
        Task<List<string>> GetFollowingPublicKeysAsync(string userPublicKey);
        Task<List<string>> GetFollowersPublicKeysAsync(string userPublicKey);
        Task<int> GetFollowersCountAsync(string userPublicKey);
        Task<int> GetFollowingCountAsync(string userPublicKey);
        Task<List<UserRekey>> GetUserRekeysAsync();
        Task SaveRekeyAsync(UserRekey rekey);
        Task<UserRekey?> GetRekeyAsync(string followerPubKey, string followingPubKey, string tag);
        Task<List<string>> GetFollowersWithoutRekeyAsync(string authorPubKey, string tag);
        Task<List<UserFollow>> GetNewFollowersSinceAsync(string authorPubKey, DateTime since);
        Task RevokeRekeysForFollowerAsync(string followingPubKey, string followerPubKey);
        Task SaveRekeysBatchAsync(List<UserRekey> newRekeys);
        Task<List<BlockCreatorNode>> GetBlockCreatorNodesAsync();
        Task<List<BlockCreatorNode>> GetActiveBlockCreatorNodesAsync();
        Task SaveBlockCreatorNodeAsync(BlockCreatorNode node);
        Task UpdateBlockCreatorNodeAsync(BlockCreatorNode updatedNode);
        Task<BlockCreatorNode?> GetBlockCreatorNodeByPublicKeyAsync(string publicKey);
        Task<List<Conversation>> GetConversationsAsync();
        Task<Conversation?> GetConversationByIdAsync(string conversationId);
        Task<Conversation?> GetConversationByParticipantsAsync(string pubKeyA, string pubKeyB);
        Task<Conversation> CreateOrGetConversationAsync(string pubKeyA, string pubKeyB);
        Task<List<Conversation>> GetUserConversationsAsync(string userPubKey);
        Task UpdateConversationLastMessageAsync(string conversationId);
        Task<List<ChatMessageMetadata>> GetChatMessagesAsync();
        Task SaveChatMessageAsync(ChatMessageMetadata message);
        Task<List<ChatMessageMetadata>> GetConversationMessagesAsync(string conversationId, int page = 0, int pageSize = 50);
        Task<List<ChatMessageMetadata>> GetUnreadMessagesForUserAsync(string userPubKey);
        Task<int> GetUnreadCountForUserAsync(string userPubKey);
        Task MarkMessagesAsReadAsync(string conversationId, string userPubKey);
        Task<List<OnlineSession>> GetOnlineSessionsAsync();
        Task SetUserOnlineAsync(string userPubKey);
        Task SetUserOfflineAsync(string userPubKey);
        Task<bool> IsUserOnlineAsync(string userPubKey);
        Task CleanupStaleSessionsAsync();
        Task<List<SignalingMessage>> GetSignalingMessagesAsync();
        Task SaveSignalingMessageAsync(SignalingMessage message);
        Task<List<SignalingMessage>> GetPendingSignalingMessagesAsync(string toPubKey);
        Task CleanupOldSignalingMessagesAsync();
    }
}

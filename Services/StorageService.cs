using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using Newtonsoft.Json;
using NBitcoin;

namespace DeltaniumApi.Services
{
    public class StorageService
    {
        private readonly string _usersFilePath = "users.json";
        private readonly string _nodesFilePath = "nodes.json";
        private readonly string _blockCreatorNodesFilePath = "blockcreator_nodes.json";
        private readonly string _storedFilesPath = "stored_files.json";
        private readonly string _vouchersFilePath = "vouchers.json";
        private readonly string _userFollowsFilePath = "user_follows.json";
        private readonly string _userRekeysFilePath = "user_rekeys.json";
        private readonly string _conversationsFilePath = "conversations.json";
        private readonly string _chatMessagesFilePath = "chat_messages.json";
        private readonly string _onlineSessionsFilePath = "online_sessions.json";
        private readonly string _signalingMessagesFilePath = "signaling_messages.json";
        private readonly string _transactionsFilePath = "transactions.json";

        public StorageService()
        {
            InitializeStorage();
        }

        private void InitializeStorage()
        {
            if (!File.Exists(_usersFilePath))
                File.WriteAllText(_usersFilePath, "[]");
            if (!File.Exists(_nodesFilePath))
                File.WriteAllText(_nodesFilePath, "[]");
            if (!File.Exists(_blockCreatorNodesFilePath))
                File.WriteAllText(_blockCreatorNodesFilePath, "[]");
            if (!File.Exists(_storedFilesPath))
                File.WriteAllText(_storedFilesPath, "[]");
            if (!File.Exists(_vouchersFilePath))
                File.WriteAllText(_vouchersFilePath, "[]");
            if (!File.Exists(_userFollowsFilePath))
                File.WriteAllText(_userFollowsFilePath, "[]");
            if (!File.Exists(_userRekeysFilePath))
                File.WriteAllText(_userRekeysFilePath, "[]");
            if (!File.Exists(_conversationsFilePath))
                File.WriteAllText(_conversationsFilePath, "[]");
            if (!File.Exists(_chatMessagesFilePath))
                File.WriteAllText(_chatMessagesFilePath, "[]");
            if (!File.Exists(_onlineSessionsFilePath))
                File.WriteAllText(_onlineSessionsFilePath, "[]");
            if (!File.Exists(_signalingMessagesFilePath))
                File.WriteAllText(_signalingMessagesFilePath, "[]");
            if (!File.Exists(_transactionsFilePath))
                File.WriteAllText(_transactionsFilePath, "[]");
        }

        public async Task<List<User>> GetUsersAsync()
        {
            var json = await File.ReadAllTextAsync(_usersFilePath);
            return JsonConvert.DeserializeObject<List<User>>(json) ?? new List<User>();
        }

        public async Task<List<StoreNode>> GetNodesAsync()
        {
            var json = await File.ReadAllTextAsync(_nodesFilePath);
            return JsonConvert.DeserializeObject<List<StoreNode>>(json) ?? new List<StoreNode>();
        }

        public async Task<List<StoredFile>> GetStoredFilesAsync()
        {
            var json = await File.ReadAllTextAsync(_storedFilesPath);
            return JsonConvert.DeserializeObject<List<StoredFile>>(json) ?? new List<StoredFile>();
        }

        public async Task<List<Voucher>> GetVouchersAsync()
        {
            var json = await File.ReadAllTextAsync(_vouchersFilePath);
            return JsonConvert.DeserializeObject<List<Voucher>>(json) ?? new List<Voucher>();
        }

        public async Task<List<UserFollow>> GetUserFollowsAsync()
        {
            var json = await File.ReadAllTextAsync(_userFollowsFilePath);
            return JsonConvert.DeserializeObject<List<UserFollow>>(json) ?? new List<UserFollow>();
        }

        // Transaction relay methods
        public async Task<List<TransactionEnvelope>> GetTransactionEnvelopesAsync()
        {
            var json = await File.ReadAllTextAsync(_transactionsFilePath);
            return JsonConvert.DeserializeObject<List<TransactionEnvelope>>(json) ?? new List<TransactionEnvelope>();
        }

        public async Task AddTransactionAsync(BlockchainTransaction tx)
        {
            var envelopes = await GetTransactionEnvelopesAsync();
            envelopes.Add(new TransactionEnvelope
            {
                Transaction = tx,
                Delivered = false,
                ReceivedAt = DateTime.UtcNow
            });
            await File.WriteAllTextAsync(_transactionsFilePath, JsonConvert.SerializeObject(envelopes, Formatting.Indented));
        }

        public async Task<List<BlockchainTransaction>> PullPendingTransactionsAsync(int limit)
        {
            var envelopes = await GetTransactionEnvelopesAsync();
            var pending = envelopes.Where(e => !e.Delivered).Take(limit).ToList();
            if (pending.Count == 0) return new List<BlockchainTransaction>();

            var now = DateTime.UtcNow;
            foreach (var env in pending)
            {
                env.Delivered = true;
                env.DeliveredAt = now;
            }

            await File.WriteAllTextAsync(_transactionsFilePath, JsonConvert.SerializeObject(envelopes, Formatting.Indented));
            return pending.Select(e => e.Transaction).ToList();
        }

        public async Task SaveUserAsync(User user)
        {
            var users = await GetUsersAsync();
            users.Add(user);
            await File.WriteAllTextAsync(_usersFilePath, JsonConvert.SerializeObject(users, Formatting.Indented));
        }

        public async Task UpdateUserAsync(User updatedUser)
        {
            var users = await GetUsersAsync();
            var index = users.FindIndex(u => u.PublicKey == updatedUser.PublicKey);
            
            if (index != -1)
            {
                users[index] = updatedUser;
                await File.WriteAllTextAsync(_usersFilePath, JsonConvert.SerializeObject(users, Formatting.Indented));
            }
        }

        public async Task SaveNodeAsync(StoreNode node)
        {
            var nodes = await GetNodesAsync();
            nodes.Add(node);
            await File.WriteAllTextAsync(_nodesFilePath, JsonConvert.SerializeObject(nodes, Formatting.Indented));
        }

        public async Task SaveStoredFileAsync(StoredFile file)
        {
            var files = await GetStoredFilesAsync();
            files.Add(file);
            await File.WriteAllTextAsync(_storedFilesPath, JsonConvert.SerializeObject(files, Formatting.Indented));
        }

        public async Task<bool> IsPublicKeyRegisteredAsync(string publicKey)
        {
            var users = await GetUsersAsync();
            Console.WriteLine($"[STORAGE] Checking registration for publicKey: {publicKey} (length: {publicKey.Length})");
            Console.WriteLine($"[STORAGE] Total users in database: {users.Count}");
            
            // Check direct match first
            if (users.Any(u => u.PublicKey == publicKey))
            {
                Console.WriteLine($"[STORAGE] ✅ Direct match found for: {publicKey}");
                return true;
            }
            
            // Debug: Print all existing public keys for comparison
            Console.WriteLine("[STORAGE] Existing public keys in database:");
            foreach (var user in users)
            {
                Console.WriteLine($"[STORAGE]   - {user.PublicKey} (length: {user.PublicKey.Length}) - {user.FullName}");
            }
            
            // If not found, try to convert and check alternative formats
            try
            {
                // If input is compressed (66 chars), try to find uncompressed version
                if (publicKey.Length == 66 && (publicKey.StartsWith("02") || publicKey.StartsWith("03")))
                {
                    Console.WriteLine($"[STORAGE] Input is compressed, converting to uncompressed...");
                    // Try to convert to uncompressed and search
                    var compressedKey = new NBitcoin.PubKey(publicKey);
                    var uncompressedKey = compressedKey.Decompress().ToHex(); // Decompress THEN convert to hex
                    Console.WriteLine($"[STORAGE] Converted to uncompressed: {uncompressedKey} (length: {uncompressedKey.Length})");
                    
                    if (users.Any(u => u.PublicKey == uncompressedKey))
                    {
                        Console.WriteLine($"[STORAGE] ✅ Match found with uncompressed version: {uncompressedKey}");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"[STORAGE] ❌ No match found with uncompressed version");
                    }
                }
                // If input is uncompressed (130 chars), try to find compressed version
                else if (publicKey.Length == 130 && publicKey.StartsWith("04"))
                {
                    Console.WriteLine($"[STORAGE] Input is uncompressed, converting to compressed...");
                    var uncompressedKey = new NBitcoin.PubKey(publicKey);
                    var compressedKey = uncompressedKey.Compress().ToHex();
                    Console.WriteLine($"[STORAGE] Converted to compressed: {compressedKey} (length: {compressedKey.Length})");
                    
                    if (users.Any(u => u.PublicKey == compressedKey))
                    {
                        Console.WriteLine($"[STORAGE] ✅ Match found with compressed version: {compressedKey}");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"[STORAGE] ❌ No match found with compressed version");
                    }
                }
                else
                {
                    Console.WriteLine($"[STORAGE] Unknown public key format: length={publicKey.Length}, prefix={publicKey.Substring(0, Math.Min(4, publicKey.Length))}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STORAGE] Error converting public key format: {ex.Message}");
                Console.WriteLine($"[STORAGE] Stack trace: {ex.StackTrace}");
            }
            
            Console.WriteLine($"[STORAGE] ❌ No match found for public key: {publicKey}");
            return false;
        }

        public async Task<List<StoreNode>> GetActiveNodesAsync()
        {
            var nodes = await GetNodesAsync();
            return nodes.Where(n => n.IsActive).ToList();
        }

        public async Task<List<StoreNode>> GetNodesStoringUserFilesAsync(string ownerPublicKey)
        {
            var storedFiles = await GetStoredFilesAsync();
            var nodes = await GetNodesAsync();

            // Get IDs of nodes that store files for this user
            var nodeIds = storedFiles
                .Where(f => f.OwnerPublicKey == ownerPublicKey && f.IsActive)
                .Select(f => f.StoreNodeId)
                .Distinct();

            // Return active nodes that store user's files
            return nodes
                .Where(n => n.IsActive && nodeIds.Contains(n.Id))
                .ToList();
        }

        public async Task<List<StoredFile>> GetUserFilesOnNodeAsync(string ownerPublicKey, string nodeId)
        {
            var storedFiles = await GetStoredFilesAsync();
            return storedFiles
                .Where(f => f.OwnerPublicKey == ownerPublicKey && 
                           f.StoreNodeId == nodeId && 
                           f.IsActive)
                .ToList();
        }

        public async Task<bool> IsVoucherValidAsync(string code)
        {
            var vouchers = await GetVouchersAsync();
            var voucher = vouchers.FirstOrDefault(v => v.Code == code && !v.Used);
            return voucher != null;
        }

        public async Task MarkVoucherUsedAsync(string code)
        {
            var vouchers = await GetVouchersAsync();
            var voucher = vouchers.FirstOrDefault(v => v.Code == code);
            if (voucher != null)
            {
                voucher.Used = true;
                await File.WriteAllTextAsync(_vouchersFilePath, JsonConvert.SerializeObject(vouchers, Formatting.Indented));
            }
        }

        // User Follow Methods
        public async Task<bool> FollowUserAsync(string followerPublicKey, string followingPublicKey, string signature = null, long timestamp = 0)
        {
            if (followerPublicKey == followingPublicKey)
                return false; // Cannot follow yourself

            var follows = await GetUserFollowsAsync();
            
            // Check if already following
            if (follows.Any(f => f.FollowerPublicKey == followerPublicKey && f.FollowingPublicKey == followingPublicKey))
                return false;

            var follow = new UserFollow
            {
                FollowerPublicKey = followerPublicKey,
                FollowingPublicKey = followingPublicKey,
                Timestamp = timestamp, // Store user-signed timestamp
                Signature = signature // Store signature for blockchain verification
            };

            follows.Add(follow);
            await File.WriteAllTextAsync(_userFollowsFilePath, JsonConvert.SerializeObject(follows, Formatting.Indented));
            return true;
        }

        public async Task<bool> UnfollowUserAsync(string followerPublicKey, string followingPublicKey)
        {
            var follows = await GetUserFollowsAsync();
            var follow = follows.FirstOrDefault(f => f.FollowerPublicKey == followerPublicKey && f.FollowingPublicKey == followingPublicKey);
            
            if (follow == null)
                return false;

            follows.Remove(follow);
            await File.WriteAllTextAsync(_userFollowsFilePath, JsonConvert.SerializeObject(follows, Formatting.Indented));
            return true;
        }

        public async Task<bool> IsFollowingAsync(string followerPublicKey, string followingPublicKey)
        {
            var follows = await GetUserFollowsAsync();
            return follows.Any(f => f.FollowerPublicKey == followerPublicKey && f.FollowingPublicKey == followingPublicKey);
        }

        public async Task<List<string>> GetFollowingPublicKeysAsync(string userPublicKey)
        {
            var follows = await GetUserFollowsAsync();
            return follows
                .Where(f => f.FollowerPublicKey == userPublicKey)
                .Select(f => f.FollowingPublicKey)
                .ToList();
        }

        public async Task<List<string>> GetFollowersPublicKeysAsync(string userPublicKey)
        {
            var follows = await GetUserFollowsAsync();
            return follows
                .Where(f => f.FollowingPublicKey == userPublicKey)
                .Select(f => f.FollowerPublicKey)
                .ToList();
        }

        public async Task<int> GetFollowersCountAsync(string userPublicKey)
        {
            var follows = await GetUserFollowsAsync();
            return follows.Count(f => f.FollowingPublicKey == userPublicKey);
        }

        public async Task<int> GetFollowingCountAsync(string userPublicKey)
        {
            var follows = await GetUserFollowsAsync();
            return follows.Count(f => f.FollowerPublicKey == userPublicKey);
        }

        // ========== PRE REKEY METHODS ==========
        
        public async Task<List<UserRekey>> GetUserRekeysAsync()
        {
            var json = await File.ReadAllTextAsync(_userRekeysFilePath);
            return JsonConvert.DeserializeObject<List<UserRekey>>(json) ?? new List<UserRekey>();
        }

        /// <summary>
        /// Save a new rekey uploaded by author A for follower B
        /// </summary>
        public async Task SaveRekeyAsync(UserRekey rekey)
        {
            var rekeys = await GetUserRekeysAsync();
            
            // Check if rekey already exists (update instead of duplicate)
            var existing = rekeys.FirstOrDefault(r => 
                r.FollowingPubKey == rekey.FollowingPubKey && 
                r.FollowerPubKey == rekey.FollowerPubKey && 
                r.Tag == rekey.Tag &&
                r.Scope == rekey.Scope &&
                r.FileId == rekey.FileId);
            
            if (existing != null)
            {
                // Update existing rekey
                existing.RkBase64 = rekey.RkBase64;
                existing.CreatedAt = rekey.CreatedAt;
                existing.ExpiresAt = rekey.ExpiresAt;
                existing.IsActive = rekey.IsActive;
            }
            else
            {
                // Add new rekey
                rekeys.Add(rekey);
            }
            
            await File.WriteAllTextAsync(_userRekeysFilePath, JsonConvert.SerializeObject(rekeys, Formatting.Indented));
        }

        /// <summary>
        /// Get a specific rekey for follower B to decrypt author A's content with tag
        /// </summary>
        public async Task<UserRekey?> GetRekeyAsync(string followerPubKey, string followingPubKey, string tag)
        {
            var rekeys = await GetUserRekeysAsync();
            return rekeys.FirstOrDefault(r => 
                r.FollowerPubKey == followerPubKey && 
                r.FollowingPubKey == followingPubKey && 
                r.Tag == tag &&
                r.IsActive);
        }

        /// <summary>
        /// Get all followers who don't have rekeys yet for a specific tag
        /// Used by author A to know which followers need rekeys generated
        /// </summary>
        public async Task<List<string>> GetFollowersWithoutRekeyAsync(string authorPubKey, string tag)
        {
            var followers = await GetFollowersPublicKeysAsync(authorPubKey);
            var rekeys = await GetUserRekeysAsync();
            
            // Get followers who already have active rekeys for this tag
            var followersWithRekeys = rekeys
                .Where(r => r.FollowingPubKey == authorPubKey && r.Tag == tag && r.IsActive)
                .Select(r => r.FollowerPubKey)
                .ToHashSet();
            
            // Return followers who don't have rekeys yet
            return followers.Where(f => !followersWithRekeys.Contains(f)).ToList();
        }

        /// <summary>
        /// Get new followers since a specific timestamp
        /// Used by author A to periodically check for new followers
        /// </summary>
        public async Task<List<UserFollow>> GetNewFollowersSinceAsync(string authorPubKey, DateTime since)
        {
            var follows = await GetUserFollowsAsync();
            return follows
                .Where(f => f.FollowingPublicKey == authorPubKey && f.CreatedAt > since)
                .OrderBy(f => f.CreatedAt)
                .ToList();
        }

        /// <summary>
        /// Revoke/deactivate all rekeys for a specific follower
        /// Used when unfollowing or blocking
        /// </summary>
        public async Task RevokeRekeysForFollowerAsync(string followingPubKey, string followerPubKey)
        {
            var rekeys = await GetUserRekeysAsync();
            var updated = false;
            
            foreach (var rekey in rekeys.Where(r => 
                r.FollowingPubKey == followingPubKey && 
                r.FollowerPubKey == followerPubKey && 
                r.IsActive))
            {
                rekey.IsActive = false;
                updated = true;
            }
            
            if (updated)
            {
                await File.WriteAllTextAsync(_userRekeysFilePath, JsonConvert.SerializeObject(rekeys, Formatting.Indented));
            }
        }

        /// <summary>
        /// Batch save multiple rekeys (for efficiency when author uploads many at once)
        /// </summary>
        public async Task SaveRekeysBatchAsync(List<UserRekey> newRekeys)
        {
            var existingRekeys = await GetUserRekeysAsync();
            
            foreach (var rekey in newRekeys)
            {
                var existing = existingRekeys.FirstOrDefault(r => 
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
                    existingRekeys.Add(rekey);
                }
            }
            
            await File.WriteAllTextAsync(_userRekeysFilePath, JsonConvert.SerializeObject(existingRekeys, Formatting.Indented));
        }

        // Block Creator Node Methods
        public async Task<List<BlockCreatorNode>> GetBlockCreatorNodesAsync()
        {
            var json = await File.ReadAllTextAsync(_blockCreatorNodesFilePath);
            return JsonConvert.DeserializeObject<List<BlockCreatorNode>>(json) ?? new List<BlockCreatorNode>();
        }

        public async Task<List<BlockCreatorNode>> GetActiveBlockCreatorNodesAsync()
        {
            var nodes = await GetBlockCreatorNodesAsync();
            return nodes.Where(n => n.IsActive).ToList();
        }

        public async Task SaveBlockCreatorNodeAsync(BlockCreatorNode node)
        {
            var nodes = await GetBlockCreatorNodesAsync();
            nodes.Add(node);
            await File.WriteAllTextAsync(_blockCreatorNodesFilePath, JsonConvert.SerializeObject(nodes, Formatting.Indented));
        }

        public async Task UpdateBlockCreatorNodeAsync(BlockCreatorNode updatedNode)
        {
            var nodes = await GetBlockCreatorNodesAsync();
            var index = nodes.FindIndex(n => n.Id == updatedNode.Id || n.PublicKey == updatedNode.PublicKey);
            
            if (index != -1)
            {
                nodes[index] = updatedNode;
                await File.WriteAllTextAsync(_blockCreatorNodesFilePath, JsonConvert.SerializeObject(nodes, Formatting.Indented));
            }
        }

        public async Task<BlockCreatorNode?> GetBlockCreatorNodeByPublicKeyAsync(string publicKey)
        {
            var nodes = await GetBlockCreatorNodesAsync();
            return nodes.FirstOrDefault(n => n.PublicKey == publicKey);
        }

        // ========== CHAT METHODS ==========

        public async Task<List<Conversation>> GetConversationsAsync()
        {
            if (!File.Exists(_conversationsFilePath))
                await File.WriteAllTextAsync(_conversationsFilePath, "[]");
            var json = await File.ReadAllTextAsync(_conversationsFilePath);
            return JsonConvert.DeserializeObject<List<Conversation>>(json) ?? new List<Conversation>();
        }

        public async Task<Conversation?> GetConversationByParticipantsAsync(string pubKeyA, string pubKeyB)
        {
            var conversations = await GetConversationsAsync();
            return conversations.FirstOrDefault(c =>
                c.Participants.Contains(pubKeyA) && c.Participants.Contains(pubKeyB));
        }

        public async Task<Conversation> CreateOrGetConversationAsync(string pubKeyA, string pubKeyB)
        {
            var existing = await GetConversationByParticipantsAsync(pubKeyA, pubKeyB);
            if (existing != null)
                return existing;

            var conversation = new Conversation
            {
                Participants = new List<string> { pubKeyA, pubKeyB }
            };

            var conversations = await GetConversationsAsync();
            conversations.Add(conversation);
            await File.WriteAllTextAsync(_conversationsFilePath, JsonConvert.SerializeObject(conversations, Formatting.Indented));
            return conversation;
        }

        public async Task<List<Conversation>> GetUserConversationsAsync(string userPubKey)
        {
            var conversations = await GetConversationsAsync();
            return conversations
                .Where(c => c.Participants.Contains(userPubKey))
                .OrderByDescending(c => c.LastMessageAt)
                .ToList();
        }

        public async Task UpdateConversationLastMessageAsync(string conversationId)
        {
            var conversations = await GetConversationsAsync();
            var conv = conversations.FirstOrDefault(c => c.ConversationId == conversationId);
            if (conv != null)
            {
                conv.LastMessageAt = DateTime.UtcNow;
                await File.WriteAllTextAsync(_conversationsFilePath, JsonConvert.SerializeObject(conversations, Formatting.Indented));
            }
        }

        public async Task<List<ChatMessageMetadata>> GetChatMessagesAsync()
        {
            if (!File.Exists(_chatMessagesFilePath))
                await File.WriteAllTextAsync(_chatMessagesFilePath, "[]");
            var json = await File.ReadAllTextAsync(_chatMessagesFilePath);
            return JsonConvert.DeserializeObject<List<ChatMessageMetadata>>(json) ?? new List<ChatMessageMetadata>();
        }

        public async Task SaveChatMessageAsync(ChatMessageMetadata message)
        {
            var messages = await GetChatMessagesAsync();
            messages.Add(message);
            await File.WriteAllTextAsync(_chatMessagesFilePath, JsonConvert.SerializeObject(messages, Formatting.Indented));
        }

        public async Task<List<ChatMessageMetadata>> GetConversationMessagesAsync(string conversationId, int page = 0, int pageSize = 50)
        {
            var messages = await GetChatMessagesAsync();
            return messages
                .Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.CreatedAt)
                .Skip(page * pageSize)
                .Take(pageSize)
                .ToList();
        }

        public async Task<List<ChatMessageMetadata>> GetUnreadMessagesForUserAsync(string userPubKey)
        {
            var messages = await GetChatMessagesAsync();
            return messages
                .Where(m => m.RecipientPubKey == userPubKey && !m.IsRead)
                .OrderBy(m => m.CreatedAt)
                .ToList();
        }

        public async Task<int> GetUnreadCountForUserAsync(string userPubKey)
        {
            var messages = await GetChatMessagesAsync();
            return messages.Count(m => m.RecipientPubKey == userPubKey && !m.IsRead);
        }

        public async Task MarkMessagesAsReadAsync(string conversationId, string userPubKey)
        {
            var messages = await GetChatMessagesAsync();
            var updated = false;
            foreach (var msg in messages.Where(m => m.ConversationId == conversationId && m.RecipientPubKey == userPubKey && !m.IsRead))
            {
                msg.IsRead = true;
                updated = true;
            }
            if (updated)
            {
                await File.WriteAllTextAsync(_chatMessagesFilePath, JsonConvert.SerializeObject(messages, Formatting.Indented));
            }
        }

        // ========== PRESENCE & SIGNALING METHODS ==========

        public async Task<List<OnlineSession>> GetOnlineSessionsAsync()
        {
            if (!File.Exists(_onlineSessionsFilePath))
                await File.WriteAllTextAsync(_onlineSessionsFilePath, "[]");
            var json = await File.ReadAllTextAsync(_onlineSessionsFilePath);
            return JsonConvert.DeserializeObject<List<OnlineSession>>(json) ?? new List<OnlineSession>();
        }

        public async Task SetUserOnlineAsync(string userPubKey)
        {
            var sessions = await GetOnlineSessionsAsync();
            var existing = sessions.FirstOrDefault(s => s.UserPubKey == userPubKey);
            if (existing != null)
            {
                existing.LastHeartbeat = DateTime.UtcNow;
            }
            else
            {
                sessions.Add(new OnlineSession { UserPubKey = userPubKey });
            }
            await File.WriteAllTextAsync(_onlineSessionsFilePath, JsonConvert.SerializeObject(sessions, Formatting.Indented));
        }

        public async Task SetUserOfflineAsync(string userPubKey)
        {
            var sessions = await GetOnlineSessionsAsync();
            sessions.RemoveAll(s => s.UserPubKey == userPubKey);
            await File.WriteAllTextAsync(_onlineSessionsFilePath, JsonConvert.SerializeObject(sessions, Formatting.Indented));
        }

        public async Task<bool> IsUserOnlineAsync(string userPubKey)
        {
            var sessions = await GetOnlineSessionsAsync();
            var session = sessions.FirstOrDefault(s => s.UserPubKey == userPubKey);
            if (session == null) return false;
            // Consider offline if no heartbeat in 60 seconds
            return (DateTime.UtcNow - session.LastHeartbeat).TotalSeconds < 60;
        }

        public async Task CleanupStaleSessionsAsync()
        {
            var sessions = await GetOnlineSessionsAsync();
            var staleThreshold = DateTime.UtcNow.AddSeconds(-60);
            var before = sessions.Count;
            sessions.RemoveAll(s => s.LastHeartbeat < staleThreshold);
            if (sessions.Count != before)
            {
                await File.WriteAllTextAsync(_onlineSessionsFilePath, JsonConvert.SerializeObject(sessions, Formatting.Indented));
            }
        }

        public async Task<List<SignalingMessage>> GetSignalingMessagesAsync()
        {
            if (!File.Exists(_signalingMessagesFilePath))
                await File.WriteAllTextAsync(_signalingMessagesFilePath, "[]");
            var json = await File.ReadAllTextAsync(_signalingMessagesFilePath);
            return JsonConvert.DeserializeObject<List<SignalingMessage>>(json) ?? new List<SignalingMessage>();
        }

        public async Task SaveSignalingMessageAsync(SignalingMessage message)
        {
            var messages = await GetSignalingMessagesAsync();
            messages.Add(message);
            await File.WriteAllTextAsync(_signalingMessagesFilePath, JsonConvert.SerializeObject(messages, Formatting.Indented));
        }

        public async Task<List<SignalingMessage>> GetPendingSignalingMessagesAsync(string toPubKey)
        {
            var messages = await GetSignalingMessagesAsync();
            var pending = messages
                .Where(m => m.ToPubKey == toPubKey && !m.IsConsumed)
                .OrderBy(m => m.CreatedAt)
                .ToList();

            // Mark as consumed
            if (pending.Any())
            {
                foreach (var msg in messages.Where(m => m.ToPubKey == toPubKey && !m.IsConsumed))
                {
                    msg.IsConsumed = true;
                }
                await File.WriteAllTextAsync(_signalingMessagesFilePath, JsonConvert.SerializeObject(messages, Formatting.Indented));
            }

            return pending;
        }

        public async Task CleanupOldSignalingMessagesAsync()
        {
            var messages = await GetSignalingMessagesAsync();
            var threshold = DateTime.UtcNow.AddMinutes(-5);
            var before = messages.Count;
            messages.RemoveAll(m => m.CreatedAt < threshold);
            if (messages.Count != before)
            {
                await File.WriteAllTextAsync(_signalingMessagesFilePath, JsonConvert.SerializeObject(messages, Formatting.Indented));
            }
        }
    }
} 
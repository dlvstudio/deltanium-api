using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeltaniumApi.Data;
using DeltaniumApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DeltaniumApi.Services
{
    public class JsonImportService
    {
        private readonly ApiDbContext _db;
        private readonly ILogger<JsonImportService> _logger;

        public JsonImportService(ApiDbContext db, ILogger<JsonImportService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task ImportFromDirectoryAsync(string directory)
        {
            directory = string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory;
            _logger.LogInformation("Importing JSON storage from {Directory}", directory);

            await ImportAsync(Path.Combine(directory, "users.json"),
                () => ReadList<User>(Path.Combine(directory, "users.json")),
                async users =>
                {
                    foreach (var user in users)
                    {
                        if (!await _db.Users.AnyAsync(u => u.Id == user.Id || u.PublicKey == user.PublicKey))
                            _db.Users.Add(user);
                    }
                });

            await ImportAsync(Path.Combine(directory, "nodes.json"),
                () => ReadList<StoreNode>(Path.Combine(directory, "nodes.json")),
                async nodes =>
                {
                    foreach (var node in nodes)
                    {
                        if (!await _db.StoreNodes.AnyAsync(n => n.Id == node.Id || n.PublicKey == node.PublicKey))
                            _db.StoreNodes.Add(node);
                    }
                });

            await ImportAsync(Path.Combine(directory, "blockcreator_nodes.json"),
                () => ReadList<BlockCreatorNode>(Path.Combine(directory, "blockcreator_nodes.json")),
                async nodes =>
                {
                    foreach (var node in nodes)
                    {
                        if (!await _db.BlockCreatorNodes.AnyAsync(n => n.Id == node.Id || n.PublicKey == node.PublicKey))
                            _db.BlockCreatorNodes.Add(node);
                    }
                });

            await ImportAsync(Path.Combine(directory, "stored_files.json"),
                () => ReadList<StoredFile>(Path.Combine(directory, "stored_files.json")),
                async files =>
                {
                    foreach (var file in files)
                    {
                        if (!await _db.StoredFiles.AnyAsync(f => f.Id == file.Id))
                            _db.StoredFiles.Add(file);
                    }
                });

            await ImportAsync(Path.Combine(directory, "vouchers.json"),
                () => ReadList<Voucher>(Path.Combine(directory, "vouchers.json")),
                async vouchers =>
                {
                    foreach (var voucher in vouchers)
                    {
                        if (!await _db.Vouchers.AnyAsync(v => v.Code == voucher.Code))
                            _db.Vouchers.Add(voucher);
                    }
                });

            await ImportAsync(Path.Combine(directory, "user_follows.json"),
                () => ReadList<UserFollow>(Path.Combine(directory, "user_follows.json")),
                async follows =>
                {
                    foreach (var follow in follows)
                    {
                        if (!await _db.UserFollows.AnyAsync(f =>
                                f.Id == follow.Id ||
                                (f.FollowerPublicKey == follow.FollowerPublicKey &&
                                 f.FollowingPublicKey == follow.FollowingPublicKey)))
                            _db.UserFollows.Add(follow);
                    }
                });

            await ImportAsync(Path.Combine(directory, "user_rekeys.json"),
                () => ReadList<UserRekey>(Path.Combine(directory, "user_rekeys.json")),
                async rekeys =>
                {
                    foreach (var rekey in rekeys)
                    {
                        if (!await _db.UserRekeys.AnyAsync(r => r.Id == rekey.Id))
                            _db.UserRekeys.Add(rekey);
                    }
                });

            await ImportAsync(Path.Combine(directory, "conversations.json"),
                () => ReadList<Conversation>(Path.Combine(directory, "conversations.json")),
                async conversations =>
                {
                    foreach (var conversation in conversations)
                    {
                        if (!await _db.Conversations.AnyAsync(c => c.ConversationId == conversation.ConversationId))
                            _db.Conversations.Add(conversation);
                    }
                });

            await ImportAsync(Path.Combine(directory, "chat_messages.json"),
                () => ReadList<ChatMessageMetadata>(Path.Combine(directory, "chat_messages.json")),
                async messages =>
                {
                    foreach (var message in messages)
                    {
                        if (!await _db.ChatMessages.AnyAsync(m => m.MessageId == message.MessageId))
                            _db.ChatMessages.Add(message);
                    }
                });

            await ImportAsync(Path.Combine(directory, "online_sessions.json"),
                () => ReadList<OnlineSession>(Path.Combine(directory, "online_sessions.json")),
                async sessions =>
                {
                    foreach (var session in sessions)
                    {
                        if (!await _db.OnlineSessions.AnyAsync(s => s.UserPubKey == session.UserPubKey))
                            _db.OnlineSessions.Add(session);
                    }
                });

            await ImportAsync(Path.Combine(directory, "signaling_messages.json"),
                () => ReadList<SignalingMessage>(Path.Combine(directory, "signaling_messages.json")),
                async messages =>
                {
                    foreach (var message in messages)
                    {
                        if (!await _db.SignalingMessages.AnyAsync(m => m.Id == message.Id))
                            _db.SignalingMessages.Add(message);
                    }
                });

            var txPath = Path.Combine(directory, "transactions.json");
            if (File.Exists(txPath))
            {
                var envelopes = ReadList<TransactionEnvelope>(txPath);
                _logger.LogInformation("Importing {Count} records from transactions.json", envelopes.Count);
                foreach (var envelope in envelopes)
                {
                    var record = TransactionEnvelopeRecord.FromModel(envelope);
                    if (!await _db.TransactionEnvelopes.AnyAsync(t => t.Id == record.Id))
                        _db.TransactionEnvelopes.Add(record);
                }
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("JSON import completed");
        }

        private static List<T> ReadList<T>(string path) =>
            JsonConvert.DeserializeObject<List<T>>(File.ReadAllText(path)) ?? new List<T>();

        private async Task ImportAsync<T>(string path, Func<List<T>> loader, Func<List<T>, Task> apply)
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("Skip missing file {Path}", path);
                return;
            }

            var items = loader();
            _logger.LogInformation("Importing {Count} records from {Path}", items.Count, Path.GetFileName(path));
            await apply(items);
        }
    }
}

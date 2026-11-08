using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly KeyService _keyService;
        private readonly IStorageService _storageService;
        private readonly SignatureAuthHelper _auth;

        public ChatController(
            KeyService keyService,
            IStorageService storageService,
            SignatureAuthHelper auth)
        {
            _keyService = keyService;
            _storageService = storageService;
            _auth = auth;
        }

        [HttpPost("conversation")]
        public async Task<ActionResult> CreateOrGetConversation()
        {
            var (ok, callerPk, err, body) = await AuthWithBodyAsync();
            if (!ok) return Unauthorized(err);

            try
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                var recipientPubKey = json != null && json.TryGetValue("recipientPubKey", out var rp)
                    ? rp.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(recipientPubKey))
                    return BadRequest("recipientPubKey is required");

                if (!_keyService.ValidatePublicKey(recipientPubKey))
                    return BadRequest("Invalid recipient public key");

                if (PubKeysMatch(callerPk!, recipientPubKey))
                    return BadRequest("Cannot create a conversation with yourself");

                if (!await _storageService.IsPublicKeyRegisteredAsync(recipientPubKey))
                    return BadRequest("Recipient is not registered");

                var conversation = await _storageService.CreateOrGetConversationAsync(callerPk!, recipientPubKey);
                return Ok(conversation);
            }
            catch (Exception ex)
            {
                return BadRequest($"Invalid request: {ex.Message}");
            }
        }

        [HttpGet("conversations")]
        public async Task<ActionResult> GetConversations()
        {
            var (ok, callerPk, err) = await AuthAsync();
            if (!ok) return Unauthorized(err);

            var conversations = await _storageService.GetUserConversationsAsync(callerPk!);
            var users = await _storageService.GetUsersAsync();
            var allMessages = await _storageService.GetChatMessagesAsync();

            var result = new List<object>();
            foreach (var conv in conversations)
            {
                var messages = await _storageService.GetConversationMessagesAsync(conv.ConversationId, 0, 1);
                var lastMessage = messages.FirstOrDefault();
                var unreadCount = allMessages.Count(m =>
                    m.ConversationId == conv.ConversationId &&
                    PubKeysMatch(m.RecipientPubKey, callerPk!) &&
                    !m.IsRead);

                var otherParticipant = conv.Participants.FirstOrDefault(p => !PubKeysMatch(p, callerPk!)) ?? "";
                var otherUser = users.FirstOrDefault(u => PubKeysMatch(u.PublicKey, otherParticipant));

                result.Add(new
                {
                    conv.ConversationId,
                    conv.Participants,
                    conv.CreatedAt,
                    conv.LastMessageAt,
                    otherParticipantPubKey = otherParticipant,
                    otherParticipantName = otherUser?.FullName ?? "",
                    otherParticipantAvatar = otherUser?.AvatarUrl ?? "",
                    unreadCount,
                    lastMessage = lastMessage != null ? new
                    {
                        lastMessage.MessageId,
                        lastMessage.SenderPubKey,
                        lastMessage.CreatedAt,
                        lastMessage.BlockId,
                        lastMessage.StoreNodeId
                    } : null
                });
            }

            return Ok(result);
        }

        [HttpPost("message")]
        public async Task<ActionResult> RegisterMessage()
        {
            var (ok, callerPk, err, body) = await AuthWithBodyAsync();
            if (!ok) return Unauthorized(err);

            try
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
                var conversationId = json["conversationId"].GetString();
                var recipientPubKey = json["recipientPubKey"].GetString();
                var storeNodeId = json.ContainsKey("storeNodeId") ? json["storeNodeId"].GetString() : "";
                var blockId = json.ContainsKey("blockId") ? json["blockId"].GetString() : "";
                var fileId = json.ContainsKey("fileId") ? json["fileId"].GetString() : "";
                var recipientFileId = json.ContainsKey("recipientFileId") ? json["recipientFileId"].GetString() : "";
                var clientMessageId = json.ContainsKey("clientMessageId") ? json["clientMessageId"].GetString() : "";

                if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(recipientPubKey))
                    return BadRequest("conversationId and recipientPubKey are required");

                var conv = await _storageService.GetConversationByIdAsync(conversationId);
                if (conv == null)
                    return NotFound("Conversation not found");

                if (!IsParticipant(conv, callerPk!))
                    return Forbid();

                if (!IsParticipant(conv, recipientPubKey))
                    return BadRequest("recipientPubKey is not a participant of this conversation");

                if (PubKeysMatch(callerPk!, recipientPubKey))
                    return BadRequest("Cannot send a message to yourself");

                var message = new ChatMessageMetadata
                {
                    ConversationId = conversationId,
                    ClientMessageId = clientMessageId ?? "",
                    SenderPubKey = callerPk!,
                    RecipientPubKey = recipientPubKey,
                    StoreNodeId = storeNodeId ?? "",
                    BlockId = blockId ?? "",
                    FileId = fileId ?? "",
                    RecipientFileId = recipientFileId ?? ""
                };

                await _storageService.SaveChatMessageAsync(message);
                await _storageService.UpdateConversationLastMessageAsync(conversationId);

                return Ok(new
                {
                    message.MessageId,
                    message.ConversationId,
                    message.SenderPubKey,
                    message.RecipientPubKey,
                    message.CreatedAt
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Invalid request: {ex.Message}");
            }
        }

        [HttpGet("messages/{conversationId}")]
        public async Task<ActionResult> GetMessages(
            string conversationId,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 50)
        {
            var (ok, callerPk, err) = await AuthAsync();
            if (!ok) return Unauthorized(err);

            var conv = await _storageService.GetConversationByIdAsync(conversationId);
            if (conv == null)
                return NotFound("Conversation not found");

            if (!IsParticipant(conv, callerPk!))
                return Forbid();

            if (pageSize <= 0) pageSize = 50;
            if (pageSize > 200) pageSize = 200;
            if (page < 0) page = 0;

            var messages = await _storageService.GetConversationMessagesAsync(conversationId, page, pageSize);
            await _storageService.MarkMessagesAsReadAsync(conversationId, callerPk!);
            return Ok(messages);
        }

        [HttpGet("unread-count")]
        public async Task<ActionResult> GetUnreadCount()
        {
            var (ok, callerPk, err) = await AuthAsync();
            if (!ok) return Unauthorized(err);

            var count = await _storageService.GetUnreadCountForUserAsync(callerPk!);
            return Ok(new { unreadCount = count });
        }

        [HttpPost("online")]
        public async Task<ActionResult> SetOnline()
        {
            var (ok, callerPk, err, _) = await AuthWithBodyAsync();
            if (!ok) return Unauthorized(err);

            await _storageService.CleanupStaleSessionsAsync();
            await _storageService.SetUserOnlineAsync(callerPk!);
            await _storageService.CleanupOldSignalingMessagesAsync();
            return Ok(new { status = "online" });
        }

        [HttpDelete("online")]
        public async Task<ActionResult> SetOffline()
        {
            var (ok, callerPk, err) = await AuthAsync();
            if (!ok) return Unauthorized(err);

            await _storageService.SetUserOfflineAsync(callerPk!);
            return Ok(new { status = "offline" });
        }

        [HttpGet("online-status")]
        public async Task<ActionResult> GetOnlineStatus([FromQuery] string user)
        {
            var (ok, _, err) = await AuthAsync();
            if (!ok) return Unauthorized(err);

            if (string.IsNullOrWhiteSpace(user))
                return BadRequest("user parameter is required");

            var isOnline = await _storageService.IsUserOnlineAsync(user);
            return Ok(new { userPubKey = user, online = isOnline });
        }

        [HttpPost("signaling/{type}")]
        public async Task<ActionResult> SendSignaling(string type)
        {
            var (ok, callerPk, err, body) = await AuthWithBodyAsync();
            if (!ok) return Unauthorized(err);

            if (type != "offer" && type != "answer" && type != "ice")
                return BadRequest("Invalid signaling type. Use: offer, answer, ice");

            try
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
                var toPubKey = json["toPubKey"].GetString();
                var payload = json["payload"].GetString();

                if (string.IsNullOrWhiteSpace(toPubKey) || string.IsNullOrWhiteSpace(payload))
                    return BadRequest("toPubKey and payload are required");

                if (!_keyService.ValidatePublicKey(toPubKey))
                    return BadRequest("Invalid toPubKey");

                if (PubKeysMatch(callerPk!, toPubKey))
                    return BadRequest("Cannot signal yourself");

                var isTargetOnline = await _storageService.IsUserOnlineAsync(toPubKey);
                if (!isTargetOnline)
                    return Ok(new { sent = false, reason = "Target user is offline" });

                var message = new SignalingMessage
                {
                    FromPubKey = callerPk!,
                    ToPubKey = toPubKey,
                    Type = type,
                    Payload = payload
                };

                await _storageService.SaveSignalingMessageAsync(message);
                return Ok(new { sent = true, messageId = message.Id });
            }
            catch (Exception ex)
            {
                return BadRequest($"Invalid request: {ex.Message}");
            }
        }

        [HttpGet("signaling/poll")]
        public async Task<ActionResult> PollSignaling()
        {
            var (ok, callerPk, err) = await AuthAsync();
            if (!ok) return Unauthorized(err);

            var messages = await _storageService.GetPendingSignalingMessagesAsync(callerPk!);
            return Ok(messages.Select(m => new
            {
                m.Id,
                m.FromPubKey,
                m.Type,
                m.Payload,
                m.CreatedAt
            }));
        }

        private async Task<(bool Ok, string? PublicKey, string Error)> AuthAsync()
        {
            var (ok, pk, err) = await _auth.ValidateAsync(Request, "", requireRegisteredUser: true);
            return (ok, pk, err);
        }

        private async Task<(bool Ok, string? PublicKey, string Error, string Body)> AuthWithBodyAsync()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            var (ok, pk, err) = await _auth.ValidateAsync(Request, body, requireRegisteredUser: true);
            return (ok, pk, err, body);
        }

        private static bool IsParticipant(Conversation conv, string publicKey) =>
            conv.Participants.Any(p => PubKeysMatch(p, publicKey));

        private static bool PubKeysMatch(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                return string.Equals(
                    SignatureAuthHelper.NormalizePubKey(a),
                    SignatureAuthHelper.NormalizePubKey(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}

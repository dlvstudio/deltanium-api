using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

        public ChatController(KeyService keyService, IStorageService storageService)
        {
            _keyService = keyService;
            _storageService = storageService;
        }

        // ========== CONVERSATION ENDPOINTS ==========

        /// <summary>
        /// Create or get conversation between two users
        /// </summary>
        [HttpPost("conversation")]
        public async Task<ActionResult> CreateOrGetConversation([FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            string requestBodyJson = "";
            try
            {
                Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body);
                requestBodyJson = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
                HttpContext.Items["RequestBody"] = requestBodyJson;
            }
            catch (Exception ex)
            {
                return BadRequest($"Cannot read request body: {ex.Message}");
            }

            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            try
            {
                var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBodyJson);
                var recipientPubKey = body["recipientPubKey"].GetString();

                if (string.IsNullOrEmpty(recipientPubKey))
                    return BadRequest("recipientPubKey is required");

                var conversation = await _storageService.CreateOrGetConversationAsync(userPubKey, recipientPubKey);
                return Ok(conversation);
            }
            catch (Exception ex)
            {
                return BadRequest($"Invalid request: {ex.Message}");
            }
        }

        /// <summary>
        /// Get all conversations for current user
        /// </summary>
        [HttpGet("conversations")]
        public async Task<ActionResult> GetConversations([FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            var conversations = await _storageService.GetUserConversationsAsync(userPubKey);

            // Enrich with last message and unread count
            var result = new List<object>();
            foreach (var conv in conversations)
            {
                var messages = await _storageService.GetConversationMessagesAsync(conv.ConversationId, 0, 1);
                var lastMessage = messages.FirstOrDefault();

                var allMessages = await _storageService.GetChatMessagesAsync();
                var unreadCount = allMessages.Count(m =>
                    m.ConversationId == conv.ConversationId &&
                    m.RecipientPubKey == userPubKey &&
                    !m.IsRead);

                var otherParticipant = conv.Participants.FirstOrDefault(p => p != userPubKey) ?? "";

                // Get other user's profile
                var users = await _storageService.GetUsersAsync();
                var otherUser = users.FirstOrDefault(u => u.PublicKey == otherParticipant);

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

        // ========== MESSAGE ENDPOINTS (Offline file-based) ==========

        /// <summary>
        /// Register a chat message (metadata only, actual content is on Store)
        /// </summary>
        [HttpPost("message")]
        public async Task<ActionResult> RegisterMessage([FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            string requestBodyJson = "";
            try
            {
                Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body);
                requestBodyJson = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
                HttpContext.Items["RequestBody"] = requestBodyJson;
            }
            catch (Exception ex)
            {
                return BadRequest($"Cannot read request body: {ex.Message}");
            }

            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            try
            {
                var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBodyJson)!;
                var conversationId = body["conversationId"].GetString();
                var recipientPubKey = body["recipientPubKey"].GetString();
                var storeNodeId = body.ContainsKey("storeNodeId") ? body["storeNodeId"].GetString() : "";
                var blockId = body.ContainsKey("blockId") ? body["blockId"].GetString() : "";
                var fileId = body.ContainsKey("fileId") ? body["fileId"].GetString() : "";
                var recipientFileId = body.ContainsKey("recipientFileId") ? body["recipientFileId"].GetString() : "";
                var clientMessageId = body.ContainsKey("clientMessageId") ? body["clientMessageId"].GetString() : "";

                if (string.IsNullOrEmpty(conversationId) || string.IsNullOrEmpty(recipientPubKey))
                    return BadRequest("conversationId and recipientPubKey are required");

                var message = new ChatMessageMetadata
                {
                    ConversationId = conversationId,
                    ClientMessageId = clientMessageId ?? "",
                    SenderPubKey = userPubKey,
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

        /// <summary>
        /// Get messages for a conversation (paginated)
        /// </summary>
        [HttpGet("messages/{conversationId}")]
        public async Task<ActionResult> GetMessages(
            string conversationId,
            [FromHeader(Name = "X-User-PubKey")] string userPubKey,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 50)
        {
            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            var messages = await _storageService.GetConversationMessagesAsync(conversationId, page, pageSize);

            // Mark messages as read
            await _storageService.MarkMessagesAsReadAsync(conversationId, userPubKey);

            return Ok(messages);
        }

        /// <summary>
        /// Get unread message count
        /// </summary>
        [HttpGet("unread-count")]
        public async Task<ActionResult> GetUnreadCount([FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            var count = await _storageService.GetUnreadCountForUserAsync(userPubKey);
            return Ok(new { unreadCount = count });
        }

        // ========== PRESENCE ENDPOINTS (Online/Offline) ==========

        /// <summary>
        /// Set user online (heartbeat every 30s)
        /// </summary>
        [HttpPost("online")]
        public async Task<ActionResult> SetOnline([FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            // Buffer request body so ValidateAuthentication can compute body hash
            try
            {
                Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body);
                var requestBodyJson = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
                HttpContext.Items["RequestBody"] = requestBodyJson;
            }
            catch { /* ignore */ }

            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            await _storageService.CleanupStaleSessionsAsync();
            await _storageService.SetUserOnlineAsync(userPubKey);
            await _storageService.CleanupOldSignalingMessagesAsync();

            // Don't consume signaling messages here - let PollSignaling handle them
            // to avoid race conditions with WebRTC signaling poll
            return Ok(new
            {
                status = "online"
            });
        }

        /// <summary>
        /// Set user offline
        /// </summary>
        [HttpDelete("online")]
        public async Task<ActionResult> SetOffline([FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            await _storageService.SetUserOfflineAsync(userPubKey);
            return Ok(new { status = "offline" });
        }

        /// <summary>
        /// Check if a user is online
        /// </summary>
        [HttpGet("online-status")]
        public async Task<ActionResult> GetOnlineStatus(
            [FromHeader(Name = "X-User-PubKey")] string userPubKey,
            [FromQuery] string user)
        {
            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            if (string.IsNullOrEmpty(user))
                return BadRequest("user parameter is required");

            var isOnline = await _storageService.IsUserOnlineAsync(user);
            return Ok(new { userPubKey = user, online = isOnline });
        }

        // ========== SIGNALING ENDPOINTS (WebRTC) ==========

        /// <summary>
        /// Send WebRTC signaling message (offer, answer, or ICE candidate)
        /// </summary>
        [HttpPost("signaling/{type}")]
        public async Task<ActionResult> SendSignaling(
            string type,
            [FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            string requestBodyJson = "";
            try
            {
                Request.EnableBuffering();
                using var reader = new StreamReader(Request.Body);
                requestBodyJson = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
                HttpContext.Items["RequestBody"] = requestBodyJson;
            }
            catch (Exception ex)
            {
                return BadRequest($"Cannot read request body: {ex.Message}");
            }

            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            if (type != "offer" && type != "answer" && type != "ice")
                return BadRequest("Invalid signaling type. Use: offer, answer, ice");

            try
            {
                var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBodyJson)!;
                var toPubKey = body["toPubKey"].GetString();
                var payload = body["payload"].GetString();

                if (string.IsNullOrEmpty(toPubKey) || string.IsNullOrEmpty(payload))
                    return BadRequest("toPubKey and payload are required");

                // Check if target user is online
                var isTargetOnline = await _storageService.IsUserOnlineAsync(toPubKey);
                if (!isTargetOnline)
                    return Ok(new { sent = false, reason = "Target user is offline" });

                var message = new SignalingMessage
                {
                    FromPubKey = userPubKey,
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

        /// <summary>
        /// Poll for pending signaling messages
        /// </summary>
        [HttpGet("signaling/poll")]
        public async Task<ActionResult> PollSignaling([FromHeader(Name = "X-User-PubKey")] string userPubKey)
        {
            if (!await ValidateAuthentication())
                return Unauthorized("Invalid authentication");

            var messages = await _storageService.GetPendingSignalingMessagesAsync(userPubKey);
            return Ok(messages.Select(m => new
            {
                m.Id,
                m.FromPubKey,
                m.Type,
                m.Payload,
                m.CreatedAt
            }));
        }

        // ========== AUTH HELPER (same pattern as UserController) ==========

        private async Task<bool> ValidateAuthentication()
        {
            if (!Request.Headers.TryGetValue("X-User-PubKey", out var publicKeyHeader) ||
                !Request.Headers.TryGetValue("X-Timestamp", out var timestampHeader) ||
                !Request.Headers.TryGetValue("X-Signature", out var signatureHeader))
            {
                return false;
            }

            var publicKey = publicKeyHeader.FirstOrDefault();
            var timestamp = timestampHeader.FirstOrDefault();
            var signature = signatureHeader.FirstOrDefault();

            if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
                return false;

            // Read request body for POST requests
            string requestBody = "";
            if (Request.Method == "POST")
            {
                if (HttpContext.Items.TryGetValue("RequestBody", out var cachedBody))
                {
                    requestBody = cachedBody?.ToString() ?? "";
                }
            }

            // Check if user is registered
            if (!await _storageService.IsPublicKeyRegisteredAsync(publicKey))
                return false;

            // Validate timestamp
            if (!long.TryParse(timestamp, out var timestampLong))
                return false;

            var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestampLong);
            var now = DateTimeOffset.UtcNow;
            var timeDiff = Math.Abs((now - requestTime).TotalMinutes);
            if (timeDiff > 5)
                return false;

            // Build signature data
            var method = Request.Method;
            var path = Request.Path + Request.QueryString;
            var bodyHash = "";

            if (Request.Method == "POST")
            {
                using var sha256 = SHA256.Create();
                var bodyBytes = Encoding.UTF8.GetBytes(requestBody);
                var hashBytes = sha256.ComputeHash(bodyBytes);
                bodyHash = Convert.ToHexString(hashBytes).ToLower();
            }

            var dataToSign = $"{method}{path}{timestamp}{bodyHash}";
            return _keyService.VerifySignature(publicKey, dataToSign, signature);
        }
    }
}

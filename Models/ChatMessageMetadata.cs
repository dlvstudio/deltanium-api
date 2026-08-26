using System;

namespace DeltaniumApi.Models
{
    public class ChatMessageMetadata
    {
        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public string ClientMessageId { get; set; } = string.Empty; // Client-generated UUID for P2P dedup
        public string ConversationId { get; set; } = string.Empty;
        public string SenderPubKey { get; set; } = string.Empty;
        public string RecipientPubKey { get; set; } = string.Empty;
        public string StoreNodeId { get; set; } = string.Empty;
        public string BlockId { get; set; } = string.Empty;
        public string FileId { get; set; } = string.Empty;
        public string RecipientFileId { get; set; } = string.Empty; // FileId for the recipient's metadata entry on Store
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsRead { get; set; } = false;
    }
}

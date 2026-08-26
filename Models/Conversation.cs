using System;
using System.Collections.Generic;

namespace DeltaniumApi.Models
{
    public class Conversation
    {
        public string ConversationId { get; set; } = Guid.NewGuid().ToString();
        public List<string> Participants { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    }
}

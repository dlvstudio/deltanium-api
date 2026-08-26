using System;

namespace DeltaniumApi.Models
{
    public class OnlineSession
    {
        public string UserPubKey { get; set; } = string.Empty;
        public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
        public string? SignalingToken { get; set; }
    }

    public class SignalingMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FromPubKey { get; set; } = string.Empty;
        public string ToPubKey { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // "offer", "answer", "ice"
        public string Payload { get; set; } = string.Empty; // SDP or ICE candidate JSON
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsConsumed { get; set; } = false;
    }
}

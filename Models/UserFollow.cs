using System;

namespace DeltaniumApi.Models
{
    public class UserFollow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FollowerPublicKey { get; set; } // User who follows
        public string FollowingPublicKey { get; set; } // User being followed
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public long Timestamp { get; set; } // Unix timestamp for blockchain (signed by user)
        public string? Signature { get; set; } // Signature of the follow action for blockchain verification
    }
} 
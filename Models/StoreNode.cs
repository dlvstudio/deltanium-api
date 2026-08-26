using System;

namespace DeltaniumApi.Models
{
    public class StoreNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PublicKey { get; set; }
        public string Endpoint { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }
} 
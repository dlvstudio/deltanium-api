using System;

namespace DeltaniumApi.Models
{
    public class BlockCreatorNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PublicKey { get; set; }
        /// <summary>HTTPS endpoint; empty for a TCP-only node.</summary>
        public string Endpoint { get; set; }
        /// <summary>DLTP endpoint (tcps://host:port); null when the node has no TCP endpoint.</summary>
        public string? TcpEndpoint { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public long StakeAmount { get; set; } = 0; // D coins staked
    }
}












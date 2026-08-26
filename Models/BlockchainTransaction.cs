using System;
using System.Collections.Generic;

namespace DeltaniumApi.Models
{
    public class BlockchainTransaction
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Type { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string? To { get; set; }
        public Dictionary<string, object> Data { get; set; } = new();
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public long Fee { get; set; }
        public string Signature { get; set; } = string.Empty;
    }
}

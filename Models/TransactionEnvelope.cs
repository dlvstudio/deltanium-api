using System;

namespace DeltaniumApi.Models
{
    public class TransactionEnvelope
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public BlockchainTransaction Transaction { get; set; } = new();
        public bool Delivered { get; set; }
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeliveredAt { get; set; }
    }
}

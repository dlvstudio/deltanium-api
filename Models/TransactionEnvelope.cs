using System;

namespace DeltaniumApi.Models
{
    public class TransactionEnvelope
    {
        public BlockchainTransaction Transaction { get; set; } = new();
        public bool Delivered { get; set; }
        public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeliveredAt { get; set; }
    }
}

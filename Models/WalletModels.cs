using System;

namespace DeltaniumApi.Models
{
    public class WalletAccount
    {
        public string PublicKey { get; set; } = "";
        public decimal Balance { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class WalletLedgerEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string? FromPublicKey { get; set; }
        public string? ToPublicKey { get; set; }
        public decimal Amount { get; set; }
        /// <summary>topup | transfer | fee | genesis | adjustment</summary>
        public string Kind { get; set; } = "transfer";
        public string? RefId { get; set; }
        public string? Memo { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TopUpOrder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string BuyerPublicKey { get; set; } = "";
        public decimal FiatAmountVnd { get; set; }
        public decimal FeePercent { get; set; }
        public decimal DCredited { get; set; }
        public decimal PlatformFiatVnd { get; set; }
        /// <summary>
        /// v2: fiat moved into the segregated backing reserve (= D credited × VndPerD).
        /// </summary>
        public decimal NetworkReserveFiatVnd { get; set; }
        /// <summary>transfer | card | iap (TOKENOMICS_V2 §3).</summary>
        public string Channel { get; set; } = "transfer";
        /// <summary>pending | completed | cancelled</summary>
        public string Status { get; set; } = "pending";
        /// <summary>Network TopUp tx id (equals Id when submitted to blockers).</summary>
        public string? NetworkTxId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }
}

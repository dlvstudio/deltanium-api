using System;
using System.Collections.Generic;

namespace DeltaniumApi.Models
{
    /// <summary>Well-known system ledger accounts (TOKENOMICS_V2 §2).</summary>
    public static class EconAccounts
    {
        public const string NetworkFund = "treasury:network-fund";
        public const string ProtocolOps = "treasury:protocol-ops";
        public const string RedemptionHold = "treasury:redemption-hold";
        public const string BlockerFeePool = "pool:blocker-fees";
        public static string Escrow(string contractId) => "escrow:" + contractId;
        public static string Bond(string nodePublicKey) => "bond:" + nodePublicKey;

        public static bool IsSystem(string account) =>
            account.StartsWith("treasury:", StringComparison.Ordinal) ||
            account.StartsWith("pool:", StringComparison.Ordinal) ||
            account.StartsWith("escrow:", StringComparison.Ordinal) ||
            account.StartsWith("bond:", StringComparison.Ordinal);
    }

    public static class FeeTypes
    {
        public const string Storage = "storage";
        public const string Bandwidth = "bandwidth";
        public const string Tx = "tx";
        public const string Creator = "creator";
    }

    /// <summary>One leg of an atomic multi-leg ledger movement.</summary>
    public record LedgerMove(string? From, string To, decimal Amount, string Kind, string? Memo = null);

    /// <summary>Generic JSON document row for economics state (escrows, proofs, subscriptions, …).</summary>
    public class EconDoc
    {
        public string Collection { get; set; } = "";
        public string Id { get; set; } = "";
        public string Json { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class FileBlockHash
    {
        public string BlockId { get; set; } = "";
        /// <summary>Lowercase hex SHA-256 of the exact bytes uploaded to the Store.</summary>
        public string Sha256 { get; set; } = "";
        /// <summary>Block index inside the file (needed to re-replicate the block onto another Store). -1 = unknown.</summary>
        public int BlockIndex { get; set; } = -1;
    }

    /// <summary>Storage escrow for one file (all replicas). §6.</summary>
    public class StorageEscrow
    {
        public string Id { get; set; } = "";            // = fileId
        public string OwnerPublicKey { get; set; } = "";
        /// <summary>Who funded the escrow: owner or the network fund (free tier).</summary>
        public string PayerAccount { get; set; } = "";
        public bool FreeTier { get; set; }
        /// <summary>Reaction, comment or chat message (§10: free; unfunded once the free quota is used up).</summary>
        public bool Social { get; set; }
        /// <summary>TimeFixed | AutoRenew</summary>
        public string ContractType { get; set; } = "TimeFixed";
        public long SizeBytes { get; set; }
        public List<string> StoreNodeIds { get; set; } = new();
        public int Months { get; set; }
        public decimal MonthlyFee { get; set; }
        public decimal TotalFunded { get; set; }
        public decimal Released { get; set; }
        public int MonthsSettled { get; set; }
        public DateTime StartAt { get; set; } = DateTime.UtcNow;
        /// <summary>Active | Grace | Expired | Lost | Closed</summary>
        public string Status { get; set; } = "Active";
        public DateTime? GraceUntil { get; set; }
        public List<string> FailedNodeIds { get; set; } = new();
        /// <summary>
        /// Stores that have returned a registered block with the right hash at least once. The block hashes come
        /// from the uploader, so a Store that never passed a challenge may simply have been given hashes that
        /// do not match what it received: it is dropped from the contract without being slashed.
        /// </summary>
        public List<string> VerifiedNodeIds { get; set; } = new();
        public List<FileBlockHash> Blocks { get; set; } = new();
        /// <summary>Set when the uploader's monthly egress cap was hit (§7): serve subscribers only.</summary>
        public bool EgressCapped { get; set; }
        /// <summary>Replica count the owner paid for; repair keeps this many healthy copies (§6).</summary>
        public int TargetReplicas { get; set; }
    }

    /// <summary>One automatic re-replication attempt (§6: replace a Store that failed proofs).</summary>
    public class ReplicationEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileId { get; set; } = "";
        public string? SourceNodeId { get; set; }
        public string TargetNodeId { get; set; } = "";
        public int BlocksCopied { get; set; }
        public int MetadataCopied { get; set; }
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public DateTime At { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Non-zero balances replayed from the ledger up to an anchor (basis of BalancesRoot).</summary>
    public class BalanceSnapshot
    {
        public string Id { get; set; } = "";
        public long Seq { get; set; }
        public Dictionary<string, decimal> Balances { get; set; } = new();
    }

    /// <summary>Merkle inclusion proof of one balance leaf in an anchor's BalancesRoot (§11 phase B).</summary>
    public class BalanceProof
    {
        public long Seq { get; set; }
        public string Account { get; set; } = "";
        public decimal Balance { get; set; }
        public bool Included { get; set; }
        public string Leaf { get; set; } = "";
        public int Index { get; set; } = -1;
        public List<MerkleStep> Path { get; set; } = new();
        public string Root { get; set; } = "";
        public DateTime UpToInclusive { get; set; }
    }

    public class MerkleStep
    {
        public string Hash { get; set; } = "";
        /// <summary>"L" when the sibling is on the left (hash = sha256(sibling + current)), "R" otherwise.</summary>
        public string Side { get; set; } = "R";
    }

    /// <summary>Reader-signed bandwidth receipt forwarded by a Store (§7).</summary>
    public class EgressReceipt
    {
        public string Reader { get; set; } = "";
        public string Origin { get; set; } = "";
        public long Ts { get; set; }
        public List<EgressReceiptItem> Items { get; set; } = new();
        public string Signature { get; set; } = "";
    }

    public class EgressReceiptItem
    {
        /// <summary>Block id, or "fileId/index" when the reader fetched by index.</summary>
        public string Block { get; set; } = "";
        public long Bytes { get; set; }
    }

    /// <summary>Per (period, file, node) proof-of-storage tally. §6.</summary>
    public class ProofTally
    {
        public string Id { get; set; } = "";
        public string Period { get; set; } = "";
        public string FileId { get; set; } = "";
        public string StoreNodeId { get; set; } = "";
        public int Passed { get; set; }
        public int Total { get; set; }
        public DateTime? LastAt { get; set; }
        public string? LastError { get; set; }
    }

    /// <summary>Per (period, file) egress usage. §7.</summary>
    public class EgressUsage
    {
        public string Id { get; set; } = "";
        public string Period { get; set; } = "";
        public string FileId { get; set; } = "";
        public long Bytes { get; set; }
        public long ChargedBytes { get; set; }
        public decimal ChargedD { get; set; }
        public decimal UnpaidD { get; set; }
    }

    public class EgressCap
    {
        public string Id { get; set; } = "";             // owner pubkey
        public decimal MaxDPerMonth { get; set; }
    }

    /// <summary>KYC-verified payee allowed to redeem D (§4). Set by ops.</summary>
    public class Payee
    {
        public string Id { get; set; } = "";             // pubkey
        public bool Verified { get; set; }
        /// <summary>node | creator</summary>
        public string Role { get; set; } = "creator";
        public string? BankRef { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class RedemptionRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string PublicKey { get; set; } = "";
        public decimal AmountD { get; set; }
        public decimal FeeD { get; set; }
        public decimal PayoutVnd { get; set; }
        /// <summary>pending | queued | paid | rejected</summary>
        public string Status { get; set; } = "pending";
        public string? BankRef { get; set; }
        public string? Note { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }

    public class BondWithdrawal
    {
        public string Id { get; set; } = "";             // node pubkey
        public decimal Amount { get; set; }
        public DateTime UnlockAt { get; set; }
        /// <summary>pending | done | cancelled</summary>
        public string Status { get; set; } = "pending";
    }

    public class SlashEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string NodePublicKey { get; set; } = "";
        public decimal Percent { get; set; }
        public decimal Amount { get; set; }
        public string Reason { get; set; } = "";
        public string? RefId { get; set; }
        public DateTime At { get; set; } = DateTime.UtcNow;
    }

    public class SubscriptionPlan
    {
        public string Id { get; set; } = "";             // creator pubkey
        public decimal PricePerMonthD { get; set; }
        public bool Active { get; set; } = true;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Subscription
    {
        public string Id { get; set; } = "";             // creator|subscriber
        public string CreatorPublicKey { get; set; } = "";
        public string SubscriberPublicKey { get; set; } = "";
        /// <summary>Last paid epoch, inclusive (YYYY-MM).</summary>
        public string PaidThroughEpoch { get; set; } = "";
        public decimal TotalPaidD { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Ledger anchor committed into a block (§11 phase A).</summary>
    public class LedgerAnchor
    {
        public string Id { get; set; } = "";             // seq as string, zero-padded
        public long Seq { get; set; }
        public DateTime FromExclusive { get; set; }
        public DateTime UpToInclusive { get; set; }
        public int EntryCount { get; set; }
        public string LedgerRoot { get; set; } = "";
        public string BalancesRoot { get; set; } = "";
        public string? BlockerPublicKey { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Accounts whose live balance differed from the ledger replay when anchoring (should be 0).</summary>
        public int LiveMismatchAccounts { get; set; }
    }

    public class StorageQuote
    {
        public long SizeBytes { get; set; }
        public int Replicas { get; set; }
        public int Months { get; set; }
        public decimal PricePerGbMonth { get; set; }
        public decimal MonthlyFee { get; set; }
        public decimal TotalFee { get; set; }
        public bool FreeTier { get; set; }
        public long FreeTierRemainingBytes { get; set; }
        public decimal EgressQuotaBytesPerMonth { get; set; }
    }
}

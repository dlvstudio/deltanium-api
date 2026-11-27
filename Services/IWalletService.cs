using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;

namespace DeltaniumApi.Services
{
    public interface IWalletService
    {
        string TreasuryKey { get; }
        decimal HardCap { get; }
        decimal VndPerD { get; }
        decimal DPerDlt { get; }
        decimal MinTopUpVnd { get; }
        bool EnforceFees { get; }
        decimal OnRampFeePercent(string channel);
        (decimal Node, decimal Fund, decimal Ops) FeeSplit(string feeType);

        Task EnsureReadyAsync();
        Task<decimal> GetBalanceAsync(string publicKey);
        Task<List<WalletAccount>> GetAllBalancesAsync();
        Task<List<WalletLedgerEntry>> GetLedgerAsync(string publicKey, int limit = 50);
        Task<List<WalletLedgerEntry>> GetLedgerRangeAsync(DateTime fromExclusive, DateTime upToInclusive);

        Task<(bool Ok, string Error)> TransferAsync(string fromPublicKey, string toPublicKey, decimal amount, string? memo = null, string? refId = null);
        Task<(bool Ok, string Error)> DebitFeeAsync(string payerPublicKey, decimal amount, string? memo = null, string? refId = null);
        Task<(bool Ok, string Error)> SettleFeeAsync(
            string payer, decimal amount, string feeType,
            IReadOnlyCollection<(string Account, decimal Weight)> serviceRecipients,
            string? refId, string? memo, bool force = false);
        List<LedgerMove> BuildFeeMoves(
            string payer, decimal amount, string feeType,
            IReadOnlyCollection<(string Account, decimal Weight)> serviceRecipients, string? memo);
        Task<(bool Ok, string Error)> ApplyMovesAsync(IReadOnlyCollection<LedgerMove> moves, string? refId, bool allowNegativeFrom = false);

        /// <summary>Set every live balance to the replay of the whole ledger (rows are the truth); returns what changed.</summary>
        Task<List<(string Account, decimal Was, decimal Now)>> RepairBalancesFromLedgerAsync();

        Task<(bool Ok, string Error, TopUpOrder? Order)> CreateTopUpOrderAsync(string buyerPublicKey, decimal fiatAmountVnd);
        Task<(bool Ok, string Error, TopUpOrder? Order)> CreateTopUpOrderAsync(string buyerPublicKey, decimal fiatAmountVnd, string channel);
        Task<TopUpOrder?> GetTopUpOrderAsync(string orderId);
        Task<(bool Ok, string Error, TopUpOrder? Order)> CompleteTopUpOrderAsync(string orderId, string? networkTxId = null);
        Task<List<TopUpOrder>> ListTopUpOrdersAsync(string? status = "pending", int limit = 50);
        Task<decimal> GetReserveDepositsVndAsync();

        Task<EconomicsSnapshot> GetEconomicsAsync();
    }

    public class FeeSplitInfo
    {
        public decimal Node { get; set; }
        public decimal Fund { get; set; }
        public decimal Ops { get; set; }
    }

    public class EconomicsSnapshot
    {
        public decimal HardCap { get; set; }
        public string TreasuryPublicKey { get; set; } = "";
        public decimal TreasuryBalance { get; set; }
        public decimal CirculatingD { get; set; }
        /// <summary>Default channel (transfer) fee, kept for v1 clients.</summary>
        public decimal OnRampFeePercent { get; set; }
        public Dictionary<string, decimal> OnRampFeeByChannel { get; set; } = new();
        public decimal PlatformFiatPercent { get; set; }
        public decimal NetworkReserveFiatPercent { get; set; }
        public decimal VndPerD { get; set; }
        /// <summary>Base units (D) per display unit (DLT).</summary>
        public decimal DPerDlt { get; set; }
        public decimal MinTopUpVnd { get; set; }
        public bool EnforceFees { get; set; }
        public Dictionary<string, FeeSplitInfo> FeeSplits { get; set; } = new();
        public string Notes { get; set; } = "";
    }
}

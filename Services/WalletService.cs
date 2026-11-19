using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeltaniumApi.Data;
using DeltaniumApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// D credit ledger (TOKENOMICS_V2). Base unit D = 1 VND; display unit DLT = 1,000,000 D.
    /// All supply is credited at genesis to the issuance account; D leaves issuance only via
    /// paid top-ups (backed 1:1 by the reserve) and returns there on redemption.
    /// </summary>
    public class WalletService : IWalletService
    {
        public const string DefaultTreasuryKey = "deltanium-treasury";

        private readonly ApiDbContext? _db;
        private readonly IConfiguration _config;
        private readonly bool _useMySql;
        private readonly string _jsonDir;
        private static readonly SemaphoreSlim JsonGate = new(1, 1);

        public WalletService(IConfiguration config, ApiDbContext? db = null)
        {
            _config = config;
            _db = db;
            _useMySql = string.Equals(config["Storage:Provider"], "MySql", StringComparison.OrdinalIgnoreCase) && db != null;
            _jsonDir = config["Economics:DataDir"] is { Length: > 0 } d
                ? d
                : Path.Combine(Directory.GetCurrentDirectory(), "wallet-data");
        }

        // ---------- parameters ----------

        /// <summary>Issuance account (v1 name kept: "deltanium-treasury").</summary>
        public string TreasuryKey =>
            _config["Economics:TreasuryPublicKey"]?.Trim() is { Length: > 0 } k ? k : DefaultTreasuryKey;

        /// <summary>Max supply in D. 10,000,000 DLT × 1,000,000 D/DLT = 10^13 D.</summary>
        public decimal HardCap => Dec("Economics:HardCap", 10_000_000_000_000m);
        public decimal VndPerD => Dec("Economics:VndPerD", 1m);
        public decimal DPerDlt => Dec("Economics:DPerDlt", 1_000_000m);
        public decimal MinTopUpVnd => Math.Max(Dec("Economics:MinTopUpVnd", 10_000m), VndPerD);
        public bool EnforceFees => Bool("Economics:EnforceFees", false);
        /// <summary>v2: 100% of supply on ledger at genesis.</summary>
        private decimal TreasuryGenesisShare => Dec("Economics:TreasuryGenesisShare", 1.0m);

        public static readonly string[] Channels = { "transfer", "card", "iap", "paypal" };

        public decimal OnRampFeePercent(string channel) => channel switch
        {
            "card" => Dec("Economics:OnRampFee:Card", 5m),
            "iap" => Dec("Economics:OnRampFee:Iap", 0m),
            "paypal" => Dec("Economics:OnRampFee:PayPal", 6m),
            _ => Dec("Economics:OnRampFee:Transfer", 3m),
        };

        /// <summary>(service node share, network fund share, protocol ops share) in percent.</summary>
        public (decimal Node, decimal Fund, decimal Ops) FeeSplit(string feeType) => feeType switch
        {
            FeeTypes.Storage => (Dec("Economics:FeeSplit:Storage:Node", 80m), Dec("Economics:FeeSplit:Storage:Fund", 10m), Dec("Economics:FeeSplit:Storage:Ops", 10m)),
            FeeTypes.Bandwidth => (Dec("Economics:FeeSplit:Bandwidth:Node", 85m), Dec("Economics:FeeSplit:Bandwidth:Fund", 5m), Dec("Economics:FeeSplit:Bandwidth:Ops", 10m)),
            FeeTypes.Tx => (Dec("Economics:FeeSplit:Tx:Node", 80m), Dec("Economics:FeeSplit:Tx:Fund", 10m), Dec("Economics:FeeSplit:Tx:Ops", 10m)),
            FeeTypes.Creator => (Dec("Economics:FeeSplit:Creator:Node", 90m), Dec("Economics:FeeSplit:Creator:Fund", 3m), Dec("Economics:FeeSplit:Creator:Ops", 7m)),
            _ => (0m, 50m, 50m)
        };

        private decimal Dec(string key, decimal fallback) =>
            decimal.TryParse(_config[key], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private bool Bool(string key, bool fallback) =>
            _config[key] is { Length: > 0 } s ? string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) : fallback;

        // ---------- setup ----------

        public async Task EnsureReadyAsync()
        {
            if (_useMySql)
            {
                await _db!.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS wallet_accounts (
  PublicKey VARCHAR(200) NOT NULL PRIMARY KEY,
  Balance DECIMAL(28,10) NOT NULL DEFAULT 0,
  UpdatedAt DATETIME(6) NOT NULL
);");
                await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS wallet_ledger (
  Id VARCHAR(64) NOT NULL PRIMARY KEY,
  FromPublicKey VARCHAR(200) NULL,
  ToPublicKey VARCHAR(200) NULL,
  Amount DECIMAL(28,10) NOT NULL,
  Kind VARCHAR(32) NOT NULL,
  RefId VARCHAR(64) NULL,
  Memo VARCHAR(512) NULL,
  CreatedAt DATETIME(6) NOT NULL,
  INDEX IX_wallet_ledger_from (FromPublicKey),
  INDEX IX_wallet_ledger_to (ToPublicKey),
  INDEX IX_wallet_ledger_created (CreatedAt)
);");
                await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS topup_orders (
  Id VARCHAR(64) NOT NULL PRIMARY KEY,
  BuyerPublicKey VARCHAR(130) NOT NULL,
  FiatAmountVnd DECIMAL(28,2) NOT NULL,
  FeePercent DECIMAL(8,4) NOT NULL,
  DCredited DECIMAL(28,10) NOT NULL,
  PlatformFiatVnd DECIMAL(28,2) NOT NULL,
  NetworkReserveFiatVnd DECIMAL(28,2) NOT NULL,
  Channel VARCHAR(16) NOT NULL DEFAULT 'transfer',
  Status VARCHAR(32) NOT NULL,
  NetworkTxId VARCHAR(64) NULL,
  CreatedAt DATETIME(6) NOT NULL,
  CompletedAt DATETIME(6) NULL,
  INDEX IX_topup_buyer (BuyerPublicKey),
  INDEX IX_topup_status (Status)
);");
                // Upgrades of tables created by earlier versions (ignore "already applied" errors).
                foreach (var sql in new[]
                {
                    "ALTER TABLE topup_orders ADD COLUMN NetworkTxId VARCHAR(64) NULL;",
                    "ALTER TABLE topup_orders ADD COLUMN Channel VARCHAR(16) NOT NULL DEFAULT 'transfer';",
                    "ALTER TABLE wallet_accounts MODIFY PublicKey VARCHAR(200) NOT NULL;",
                    "ALTER TABLE wallet_ledger MODIFY FromPublicKey VARCHAR(200) NULL;",
                    "ALTER TABLE wallet_ledger MODIFY ToPublicKey VARCHAR(200) NULL;",
                })
                {
                    try { await _db.Database.ExecuteSqlRawAsync(sql); } catch { /* already applied */ }
                }
            }
            else
            {
                Directory.CreateDirectory(_jsonDir);
                EnsureJsonFile("wallet_accounts.json");
                EnsureJsonFile("wallet_ledger.json");
                EnsureJsonFile("topup_orders.json");
            }

            await SeedGenesisIfEmptyAsync();
        }

        private void EnsureJsonFile(string name)
        {
            var path = Path.Combine(_jsonDir, name);
            if (!File.Exists(path)) File.WriteAllText(path, "[]");
        }

        private async Task SeedGenesisIfEmptyAsync()
        {
            var accounts = await GetAllBalancesAsync();
            if (accounts.Count > 0) return;
            var genesis = Math.Round(HardCap * TreasuryGenesisShare, 10);
            await ApplyMovesAsync(new[] { new LedgerMove(null, TreasuryKey, genesis, "genesis",
                $"Genesis: {TreasuryGenesisShare:P0} of hard cap ({HardCap} D) to issuance") }, "genesis-issuance", allowNegativeFrom: true);
        }

        // ---------- reads ----------

        public async Task<EconomicsSnapshot> GetEconomicsAsync()
        {
            var issuance = await GetBalanceAsync(TreasuryKey);
            return new EconomicsSnapshot
            {
                HardCap = HardCap,
                TreasuryPublicKey = TreasuryKey,
                TreasuryBalance = issuance,
                CirculatingD = HardCap - issuance,
                OnRampFeePercent = OnRampFeePercent("transfer"),
                OnRampFeeByChannel = Channels.ToDictionary(c => c, OnRampFeePercent),
                PlatformFiatPercent = OnRampFeePercent("transfer"),
                NetworkReserveFiatPercent = 100m - OnRampFeePercent("transfer"),
                VndPerD = VndPerD,
                DPerDlt = DPerDlt,
                MinTopUpVnd = MinTopUpVnd,
                EnforceFees = EnforceFees,
                FeeSplits = new[] { FeeTypes.Storage, FeeTypes.Bandwidth, FeeTypes.Tx, FeeTypes.Creator }
                    .ToDictionary(t => t, t => { var s = FeeSplit(t); return new FeeSplitInfo { Node = s.Node, Fund = s.Fund, Ops = s.Ops }; }),
                Notes = "See TOKENOMICS_V2.md — 1 D = 1 VND (base unit), 1 DLT = 1,000,000 D. " +
                        "Top-ups buy D from issuance and are backed 1:1 by the reserve; register grant is 0."
            };
        }

        public async Task<decimal> GetBalanceAsync(string publicKey)
        {
            publicKey = Norm(publicKey);
            if (_useMySql)
            {
                var row = await _db!.WalletAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.PublicKey == publicKey);
                return row?.Balance ?? 0m;
            }
            await JsonGate.WaitAsync();
            try { return ReadJson<List<WalletAccount>>("wallet_accounts.json").FirstOrDefault(a => a.PublicKey == publicKey)?.Balance ?? 0m; }
            finally { JsonGate.Release(); }
        }

        public async Task<List<WalletAccount>> GetAllBalancesAsync()
        {
            if (_useMySql)
                return await _db!.WalletAccounts.AsNoTracking().ToListAsync();
            await JsonGate.WaitAsync();
            try { return ReadJson<List<WalletAccount>>("wallet_accounts.json"); }
            finally { JsonGate.Release(); }
        }

        public async Task<List<WalletLedgerEntry>> GetLedgerAsync(string publicKey, int limit = 50)
        {
            publicKey = Norm(publicKey);
            limit = Math.Clamp(limit, 1, 200);
            if (_useMySql)
            {
                return await _db!.WalletLedger.AsNoTracking()
                    .Where(e => e.FromPublicKey == publicKey || e.ToPublicKey == publicKey)
                    .OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
            }
            await JsonGate.WaitAsync();
            try
            {
                return ReadJson<List<WalletLedgerEntry>>("wallet_ledger.json")
                    .Where(e => e.FromPublicKey == publicKey || e.ToPublicKey == publicKey)
                    .OrderByDescending(e => e.CreatedAt).Take(limit).ToList();
            }
            finally { JsonGate.Release(); }
        }

        /// <summary>Ledger rows with fromExclusive &lt; CreatedAt ≤ upToInclusive, in canonical order.</summary>
        public async Task<List<WalletLedgerEntry>> GetLedgerRangeAsync(DateTime fromExclusive, DateTime upToInclusive)
        {
            List<WalletLedgerEntry> rows;
            if (_useMySql)
            {
                rows = await _db!.WalletLedger.AsNoTracking()
                    .Where(e => e.CreatedAt > fromExclusive && e.CreatedAt <= upToInclusive)
                    .ToListAsync();
            }
            else
            {
                await JsonGate.WaitAsync();
                try
                {
                    rows = ReadJson<List<WalletLedgerEntry>>("wallet_ledger.json")
                        .Where(e => e.CreatedAt > fromExclusive && e.CreatedAt <= upToInclusive).ToList();
                }
                finally { JsonGate.Release(); }
            }
            return rows.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        }

        // ---------- transfers & fees ----------

        public async Task<(bool Ok, string Error)> TransferAsync(
            string fromPublicKey, string toPublicKey, decimal amount, string? memo = null, string? refId = null)
        {
            fromPublicKey = Norm(fromPublicKey);
            toPublicKey = Norm(toPublicKey);
            if (amount <= 0) return (false, "Amount must be positive");
            if (fromPublicKey == toPublicKey) return (false, "Cannot transfer to self");
            if (EconAccounts.IsSystem(toPublicKey) || toPublicKey == TreasuryKey)
                return (false, "Cannot transfer to a system account");
            return await ApplyMovesAsync(new[] { new LedgerMove(fromPublicKey, toPublicKey, amount, "transfer", memo) }, refId);
        }

        /// <summary>Legacy flat fee (v1). v2 callers use <see cref="SettleFeeAsync"/>.</summary>
        public async Task<(bool Ok, string Error)> DebitFeeAsync(
            string payerPublicKey, decimal amount, string? memo = null, string? refId = null)
        {
            if (!EnforceFees || amount <= 0) return (true, "");
            return await SettleFeeAsync(Norm(payerPublicKey), amount, "other", Array.Empty<(string, decimal)>(), refId, memo ?? "Network fee", force: true);
        }

        /// <summary>
        /// Split a service fee (§5): node share goes to <paramref name="serviceRecipients"/> pro rata to weight
        /// (or to the network fund when empty), the rest to the network fund and protocol ops. Atomic.
        /// User-paid fees are skipped while Economics:EnforceFees=false unless <paramref name="force"/>.
        /// </summary>
        public async Task<(bool Ok, string Error)> SettleFeeAsync(
            string payer,
            decimal amount,
            string feeType,
            IReadOnlyCollection<(string Account, decimal Weight)> serviceRecipients,
            string? refId,
            string? memo,
            bool force = false)
        {
            if (amount <= 0) return (true, "");
            payer = Norm(payer);
            if (!force && !EnforceFees && !EconAccounts.IsSystem(payer)) return (true, "");

            var moves = BuildFeeMoves(payer, amount, feeType, serviceRecipients, memo);
            return await ApplyMovesAsync(moves, refId);
        }

        public List<LedgerMove> BuildFeeMoves(
            string payer, decimal amount, string feeType,
            IReadOnlyCollection<(string Account, decimal Weight)> serviceRecipients, string? memo)
        {
            var (nodePct, fundPct, opsPct) = FeeSplit(feeType);
            var total = nodePct + fundPct + opsPct;
            if (total <= 0) { nodePct = 0; fundPct = 50; opsPct = 50; total = 100; }

            var nodeAmount = Math.Round(amount * nodePct / total, 10);
            var opsAmount = Math.Round(amount * opsPct / total, 10);
            var fundAmount = amount - nodeAmount - opsAmount; // absorbs rounding

            var moves = new List<LedgerMove>();
            var kind = "fee:" + feeType;
            var recipients = serviceRecipients.Where(r => r.Weight > 0 && !string.IsNullOrWhiteSpace(r.Account)).ToList();
            if (nodeAmount > 0)
            {
                if (recipients.Count == 0)
                {
                    fundAmount += nodeAmount;
                }
                else
                {
                    var weightSum = recipients.Sum(r => r.Weight);
                    decimal allocated = 0;
                    for (var i = 0; i < recipients.Count; i++)
                    {
                        var share = i == recipients.Count - 1
                            ? nodeAmount - allocated
                            : Math.Round(nodeAmount * recipients[i].Weight / weightSum, 10);
                        allocated += share;
                        moves.Add(new LedgerMove(payer, Norm(recipients[i].Account), share, kind, memo));
                    }
                }
            }
            if (fundAmount > 0) moves.Add(new LedgerMove(payer, EconAccounts.NetworkFund, fundAmount, kind, memo));
            if (opsAmount > 0) moves.Add(new LedgerMove(payer, EconAccounts.ProtocolOps, opsAmount, kind, memo));
            return moves.Where(m => m.Amount > 0 && m.From != m.To).ToList();
        }

        // ---------- top-ups ----------

        public Task<(bool Ok, string Error, TopUpOrder? Order)> CreateTopUpOrderAsync(string buyerPublicKey, decimal fiatAmountVnd)
            => CreateTopUpOrderAsync(buyerPublicKey, fiatAmountVnd, "transfer");

        public async Task<(bool Ok, string Error, TopUpOrder? Order)> CreateTopUpOrderAsync(
            string buyerPublicKey, decimal fiatAmountVnd, string channel)
        {
            buyerPublicKey = Norm(buyerPublicKey);
            channel = (channel ?? "transfer").Trim().ToLowerInvariant();
            if (!Channels.Contains(channel)) return (false, $"Unknown channel '{channel}' (transfer | card | iap | paypal)", null);
            if (fiatAmountVnd < MinTopUpVnd) return (false, $"Minimum top-up is {MinTopUpVnd} VND", null);

            var fee = OnRampFeePercent(channel);
            var dCredited = Math.Floor(fiatAmountVnd * (100m - fee) / 100m / VndPerD * 1_000_000m) / 1_000_000m;
            var reserveFiat = Math.Round(dCredited * VndPerD, 2);
            var order = new TopUpOrder
            {
                BuyerPublicKey = buyerPublicKey,
                FiatAmountVnd = fiatAmountVnd,
                FeePercent = fee,
                DCredited = dCredited,
                NetworkReserveFiatVnd = reserveFiat,
                PlatformFiatVnd = fiatAmountVnd - reserveFiat,
                Channel = channel,
                Status = "pending"
            };

            if (_useMySql)
            {
                _db!.TopUpOrders.Add(order);
                await _db.SaveChangesAsync();
            }
            else
            {
                await JsonGate.WaitAsync();
                try
                {
                    var orders = ReadJson<List<TopUpOrder>>("topup_orders.json");
                    orders.Add(order);
                    WriteJson("topup_orders.json", orders);
                }
                finally { JsonGate.Release(); }
            }
            return (true, "", order);
        }

        public async Task<TopUpOrder?> GetTopUpOrderAsync(string orderId)
        {
            if (_useMySql) return await _db!.TopUpOrders.FirstOrDefaultAsync(o => o.Id == orderId);
            await JsonGate.WaitAsync();
            try { return ReadJson<List<TopUpOrder>>("topup_orders.json").FirstOrDefault(o => o.Id == orderId); }
            finally { JsonGate.Release(); }
        }

        public async Task<(bool Ok, string Error, TopUpOrder? Order)> CompleteTopUpOrderAsync(string orderId, string? networkTxId = null)
        {
            var order = await GetTopUpOrderAsync(orderId);
            if (order == null) return (false, "Order not found", null);
            if (order.Status == "completed") return (true, "", order);
            if (order.Status != "pending") return (false, $"Order status is {order.Status}", order);

            if (await GetBalanceAsync(TreasuryKey) < order.DCredited)
                return (false, "Issuance account has insufficient D", order);

            var move = await ApplyMovesAsync(new[]
            {
                new LedgerMove(TreasuryKey, order.BuyerPublicKey, order.DCredited, "topup",
                    $"Top-up {order.FiatAmountVnd} VND via {order.Channel} → {order.DCredited} D (fee {order.FeePercent}%, reserve {order.NetworkReserveFiatVnd} VND) [networkTx={networkTxId ?? order.Id}]")
            }, order.Id);
            if (!move.Ok) return (false, move.Error, order);

            order.Status = "completed";
            order.CompletedAt = DateTime.UtcNow;
            order.NetworkTxId = string.IsNullOrWhiteSpace(networkTxId) ? order.Id : networkTxId;

            if (_useMySql)
            {
                _db!.TopUpOrders.Update(order);
                await _db.SaveChangesAsync();
            }
            else
            {
                await JsonGate.WaitAsync();
                try
                {
                    var orders = ReadJson<List<TopUpOrder>>("topup_orders.json");
                    var idx = orders.FindIndex(o => o.Id == orderId);
                    if (idx >= 0) orders[idx] = order;
                    WriteJson("topup_orders.json", orders);
                }
                finally { JsonGate.Release(); }
            }
            return (true, "", order);
        }

        public async Task<List<TopUpOrder>> ListTopUpOrdersAsync(string? status = "pending", int limit = 50)
        {
            limit = limit <= 0 ? 50 : Math.Min(limit, 1000);
            var statusFilter = string.IsNullOrWhiteSpace(status) || status == "all" ? null : status.Trim();
            if (_useMySql)
            {
                var q = _db!.TopUpOrders.AsQueryable();
                if (statusFilter != null) q = q.Where(o => o.Status == statusFilter);
                return await q.OrderByDescending(o => o.CreatedAt).Take(limit).ToListAsync();
            }
            await JsonGate.WaitAsync();
            try
            {
                var orders = ReadJson<List<TopUpOrder>>("topup_orders.json").AsEnumerable();
                if (statusFilter != null) orders = orders.Where(o => o.Status == statusFilter);
                return orders.OrderByDescending(o => o.CreatedAt).Take(limit).ToList();
            }
            finally { JsonGate.Release(); }
        }

        /// <summary>Total VND moved into the backing reserve by completed top-ups.</summary>
        public async Task<decimal> GetReserveDepositsVndAsync()
        {
            if (_useMySql)
                return await _db!.TopUpOrders.Where(o => o.Status == "completed").SumAsync(o => (decimal?)o.NetworkReserveFiatVnd) ?? 0m;
            await JsonGate.WaitAsync();
            try { return ReadJson<List<TopUpOrder>>("topup_orders.json").Where(o => o.Status == "completed").Sum(o => o.NetworkReserveFiatVnd); }
            finally { JsonGate.Release(); }
        }

        // ---------- core movement ----------

        /// <summary>Apply all legs atomically: either every leg is booked or none.</summary>
        public async Task<(bool Ok, string Error)> ApplyMovesAsync(
            IReadOnlyCollection<LedgerMove> moves, string? refId, bool allowNegativeFrom = false)
        {
            var legs = moves.Select(m => m with { From = m.From == null ? null : Norm(m.From), To = Norm(m.To) }).ToList();
            if (legs.Count == 0) return (true, "");
            if (legs.Any(m => m.Amount <= 0)) return (false, "Amount must be positive");

            if (_useMySql)
            {
                await using var tx = await _db!.Database.BeginTransactionAsync();
                try
                {
                    var keys = legs.SelectMany(m => new[] { m.From, m.To }).Where(k => k != null).Distinct().ToList();
                    var accounts = await _db.WalletAccounts.Where(a => keys.Contains(a.PublicKey)).ToDictionaryAsync(a => a.PublicKey);
                    var err = Book(legs, accounts, a => _db.WalletAccounts.Add(a), allowNegativeFrom, refId, e => _db.WalletLedger.Add(e));
                    if (err != null) { await tx.RollbackAsync(); return (false, err); }
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return (true, "");
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync();
                    return (false, ex.Message);
                }
            }

            await JsonGate.WaitAsync();
            try
            {
                var list = ReadJson<List<WalletAccount>>("wallet_accounts.json");
                var accounts = list.ToDictionary(a => a.PublicKey);
                var ledger = ReadJson<List<WalletLedgerEntry>>("wallet_ledger.json");
                var err = Book(legs, accounts, a => list.Add(a), allowNegativeFrom, refId, e => ledger.Add(e));
                if (err != null) return (false, err);
                WriteJson("wallet_accounts.json", list);
                WriteJson("wallet_ledger.json", ledger);
                return (true, "");
            }
            finally { JsonGate.Release(); }
        }

        private static string? Book(
            List<LedgerMove> legs,
            Dictionary<string, WalletAccount> accounts,
            Action<WalletAccount> addAccount,
            bool allowNegativeFrom,
            string? refId,
            Action<WalletLedgerEntry> addEntry)
        {
            WalletAccount Get(string key)
            {
                if (!accounts.TryGetValue(key, out var acc))
                {
                    acc = new WalletAccount { PublicKey = key, Balance = 0 };
                    accounts[key] = acc;
                    addAccount(acc);
                }
                return acc;
            }

            // Check sufficiency per source account over all legs first (no partial booking).
            foreach (var g in legs.Where(l => l.From != null).GroupBy(l => l.From!))
            {
                var need = g.Sum(l => l.Amount);
                var have = accounts.TryGetValue(g.Key, out var a) ? a.Balance : 0m;
                if (!allowNegativeFrom && have < need)
                    return $"Insufficient balance in {g.Key} (need {need}, have {have})";
            }

            var now = DateTime.UtcNow;
            foreach (var leg in legs)
            {
                if (leg.From != null)
                {
                    var from = Get(leg.From);
                    from.Balance -= leg.Amount;
                    from.UpdatedAt = now;
                }
                var to = Get(leg.To);
                to.Balance += leg.Amount;
                to.UpdatedAt = now;
                addEntry(new WalletLedgerEntry
                {
                    FromPublicKey = leg.From,
                    ToPublicKey = leg.To,
                    Amount = leg.Amount,
                    Kind = leg.Kind.Length > 32 ? leg.Kind[..32] : leg.Kind,
                    RefId = refId is { Length: > 64 } ? refId[..64] : refId,
                    Memo = leg.Memo is { Length: > 512 } ? leg.Memo[..512] : leg.Memo,
                    CreatedAt = now
                });
            }
            return null;
        }

        private static string Norm(string pk) => (pk ?? "").Trim();

        private T ReadJson<T>(string file) where T : new()
        {
            var path = Path.Combine(_jsonDir, file);
            if (!File.Exists(path)) return new T();
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) ?? new T();
        }

        private void WriteJson(string file, object data)
        {
            Directory.CreateDirectory(_jsonDir);
            File.WriteAllText(Path.Combine(_jsonDir, file), JsonConvert.SerializeObject(data, Formatting.Indented));
        }
    }
}

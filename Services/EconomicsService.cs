using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DeltaniumApi.Services.Dltp;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// TOKENOMICS_V2 network economics on top of the wallet ledger:
    /// storage escrow + proof-of-storage (§6), bandwidth (§7), bonds &amp; slashing (§8),
    /// creator subscriptions &amp; tips (§9), free tier &amp; sponsored txs (§10),
    /// ledger anchors (§11 phase A), reserve &amp; redemption (§2, §4).
    /// </summary>
    public class EconomicsService
    {
        private const string CEscrow = "escrow", CProof = "proof", CEgress = "egress", CEgressCap = "egresscap",
            CEgressSpend = "egressspend", CPayee = "payee", CRedemption = "redemption", CBondWd = "bondwd",
            CSlash = "slash", CPlan = "subplan", CSub = "sub", CAnchor = "anchor", CMeta = "meta",
            CAnchorBal = "anchorbal", CRepl = "repl", CReceipt = "egressrcpt";

        private static readonly HashSet<string> SocialTxTypes = new(StringComparer.OrdinalIgnoreCase)
        { "UserRegistration", "Follow", "Unfollow", "CreatePost", "React", "Comment" };

        private static readonly ConcurrentDictionary<string, Queue<DateTime>> SocialRate = new();

        private readonly IWalletService _wallet;
        private readonly EconDocStore _docs;
        private readonly IStorageService _storage;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory? _http;
        private readonly ILogger<EconomicsService>? _log;
        private readonly KeyService? _keys;

        public EconomicsService(
            IWalletService wallet,
            EconDocStore docs,
            IStorageService storage,
            IConfiguration config,
            IHttpClientFactory? http = null,
            ILogger<EconomicsService>? log = null,
            KeyService? keys = null)
        {
            _keys = keys;
            _wallet = wallet;
            _docs = docs;
            _storage = storage;
            _config = config;
            _http = http;
            _log = log;
        }

        // ---------------- parameters ----------------

        public decimal PricePerGbMonth => Dec("Economics:Storage:PricePerGbMonth", 800m);
        public decimal MinContractFee => Dec("Economics:Storage:MinContractFee", 10m);
        public int DefaultMonths => (int)Dec("Economics:Storage:DefaultMonths", 12m);
        public int MaxMonths => (int)Dec("Economics:Storage:MaxMonths", 120m);
        public long FreeTierStorageBytes => (long)Dec("Economics:FreeTier:StorageBytes", 200m * 1024 * 1024);
        public long FreeTierEgressBytes => (long)Dec("Economics:FreeTier:EgressBytesPerMonth", 2m * 1024 * 1024 * 1024);
        public bool FreeTierRequireEmail => Bool("Economics:FreeTier:RequireEmail", true);
        public int SocialTxPerHour => (int)Dec("Economics:FreeTier:SocialTxPerHour", 120m);
        public decimal EgressQuotaMultiplier => Dec("Economics:Bandwidth:QuotaMultiplier", 10m);
        public decimal EgressPricePerGb => Dec("Economics:Bandwidth:PricePerGb", 300m);
        public decimal MaxReportedReadsPerFile => Dec("Economics:Bandwidth:MaxReadsPerReport", 10000m);
        public decimal TxFee => Dec("Economics:TxFee", 1m);
        public decimal RedeemFeePercent => Dec("Economics:Redemption:FeePercent", 1m);
        public decimal RedeemMinD => Dec("Economics:Redemption:MinD", 100_000m);
        public decimal ProofFullPassRate => Dec("Economics:Proof:FullPassPercent", 95m) / 100m;
        public decimal ProofMinPassRate => Dec("Economics:Proof:MinPassPercent", 80m) / 100m;
        public bool NoChallengeCountsAsPass => Bool("Economics:Proof:NoChallengeCountsAsPass", true);
        public decimal SlashStorageFailPercent => Dec("Economics:Slash:StorageFailPercent", 10m);
        public decimal SlashBlockerDowntimePercent => Dec("Economics:Slash:BlockerDowntimePercent", 5m);
        public bool EnforceBonds => Bool("Economics:Bond:Enforce", false);
        public decimal StoreBondBase => Dec("Economics:Bond:StoreBase", 100_000m);
        public decimal StoreBondPer100Gb => Dec("Economics:Bond:StorePer100Gb", 50_000m);
        public decimal BlockerBond => Dec("Economics:Bond:Blocker", 500_000m);
        public int BondWithdrawDelayDays => (int)Dec("Economics:Bond:WithdrawDelayDays", 30m);
        public int GraceDays => (int)Dec("Economics:Storage:GraceDays", 30m);
        /// <summary>off: bill Store-reported egress; require: bill only bytes covered by reader-signed receipts (§7).</summary>
        public string EgressReceiptMode => (_config["Economics:Bandwidth:ReceiptMode"] ?? "off").Trim().ToLowerInvariant();
        public int EgressReceiptMaxAgeDays => (int)Dec("Economics:Bandwidth:ReceiptMaxAgeDays", 30m);
        public bool AutoRepair => Bool("Economics:Storage:AutoRepair", true);
        /// <summary>Most copies one upload may ask for (Economics:Storage:MaxReplicas, default 3).</summary>
        public int MaxReplicas => int.TryParse(_config["Economics:Storage:MaxReplicas"], out var r) && r > 0 ? r : 3;

        /// <summary>Fewer healthy copies than were paid for: a Store failed, or the uploader asked Central to replicate.</summary>
        public static bool UnderReplicated(StorageEscrow e) =>
            e.StoreNodeIds.Count(id => !e.FailedNodeIds.Contains(id)) < (e.TargetReplicas > 0 ? e.TargetReplicas : e.StoreNodeIds.Count);

        private decimal Dec(string key, decimal fallback) =>
            decimal.TryParse(_config[key], NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private bool Bool(string key, bool fallback) =>
            _config[key] is { Length: > 0 } s ? string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) : fallback;

        /// <summary>
        /// Authenticate a call to a Store's internal endpoint: Central signature (Stores verify it with the
        /// Central public key). The shared node token is gone.
        /// </summary>
        private void AuthorizeStoreCall(HttpRequestMessage req, string? body)
        {
            if (_keys != null) CentralRequestSigner.Sign(req, body, _keys);
        }

        public static string Epoch(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        private static bool SameKey(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(SignatureAuthHelper.NormalizePubKey(a), SignatureAuthHelper.NormalizePubKey(b),
                StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- node identity ----------------

        public async Task<(string Kind, string PublicKey, string Id, string Endpoint)?> FindNodeAsync(string publicKey)
        {
            foreach (var n in await _storage.GetNodesAsync())
                if (SameKey(n.PublicKey, publicKey)) return ("store", n.PublicKey, n.Id, NodeAddress.Of(n.Endpoint, n.TcpEndpoint, n.PublicKey));
            foreach (var b in await _storage.GetBlockCreatorNodesAsync())
                if (SameKey(b.PublicKey, publicKey)) return ("blocker", b.PublicKey, b.Id, NodeAddress.Of(b.Endpoint, b.TcpEndpoint, b.PublicKey));
            return null;
        }

        /// <summary>Ledger account for a caller: registered nodes use their registry key, users their own key.</summary>
        public async Task<string> ResolveAccountAsync(string callerPublicKey)
        {
            var node = await FindNodeAsync(callerPublicKey);
            return node?.PublicKey ?? callerPublicKey.Trim();
        }

        // ---------------- storage quote & escrow (§6, §10) ----------------

        /// <summary>
        /// Free tier needs a verified email once Economics:FreeTier:RequireVerifiedEmail=true (needs SMTP, see
        /// EmailVerificationService). Until then an email on the profile is enough, but each address counts once:
        /// only the earliest account registered with it gets the free tier.
        /// </summary>
        public bool FreeTierRequireVerifiedEmail => Bool("Economics:FreeTier:RequireVerifiedEmail", false);

        /// <summary>
        /// Free-tier bytes the network may hold without the network fund paying for them (fund empty).
        /// Past it new free-tier uploads are refused instead of being stored for nothing.
        /// </summary>
        public long FreeTierUnfundedBudgetBytes => (long)Dec("Economics:FreeTier:UnfundedBudgetBytes", 50m * 1024 * 1024 * 1024);

        public async Task<bool> IsEmailVerifiedAsync(string owner) =>
            !string.IsNullOrEmpty((await _docs.GetAsync<EmailVerification>(EmailVerificationService.CVerify, owner))?.VerifiedEmail);

        public async Task<bool> IsFreeTierEligibleAsync(string owner)
        {
            var users = await _storage.GetUsersAsync();
            var user = users.FirstOrDefault(u => SameKey(u.PublicKey, owner));
            if (user == null) return false;
            if (FreeTierRequireVerifiedEmail) return await IsEmailVerifiedAsync(owner);
            if (!FreeTierRequireEmail) return true;
            if (string.IsNullOrWhiteSpace(user.Email)) return false;
            var email = EmailVerificationService.Normalize(user.Email);
            var first = users.Where(u => !string.IsNullOrWhiteSpace(u.Email) && EmailVerificationService.Normalize(u.Email!) == email)
                .OrderBy(u => u.CreatedAt).First();
            return SameKey(first.PublicKey, owner);
        }

        public async Task<long> GetFreeTierUsedBytesAsync(string owner)
        {
            var escrows = await _docs.ListAsync<StorageEscrow>(CEscrow,
                e => e.FreeTier && (e.Status == "Active" || e.Status == "Grace") && SameKey(e.OwnerPublicKey, owner));
            return escrows.Sum(e => e.SizeBytes * Math.Max(1, e.StoreNodeIds.Count));
        }

        /// <summary>Largest item that may be registered as a free social item (reaction, comment, chat message).</summary>
        public const long SocialItemMaxBytes = 64 * 1024;

        /// <summary>
        /// Social bytes one owner may store for free per calendar month (UTC). "Social" is declared by the App,
        /// so without a budget any small file could be registered as a reaction and stored free forever.
        /// Beyond it, items are billed like any other upload (free-tier quota first).
        /// </summary>
        public long SocialMonthlyBytes => (long)Dec("Economics:FreeTier:SocialMonthlyBytes", 20m * 1024 * 1024);

        public async Task<long> GetSocialBytesThisMonthAsync(string owner, DateTime nowUtc)
        {
            var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var items = await _docs.ListAsync<StorageEscrow>(CEscrow,
                e => e.Social && e.StartAt >= monthStart && SameKey(e.OwnerPublicKey, owner));
            return items.Sum(e => e.SizeBytes * Math.Max(1, e.StoreNodeIds.Count));
        }

        public decimal MonthlyStorageFee(long sizeBytes, int replicas)
        {
            var gb = sizeBytes / (1024m * 1024m * 1024m);
            return Math.Round(gb * PricePerGbMonth * Math.Max(1, replicas), 10);
        }

        /// <summary>
        /// What an AutoRenew contract takes each month after the first: the storage actually used, in whole 0.0001 D
        /// (at least 0.0001 D). The minimum contract fee is an opening fee and applies to the first payment only.
        /// </summary>
        public static decimal RenewalFeeFor(decimal monthly) =>
            monthly <= 0 ? 0m : Math.Max(0.0001m, Math.Round(monthly, 4, MidpointRounding.AwayFromZero));

        public async Task<StorageQuote> QuoteAsync(string? owner, long sizeBytes, int replicas, int months)
        {
            months = Math.Clamp(months <= 0 ? DefaultMonths : months, 1, MaxMonths);
            replicas = Math.Max(1, replicas);
            var monthly = MonthlyStorageFee(sizeBytes, replicas);
            // Whole 0.0001 D, so the fee an App shows (4 decimals) is exactly the fee charged.
            var total = Math.Round(Math.Max(MinContractFee, monthly * months), 4, MidpointRounding.AwayFromZero);
            var quote = new StorageQuote
            {
                SizeBytes = sizeBytes,
                Replicas = replicas,
                Months = months,
                PricePerGbMonth = PricePerGbMonth,
                MonthlyFee = Math.Round(total / months, 10),
                TotalFee = total,
                RenewalFee = RenewalFeeFor(monthly),
                EgressQuotaBytesPerMonth = sizeBytes * EgressQuotaMultiplier
            };
            if (!string.IsNullOrWhiteSpace(owner) && await IsFreeTierEligibleAsync(owner))
            {
                var used = await GetFreeTierUsedBytesAsync(owner);
                var remaining = Math.Max(0, FreeTierStorageBytes - used);
                quote.FreeTierRemainingBytes = remaining;
                quote.FreeTier = sizeBytes * replicas <= remaining;
                if (quote.FreeTier)
                {
                    // §10: the network fund pays nodes at normal rates for the bytes actually stored. The per-contract
                    // minimum is an anti-dust rule for paid contracts; applied here it would cost the fund 10 D per like.
                    quote.MonthlyFee = monthly;
                    quote.TotalFee = Math.Round(monthly * months, 10);
                }
            }
            return quote;
        }

        /// <summary>
        /// Exactly what registering an item will take from its owner's wallet, using the same rules as
        /// <see cref="OpenStorageEscrowAsync"/>: paid replica count, the social allowance, the free tier and
        /// EnforceFees. Apps show <see cref="StorageCharge.OwnerPays"/> before uploading, so the fee shown is the fee charged.
        /// </summary>
        public async Task<StorageCharge> QuoteChargeAsync(string? owner, long sizeBytes, int storeNodeCount, int replicas,
            int months, bool social)
        {
            // Paid replica count: at least the Stores uploaded to, at most MaxReplicas and the Stores that exist.
            // Missing copies are made by the repair job from a Store that holds the file.
            var activeStores = (await _storage.GetActiveNodesAsync())?.Count ?? 0;
            var target = Math.Max(storeNodeCount, Math.Min(replicas, Math.Min(MaxReplicas, Math.Max(activeStores, 1))));
            target = Math.Max(1, target);
            // §10: social items (reaction, comment, chat message) are free. They use the free quota while it lasts;
            // beyond it they are recorded unfunded instead of charging the owner. Only small items qualify.
            social = social && sizeBytes > 0 && sizeBytes <= SocialItemMaxBytes && SocialMonthlyBytes > 0;
            if (social && !string.IsNullOrWhiteSpace(owner) &&
                await GetSocialBytesThisMonthAsync(owner, DateTime.UtcNow) + sizeBytes * target > SocialMonthlyBytes)
                social = false;
            var quote = await QuoteAsync(owner, sizeBytes, target, months);
            var ownerPays = !_wallet.EnforceFees || social || quote.FreeTier ? 0m : quote.TotalFee;
            return new StorageCharge(quote, target, social, ownerPays);
        }

        /// <summary>
        /// The owner deleted these items: their contracts end after the months already paid (no further charge, and
        /// an old prepaid contract is not switched to monthly billing). Items of other owners are skipped.
        /// Returns how many were stopped.
        /// </summary>
        public async Task<int> StopRenewalAsync(string owner, IEnumerable<string> fileIds)
        {
            var stopped = 0;
            foreach (var id in fileIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().Take(200))
            {
                var e = await GetEscrowAsync(id);
                if (e == null || !SameKey(e.OwnerPublicKey, owner) || e.RenewalStopped) continue;
                e.RenewalStopped = true; // also keeps an old prepaid contract from switching to monthly billing
                e.ContractType = "TimeFixed";
                if (e.Status == "Grace") { End(e, "Closed", DateTime.UtcNow); await RefundRemainderAsync(e, "Deleted by owner"); }
                await SaveEscrowAsync(e);
                stopped++;
            }
            return stopped;
        }

        // ---------------- on-chain storage terms (StorageTerm txs) ----------------

        private const string CTermQ = "termq";

        public class TermQueueItem
        {
            public string Id { get; set; } = "";
            public DateTime At { get; set; } = DateTime.UtcNow;
        }

        /// <summary>Saves the escrow and, when what a Store must know changed, queues it for the next StorageTerm tx.</summary>
        private async Task SaveEscrowAsync(StorageEscrow e)
        {
            var key = TermKey(e);
            if (e.LastTermKey != key)
            {
                e.LastTermKey = key;
                await _docs.PutAsync(CTermQ, e.Id, new TermQueueItem { Id = e.Id });
            }
            await _docs.PutAsync(CEscrow, e.Id, e);
        }

        private string TermKey(StorageEscrow e)
        {
            var t = TermFor(e, includeBlocks: false);
            return $"{t.Status}|{t.PaidUntil}|{t.DeleteAfter}|{e.Blocks.Count}";
        }

        /// <summary>
        /// What the chain says about an item: paid until when, and from when a Store may delete it.
        /// Paid (or free) items: paidUntil + grace + StoreDeleteAfterDays, so a missed renewal still gets the full
        /// grace. Deleted by the owner: paidUntil + StoreDeleteAfterDays. Ended: EndedAt + StoreDeleteAfterDays.
        /// </summary>
        public StorageTermNetworkTx.Entry TermFor(StorageEscrow e, bool includeBlocks)
        {
            var paidUntil = e.StartAt.AddMonths(Math.Max(e.Months, 0));
            var keep = TimeSpan.FromDays(StoreDeleteAfterDays);
            string status;
            DateTime deleteAfter;
            if (e.Status is "Expired" or "Closed" or "Lost")
            {
                status = "ended";
                deleteAfter = (e.EndedAt ?? DateTime.UtcNow) + keep;
                if (e.Status == "Closed" && e.EndedAt != null && e.EndedAt < paidUntil) paidUntil = e.EndedAt.Value;
            }
            else if (e.RenewalStopped || (e.ContractType != "AutoRenew" && !CanConvertToMonthly(e)))
            {
                status = "stopped";
                deleteAfter = paidUntil + keep;
            }
            else if (e.Status == "Grace")
            {
                status = "grace";
                var graceEnd = e.GraceUntil ?? paidUntil.AddDays(GraceDays);
                deleteAfter = (graceEnd > paidUntil.AddDays(GraceDays) ? graceEnd : paidUntil.AddDays(GraceDays)) + keep;
            }
            else
            {
                status = "active";
                deleteAfter = paidUntil.AddDays(GraceDays) + keep;
            }
            return new StorageTermNetworkTx.Entry(e.Id, status, StorageTermNetworkTx.Unix(paidUntil), StorageTermNetworkTx.Unix(deleteAfter),
                includeBlocks && e.Blocks.Count > 0 ? e.Blocks.Select(b => b.BlockId).ToList() : null);
        }

        /// <summary>Queued items for the next StorageTerm tx (oldest first), with their entries.</summary>
        public async Task<List<StorageTermNetworkTx.Entry>> PendingTermsAsync(int max = StorageTermNetworkTx.MaxEntries)
        {
            var queued = (await _docs.ListAsync<TermQueueItem>(CTermQ)).OrderBy(q => q.At).Take(max).ToList();
            var entries = new List<StorageTermNetworkTx.Entry>();
            foreach (var q in queued)
            {
                var e = await GetEscrowAsync(q.Id);
                if (e == null) { await _docs.DeleteAsync(CTermQ, q.Id); continue; }
                // Block ids go on chain once, so a Store knows which blocks belong to the item.
                entries.Add(TermFor(e, includeBlocks: !e.TermBlocksPublished));
            }
            return entries;
        }

        /// <summary>The tx carrying these entries was accepted by a Blocker: clear them from the queue.</summary>
        public async Task MarkTermsPublishedAsync(IEnumerable<StorageTermNetworkTx.Entry> entries)
        {
            foreach (var t in entries)
            {
                var e = await GetEscrowAsync(t.FileId);
                if (e != null && t.Blocks != null && !e.TermBlocksPublished)
                {
                    e.TermBlocksPublished = true;
                    await _docs.PutAsync(CEscrow, e.Id, e);
                }
                // Re-queued meanwhile with a newer term? Its key differs from what was sent: keep it queued.
                if (e == null || TermFor(e, false) with { Blocks = null } == t with { Blocks = null })
                    await _docs.DeleteAsync(CTermQ, t.FileId);
            }
        }

        /// <summary>Once: queue every escrow, so the chain holds a term for items stored before StorageTerm existed.</summary>
        public async Task<int> QueueAllTermsOnceAsync()
        {
            const string marker = "storage-terms-backfill-v1";
            if (await _docs.GetAsync<OpsMarker>(COps, marker) != null) return 0;
            var all = await _docs.ListAsync<StorageEscrow>(CEscrow);
            foreach (var e in all)
            {
                e.LastTermKey = TermKey(e);
                await _docs.PutAsync(CEscrow, e.Id, e);
                await _docs.PutAsync(CTermQ, e.Id, new TermQueueItem { Id = e.Id, At = e.StartAt });
            }
            await _docs.PutAsync(COps, marker, new OpsMarker { Id = marker, DoneAt = DateTime.UtcNow, Summary = $"{all.Count} escrows queued" });
            return all.Count;
        }

        /// <summary>Days a Store keeps an Expired or Closed item before deleting it (a last chance to notice).</summary>
        public int StoreDeleteAfterDays => (int)Math.Max(0m, Dec("Economics:Storage:StoreDeleteAfterDays", 7m));

        /// <summary>How far back <see cref="EndedForNodeAsync"/> looks, so a Store that was offline still catches up.</summary>
        public const int EndedLookbackDays = 60;

        private static decimal NextFee(StorageEscrow e) => e.RenewalFee > 0 ? e.RenewalFee : e.MonthlyFee;

        /// <summary>
        /// What the owner's wallet must cover to keep their items: items in grace (renewal failed, deleted at
        /// <c>GraceUntil</c> + StoreDeleteAfterDays) and renewals due within <paramref name="withinDays"/>.
        /// Fees are the exact amounts renewal takes.
        /// </summary>
        public async Task<RenewalStatus> GetRenewalStatusAsync(string owner, DateTime? nowUtc = null, int withinDays = 7)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var mine = await _docs.ListAsync<StorageEscrow>(CEscrow,
                e => (e.Status == "Active" || e.Status == "Grace") && SameKey(e.OwnerPublicKey, owner));
            var status = new RenewalStatus { Balance = await _wallet.GetBalanceAsync(owner), DeleteAfterDays = StoreDeleteAfterDays };
            foreach (var e in mine)
            {
                if (e.RenewalStopped || (_wallet.EnforceFees && e.TotalFunded == 0 && !e.ConvertedToMonthly && e.ContractType == "AutoRenew"))
                    continue; // deleted by the owner, or unfunded (renews for nothing)
                if (e.Status == "Grace")
                {
                    status.GraceCount++;
                    status.GraceFee += NextFee(e);
                    if (e.GraceUntil is { } g && (status.GraceUntil == null || g < status.GraceUntil)) status.GraceUntil = g;
                    continue;
                }
                var due = e.StartAt.AddMonths(e.Months);
                if (due > now.AddDays(withinDays)) continue;
                decimal fee;
                if (e.ContractType == "AutoRenew") fee = NextFee(e);
                else if (CanConvertToMonthly(e))
                {
                    var replicas = Math.Max(1, e.TargetReplicas > 0 ? e.TargetReplicas : e.StoreNodeIds.Count);
                    fee = RenewalFeeFor(MonthlyStorageFee(e.SizeBytes, replicas));
                }
                else continue;
                if (!_wallet.EnforceFees || fee <= 0) continue;
                status.DueCount++;
                status.DueFee += fee;
                if (status.FirstDue == null || due < status.FirstDue) status.FirstDue = due;
            }
            status.Shortfall = Math.Max(0m, status.GraceFee + status.DueFee - status.Balance);
            return status;
        }

        /// <summary>
        /// Items a Store may now delete: Expired or Closed at least StoreDeleteAfterDays ago (within the lookback),
        /// held by <paramref name="nodeId"/>. Stores poll this; Central never deletes anything on a Store.
        /// </summary>
        public async Task<List<StorageEscrow>> EndedForNodeAsync(string nodeId, DateTime? nowUtc = null, int max = 500)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var before = now.AddDays(-StoreDeleteAfterDays);
            var after = before.AddDays(-EndedLookbackDays);
            var ended = await _docs.ListAsync<StorageEscrow>(CEscrow,
                e => (e.Status == "Expired" || e.Status == "Closed") && e.EndedAt != null &&
                     e.EndedAt <= before && e.EndedAt >= after && e.StoreNodeIds.Contains(nodeId));
            return ended.OrderBy(e => e.EndedAt).Take(max).ToList();
        }

        /// <summary>
        /// A Store deleted an ended post and reports the reactions and comments on it: stop renewing them, so
        /// their owners stop paying for items nobody can see. Only items the Store holds are touched, and only
        /// when the parent really ended. They run out the month already paid, then close like any other item.
        /// </summary>
        public async Task<int> StopRenewalOfChildrenAsync(string nodeId, string parentFileId, IEnumerable<string> childFileIds, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var parent = await GetEscrowAsync(parentFileId);
            if (parent == null || parent.Status is not ("Expired" or "Closed") || parent.EndedAt == null ||
                parent.EndedAt > now.AddDays(-StoreDeleteAfterDays))
                return 0;
            var stopped = 0;
            foreach (var id in childFileIds.Where(i => !string.IsNullOrWhiteSpace(i) && i != parentFileId).Distinct().Take(500))
            {
                var e = await GetEscrowAsync(id);
                if (e == null || e.RenewalStopped || !e.StoreNodeIds.Contains(nodeId)) continue;
                e.RenewalStopped = true;
                e.ContractType = "TimeFixed";
                if (e.Status == "Grace") { End(e, "Closed", now); await RefundRemainderAsync(e, "Parent post ended"); }
                await SaveEscrowAsync(e);
                stopped++;
            }
            return stopped;
        }

        public async Task<StorageEscrow?> GetEscrowAsync(string fileId) => await _docs.GetAsync<StorageEscrow>(CEscrow, fileId);

        /// <summary>
        /// Fund the storage escrow for a newly registered file (all replicas). Free-tier uploads are paid by the
        /// network fund; otherwise the owner pays. While Economics:EnforceFees=false the escrow is recorded
        /// unfunded so proofs still run but nobody is charged.
        /// </summary>
        public async Task<(bool Ok, int Status, string Error, StorageEscrow? Escrow, StorageQuote? Quote)> OpenStorageEscrowAsync(
            string owner, string fileId, long sizeBytes, IReadOnlyList<string> storeNodeIds, int months,
            string? contractType, IReadOnlyList<FileBlockHash>? blocks, bool social = false, int replicas = 0)
        {
            var charge = await QuoteChargeAsync(owner, sizeBytes, storeNodeIds.Distinct().Count(), replicas, months, social);
            var target = charge.Replicas;
            if (social && !charge.Social)
                _log?.LogInformation("Social budget used up for {Owner}; {FileId} billed as a normal upload", owner, fileId);
            social = charge.Social;
            var existing = await GetEscrowAsync(fileId);
            if (existing != null)
            {
                if (!SameKey(existing.OwnerPublicKey, owner)) return (false, 409, "fileId already registered by another owner", null, null);
                var missing = storeNodeIds.Where(id => !existing.StoreNodeIds.Contains(id)).ToList();
                if (existing.Blocks.Count == 0 && blocks is { Count: > 0 }) existing.Blocks = blocks.ToList();
                if (missing.Count == 0)
                {
                    await SaveEscrowAsync(existing);
                    return (true, 200, "", existing, null);
                }
                return (false, 409, "Escrow already open for this file; register new replicas under a new fileId", existing, null);
            }

            var type = string.Equals(contractType, "AutoRenew", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(contractType, "OpenEnded", StringComparison.OrdinalIgnoreCase)
                ? "AutoRenew" : "TimeFixed";
            var quote = charge.Quote;
            var escrow = new StorageEscrow
            {
                Id = fileId,
                OwnerPublicKey = owner,
                ContractType = type,
                SizeBytes = sizeBytes,
                StoreNodeIds = storeNodeIds.Distinct().ToList(),
                Months = quote.Months,
                MonthlyFee = quote.MonthlyFee,
                RenewalFee = type == "AutoRenew" ? quote.RenewalFee : 0m,
                StartAt = DateTime.UtcNow,
                Blocks = blocks?.Where(b => !string.IsNullOrWhiteSpace(b.BlockId) && !string.IsNullOrWhiteSpace(b.Sha256))
                    .Select(b => new FileBlockHash { BlockId = b.BlockId, Sha256 = b.Sha256.ToLowerInvariant(), BlockIndex = b.BlockIndex }).ToList() ?? new()
            };
            escrow.TargetReplicas = target;

            if (_wallet.EnforceFees)
            {
                var funded = false;
                if (quote.FreeTier)
                {
                    var r = await _wallet.ApplyMovesAsync(new[]
                    {
                        new LedgerMove(EconAccounts.NetworkFund, EconAccounts.Escrow(fileId), quote.TotalFee, "escrow:fund", "Free tier storage (network fund)")
                    }, fileId);
                    if (r.Ok) { funded = true; escrow.FreeTier = true; escrow.PayerAccount = EconAccounts.NetworkFund; }
                    else
                    {
                        // The free tier is a promise to the user, not a loan: when the network fund is short the
                        // escrow is recorded unfunded (still inside the user's quota) instead of charging the owner,
                        // up to a network-wide budget so an empty fund cannot mean unlimited unpaid storage.
                        var unfunded = (await _docs.ListAsync<StorageEscrow>(CEscrow, x => x.FreeTier && x.TotalFunded == 0 && (x.Status == "Active" || x.Status == "Grace")))
                            .Sum(x => x.SizeBytes * Math.Max(1, x.StoreNodeIds.Count));
                        // Social items have their own monthly budget and are never refused here.
                        if (!social && unfunded + quote.SizeBytes * quote.Replicas > FreeTierUnfundedBudgetBytes)
                            return (false, 402, "Free storage is paused until the network fund is topped up", null, quote);
                        _log?.LogWarning("Free tier funding failed for {FileId}: {Err}; recorded unfunded", fileId, r.Error);
                        escrow.FreeTier = true;
                        escrow.Social = social;
                        escrow.PayerAccount = "";
                        await SaveEscrowAsync(escrow);
                        return (true, 200, "", escrow, quote);
                    }
                }
                if (!funded && social)
                {
                    escrow.PayerAccount = "";
                    escrow.Social = true;
                    await SaveEscrowAsync(escrow);
                    return (true, 200, "", escrow, quote);
                }
                if (!funded)
                {
                    var r = await _wallet.ApplyMovesAsync(new[]
                    {
                        new LedgerMove(owner, EconAccounts.Escrow(fileId), quote.TotalFee, "escrow:fund",
                            $"Storage {quote.SizeBytes} B × {quote.Replicas} replicas × {quote.Months} mo")
                    }, fileId);
                    if (!r.Ok) return (false, 402, $"Storage fee {quote.TotalFee} D not paid: {r.Error}", null, quote);
                    escrow.PayerAccount = owner;
                }
                escrow.TotalFunded = quote.TotalFee;
            }
            else
            {
                escrow.PayerAccount = "";
                escrow.FreeTier = quote.FreeTier;
                escrow.Social = social;
            }

            await SaveEscrowAsync(escrow);
            return (true, 200, "", escrow, quote);
        }

        /// <summary>How long after opening an escrow its owner may cancel it because the upload failed.</summary>
        public TimeSpan EscrowCancelWindow => TimeSpan.FromMinutes((double)Math.Max(1m, Dec("Economics:Storage:CancelWindowMinutes", 60m)));

        /// <summary>
        /// The App paid for storage, then the upload failed. Close the escrow and refund whoever paid, so the
        /// owner is not charged for a file that never arrived and the Stores are not challenged (and slashed)
        /// for blocks they never received. Refused once any Store holds the complete file, so a finished upload
        /// cannot be stored for free by cancelling it afterwards.
        /// </summary>
        public async Task<(bool Ok, int Status, string Error, decimal Refunded)> CancelFailedUploadAsync(string owner, string fileId, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var e = await GetEscrowAsync(fileId);
            if (e == null) return (false, 404, "No storage contract for this file", 0);
            if (!SameKey(e.OwnerPublicKey, owner)) return (false, 403, "Not the owner of this file", 0);
            if (e.Status == "Closed") return (true, 200, "", 0); // already cancelled: idempotent
            if (e.Status != "Active" || e.MonthsSettled > 0 || e.Released > 0)
                return (false, 409, "Storage contract already running; it can no longer be cancelled", 0);
            if (now - e.StartAt > EscrowCancelWindow)
                return (false, 409, "Too late to cancel: the cancel window has passed", 0);
            if (e.Blocks.Count == 0)
                return (false, 409, "No block hashes registered, so the upload cannot be checked", 0);

            if (await AnyStoreHoldsFileAsync(e))
                return (false, 409, "The upload completed on a Store; the contract stays", 0);

            var refunded = await _wallet.GetBalanceAsync(EconAccounts.Escrow(e.Id));
            await RefundRemainderAsync(e, "Upload failed; cancelled by owner");
            End(e, "Closed", now);
            await SaveEscrowAsync(e);
            _log?.LogInformation("Escrow {FileId} cancelled after a failed upload; refunded {Refunded} D", fileId, refunded);
            return (true, 200, "", Math.Max(0, refunded));
        }

        /// <summary>True when at least one Store returns every registered block with the right hash.</summary>
        private async Task<bool> AnyStoreHoldsFileAsync(StorageEscrow e)
        {
            if (_http == null) return false;
            var nodes = (await _storage.GetNodesAsync()).ToDictionary(n => n.Id);
            foreach (var nodeId in e.StoreNodeIds)
            {
                if (!nodes.TryGetValue(nodeId, out var node)) continue;
                var complete = true;
                foreach (var block in e.Blocks)
                {
                    bool ok;
                    try { (ok, _) = await ChallengeNodeAsync(NodeAddress.Of(node.Endpoint, node.TcpEndpoint, node.PublicKey), block); }
                    catch { ok = false; }
                    if (!ok) { complete = false; break; }
                }
                if (complete) return true;
            }
            return false;
        }

        // ---------------- proof-of-storage (§6) ----------------

        public static int MonthIndex(StorageEscrow e, DateTime now)
        {
            var months = (now.Year - e.StartAt.Year) * 12 + now.Month - e.StartAt.Month;
            if (now < e.StartAt.AddMonths(months)) months--;
            return Math.Max(0, months);
        }

        private static string ProofId(string fileId, string nodeId, int monthIndex) => $"{fileId}|{nodeId}|m{monthIndex}";

        public async Task<(int Challenged, int Passed)> RunChallengesAsync(int maxFiles = 500)
        {
            var nodes = (await _storage.GetNodesAsync()).ToDictionary(n => n.Id);
            var escrows = (await _docs.ListAsync<StorageEscrow>(CEscrow, e => (e.Status == "Active" || e.Status == "Grace") && e.Blocks.Count > 0))
                .OrderBy(_ => Random.Shared.Next()).Take(maxFiles).ToList();
            int challenged = 0, passed = 0;
            var now = DateTime.UtcNow;
            foreach (var e in escrows)
            {
                var block = e.Blocks[Random.Shared.Next(e.Blocks.Count)];
                foreach (var nodeId in e.StoreNodeIds.Where(id => !e.FailedNodeIds.Contains(id)))
                {
                    string? error = null;
                    var ok = false;
                    if (!nodes.TryGetValue(nodeId, out var node)) error = "node not in registry";
                    else
                    {
                        try { (ok, error) = await ChallengeNodeAsync(NodeAddress.Of(node.Endpoint, node.TcpEndpoint, node.PublicKey), block); }
                        catch (Exception ex) { error = ex.Message; }
                    }
                    await RecordProofAsync(e, nodeId, ok, error, now);
                    challenged++;
                    if (ok) passed++;
                }
            }
            return (challenged, passed);
        }

        public async Task RecordProofAsync(StorageEscrow e, string nodeId, bool ok, string? error, DateTime now)
        {
            var id = ProofId(e.Id, nodeId, MonthIndex(e, now));
            var tally = await _docs.GetAsync<ProofTally>(CProof, id) ?? new ProofTally
            {
                Id = id, Period = Epoch(now), FileId = e.Id, StoreNodeId = nodeId
            };
            tally.Total++;
            if (ok) tally.Passed++;
            tally.LastAt = now;
            tally.LastError = ok ? null : error;
            await _docs.PutAsync(CProof, id, tally);
            if (ok && !e.VerifiedNodeIds.Contains(nodeId))
            {
                e.VerifiedNodeIds.Add(nodeId);
                await SaveEscrowAsync(e);
            }
        }

        private async Task<(bool Ok, string? Error)> ChallengeNodeAsync(string endpoint, FileBlockHash block)
        {
            var (ok, err, _, _) = await FetchBlockAsync(endpoint, block);
            return (ok, err);
        }

        /// <summary>Challenge a Store for one block; on success returns the verified bytes and its block index.</summary>
        private async Task<(bool Ok, string? Error, byte[]? Data, int BlockIndex)> FetchBlockAsync(string endpoint, FileBlockHash block)
        {
            if (_http == null) return (false, "no http client", null, -1);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            using var client = _http.CreateClient("econ");
            var json = JsonSerializer.Serialize(new { blockId = block.BlockId, nonce });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/api/proof/challenge")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AuthorizeStoreCall(req, json);
            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}", null, -1);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string? data = root.TryGetProperty("dataBase64", out var d) ? d.GetString() : null;
            string? echo = root.TryGetProperty("nonce", out var n) ? n.GetString() : null;
            var index = root.TryGetProperty("blockIndex", out var bi) && bi.ValueKind == JsonValueKind.Number ? bi.GetInt32() : block.BlockIndex;
            if (data == null || echo != nonce) return (false, "bad challenge response", null, -1);
            var bytes = Convert.FromBase64String(data);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return hash == block.Sha256 ? (true, null, bytes, index) : (false, "hash mismatch", null, -1);
        }

        // ---------------- monthly settlement (§6) ----------------

        public async Task<int> SettleDueAsync(DateTime? nowOverride = null)
        {
            var now = nowOverride ?? DateTime.UtcNow;
            var nodes = (await _storage.GetNodesAsync()).ToDictionary(n => n.Id);
            var escrows = await _docs.ListAsync<StorageEscrow>(CEscrow, e => e.Status == "Active" || e.Status == "Grace");
            var settledMonths = 0;
            foreach (var e in escrows)
            {
                if (e.Status == "Active")
                {
                    while (e.MonthsSettled < e.Months && e.StartAt.AddMonths(e.MonthsSettled + 1) <= now)
                    {
                        await SettleMonthAsync(e, e.MonthsSettled, nodes);
                        e.MonthsSettled++;
                        settledMonths++;
                    }
                    if (AutoRepair && e.Blocks.Count > 0 && UnderReplicated(e))
                    {
                        try { await RepairEscrowAsync(e, nodes); }
                        catch (Exception ex) { _log?.LogWarning("Repair {FileId} failed: {Err}", e.Id, ex.Message); }
                    }
                    if (e.StoreNodeIds.Count > 0 && e.StoreNodeIds.All(id => e.FailedNodeIds.Contains(id)))
                    {
                        End(e, "Lost", now);
                        await RefundRemainderAsync(e, "All replicas failed proof-of-storage");
                    }
                    else if (e.MonthsSettled >= e.Months)
                    {
                        if (e.ContractType != "AutoRenew" && CanConvertToMonthly(e)) ConvertToMonthly(e);
                        if (e.ContractType == "AutoRenew") await TryRenewAsync(e, now);
                        else
                        {
                            End(e, "Closed", now);
                            await RefundRemainderAsync(e, "Contract ended");
                        }
                    }
                }
                else if (e.Status == "Grace")
                {
                    await TryRenewAsync(e, now);
                    if (e.Status == "Grace" && e.GraceUntil <= now)
                    {
                        End(e, "Expired", now);
                        await RefundRemainderAsync(e, "Grace period ended");
                        await _storage.DeactivateStoredFileAsync(e.Id);
                    }
                }
                await SaveEscrowAsync(e);
            }
            return settledMonths;
        }

        private async Task SettleMonthAsync(StorageEscrow e, int monthIndex, Dictionary<string, StoreNode> nodes)
        {
            var replicas = Math.Max(1, e.TargetReplicas > 0 ? e.TargetReplicas : e.StoreNodeIds.Count);
            var perNode = e.TotalFunded > 0 ? Math.Round(e.MonthlyFee / replicas, 10) : 0m;
            foreach (var nodeId in e.StoreNodeIds)
            {
                if (e.FailedNodeIds.Contains(nodeId)) continue;
                var tally = await _docs.GetAsync<ProofTally>(CProof, ProofId(e.Id, nodeId, monthIndex));
                decimal rate = tally == null || tally.Total == 0
                    ? (NoChallengeCountsAsPass || e.Blocks.Count == 0 ? 1m : 0m) // no block hashes (e.g. social metadata): cannot be challenged
                    : (decimal)tally.Passed / tally.Total;

                nodes.TryGetValue(nodeId, out var node);
                decimal release = rate >= ProofFullPassRate ? perNode : rate >= ProofMinPassRate ? Math.Round(perNode * rate, 10) : 0m;

                if (rate < ProofMinPassRate)
                {
                    e.FailedNodeIds.Add(nodeId);
                    // A Store that never once returned a registered block may have been handed hashes that do not
                    // match what was uploaded (the uploader supplies them). Drop it from the contract, unpaid, but
                    // do not slash: otherwise anyone could register junk hashes to burn a competitor's bond.
                    if (e.Blocks.Count > 0 && !e.VerifiedNodeIds.Contains(nodeId))
                    {
                        _log?.LogWarning("Store {Node} never passed a challenge for {FileId}; dropped without slashing", nodeId, e.Id);
                        continue;
                    }
                    if (node != null)
                        await SlashAsync(node.PublicKey, SlashStorageFailPercent, $"Proof-of-storage pass rate {rate:P0} for {e.Id} month {monthIndex}",
                            e.Id, new[] { e.OwnerPublicKey });
                }

                var escrowBal = await _wallet.GetBalanceAsync(EconAccounts.Escrow(e.Id));
                release = Math.Min(release, escrowBal);
                if (release > 0 && node != null)
                {
                    var r = await _wallet.SettleFeeAsync(EconAccounts.Escrow(e.Id), release, FeeTypes.Storage,
                        new[] { (node.PublicKey, 1m) }, e.Id, $"Storage month {monthIndex + 1} for {e.Id} (pass {rate:P0})", force: true);
                    if (r.Ok) e.Released += release;
                    else _log?.LogWarning("Escrow release failed {FileId}: {Err}", e.Id, r.Error);
                }
            }
        }

        private static void End(StorageEscrow e, string status, DateTime now)
        {
            e.Status = status;
            e.EndedAt = now;
            e.GraceUntil = null;
        }

        /// <summary>
        /// Old prepaid contracts (TimeFixed, e.g. 12 months) switch to monthly billing when the term runs out,
        /// unless the owner deleted the item. Needs an owner to bill; reactions stored unfunded under the old
        /// free social allowance are converted too, so from then on they are paid like everything else.
        /// </summary>
        private bool CanConvertToMonthly(StorageEscrow e) =>
            ConvertPrepaidBefore is { } cutoff && e.StartAt < cutoff &&
            !e.RenewalStopped && !string.IsNullOrWhiteSpace(e.OwnerPublicKey) && e.SizeBytes > 0;

        /// <summary>
        /// Prepaid contracts opened before this date (when monthly billing replaced prepaying 12 months) switch to
        /// monthly billing at the end of their term. Later TimeFixed contracts were asked for as fixed terms and
        /// simply end. Unset: none are converted.
        /// </summary>
        public DateTime? ConvertPrepaidBefore =>
            DateTime.TryParse(_config["Economics:Storage:ConvertPrepaidBefore"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                ? d : null;

        private void ConvertToMonthly(StorageEscrow e)
        {
            var replicas = Math.Max(1, e.TargetReplicas > 0 ? e.TargetReplicas : e.StoreNodeIds.Count);
            e.ContractType = "AutoRenew";
            e.ConvertedToMonthly = true;
            // The free tier is gone (§10): from now on the owner pays, at today's price.
            e.FreeTier = false;
            e.PayerAccount = e.OwnerPublicKey;
            e.RenewalFee = RenewalFeeFor(MonthlyStorageFee(e.SizeBytes, replicas));
        }

        private async Task TryRenewAsync(StorageEscrow e, DateTime now)
        {
            // Unfunded items (EnforceFees off, or the old free social allowance) renew for nothing, until an old
            // contract is converted to monthly billing.
            if (!_wallet.EnforceFees || (e.TotalFunded == 0 && !e.ConvertedToMonthly))
            {
                e.Months++;
                e.Status = "Active";
                e.GraceUntil = null;
                return;
            }
            var payer = e.FreeTier ? EconAccounts.NetworkFund : e.OwnerPublicKey;
            // Renewals pay the storage actually used; the first month carried the minimum (opening) fee.
            var fee = e.RenewalFee > 0 ? e.RenewalFee : e.MonthlyFee;
            var r = await _wallet.ApplyMovesAsync(new[]
            {
                new LedgerMove(payer, EconAccounts.Escrow(e.Id), fee, "escrow:renew", $"Auto-renew month {e.Months + 1}")
            }, e.Id);
            if (r.Ok)
            {
                e.Months++;
                e.MonthlyFee = fee; // what the next settlement releases to the Stores
                e.TotalFunded += fee;
                e.Status = "Active";
                e.GraceUntil = null;
            }
            else if (e.Status != "Grace")
            {
                e.Status = "Grace";
                e.GraceUntil = now.AddDays(GraceDays);
            }
        }

        private async Task RefundRemainderAsync(StorageEscrow e, string reason)
        {
            var bal = await _wallet.GetBalanceAsync(EconAccounts.Escrow(e.Id));
            if (bal <= 0) return;
            var to = string.IsNullOrWhiteSpace(e.PayerAccount) ? e.OwnerPublicKey : e.PayerAccount;
            await _wallet.ApplyMovesAsync(new[] { new LedgerMove(EconAccounts.Escrow(e.Id), to, bal, "escrow:refund", reason) }, e.Id);
        }

        // ---------------- one-off: Store data lost to the old deploy (2026-11) ----------------

        /// <summary>Done-marker per cutoff, so the reconcile runs once.</summary>
        private const string COps = "ops";

        public class OpsMarker
        {
            public string Id { get; set; } = "";
            public DateTime DoneAt { get; set; }
            public string Summary { get; set; } = "";
        }

        /// <summary>
        /// Until 2026-11-17 every Store deploy deleted the Store's blocks (MSDeploy sync wiped the site's FileStorage).
        /// That was the operator's fault, not the Stores': for each escrow opened before <paramref name="startedBeforeUtc"/>,
        /// a Store that answers "block not found" (HTTP 404) is dropped from the contract without slashing; when no Store
        /// holds the file any more the escrow is marked Lost, its unused funds go back to the payer and the file is
        /// deactivated. A Store that is unreachable or answers anything else is left alone. Runs once per cutoff.
        /// </summary>
        public async Task<(int Checked, int NodesDropped, int Lost)> ReconcileStoreDataLossAsync(DateTime startedBeforeUtc, bool force = false)
        {
            var markerId = $"reconcile-loss-{startedBeforeUtc:yyyyMMddTHHmmssZ}";
            if (!force && await _docs.GetAsync<OpsMarker>(COps, markerId) != null) return (0, 0, 0);
            if (_http == null) return (0, 0, 0);

            var nodes = (await _storage.GetNodesAsync()).ToDictionary(n => n.Id);
            var escrows = await _docs.ListAsync<StorageEscrow>(CEscrow, e =>
                (e.Status == "Active" || e.Status == "Grace") && e.Blocks.Count > 0 && e.StartAt < startedBeforeUtc);
            int dropped = 0, lost = 0;
            foreach (var e in escrows)
            {
                var holders = 0;
                var unknown = 0;
                var lostOn = new List<string>();
                foreach (var nodeId in e.StoreNodeIds.Where(id => !e.FailedNodeIds.Contains(id)))
                {
                    if (!nodes.TryGetValue(nodeId, out var node)) { unknown++; continue; }
                    string state = "holds";
                    foreach (var block in e.Blocks)
                    {
                        string? err;
                        bool ok;
                        try { (ok, err) = await ChallengeNodeAsync(NodeAddress.Of(node.Endpoint, node.TcpEndpoint, node.PublicKey), block); }
                        catch (Exception ex) { ok = false; err = ex.Message; }
                        if (ok) continue;
                        state = err == "HTTP 404" ? "lost" : "unknown";
                        break;
                    }
                    if (state == "holds") holders++;
                    else if (state == "lost") lostOn.Add(nodeId);
                    else unknown++;
                }
                if (lostOn.Count == 0) continue;

                foreach (var nodeId in lostOn)
                {
                    e.FailedNodeIds.Add(nodeId);     // settlement skips failed nodes: no pay, no slash
                    e.VerifiedNodeIds.Remove(nodeId);
                    dropped++;
                }
                if (holders == 0 && unknown == 0)
                {
                    End(e, "Lost", DateTime.UtcNow);
                    await RefundRemainderAsync(e, "Blocks deleted by a Store redeploy (operator fault)");
                    await _storage.DeactivateStoredFileAsync(e.Id);
                    lost++;
                }
                else if (holders > 0 && AutoRepair)
                {
                    try { await RepairEscrowAsync(e, nodes); }
                    catch (Exception ex) { _log?.LogWarning("Repair {FileId} after data loss failed: {Err}", e.Id, ex.Message); }
                }
                await SaveEscrowAsync(e);
                _log?.LogWarning("Data-loss reconcile {FileId}: lost on {Lost}, holders {Holders}, status {Status}",
                    e.Id, string.Join(",", lostOn), holders, e.Status);
            }

            var summary = $"checked {escrows.Count}, nodes dropped {dropped}, files lost {lost}";
            await _docs.PutAsync(COps, markerId, new OpsMarker { Id = markerId, DoneAt = DateTime.UtcNow, Summary = summary });
            _log?.LogWarning("Data-loss reconcile before {Cutoff:o}: {Summary}", startedBeforeUtc, summary);
            return (escrows.Count, dropped, lost);
        }

        // ---------------- automatic re-replication (§6) ----------------

        /// <summary>Replace failed replicas for every active escrow (worker job "repair").</summary>
        public async Task<int> RepairReplicasAsync(int maxFiles = 200)
        {
            var nodes = (await _storage.GetNodesAsync()).ToDictionary(n => n.Id);
            var escrows = (await _docs.ListAsync<StorageEscrow>(CEscrow, e =>
                    (e.Status == "Active" || e.Status == "Grace") && e.Blocks.Count > 0 && UnderReplicated(e)))
                .Take(maxFiles).ToList();
            var added = 0;
            foreach (var e in escrows)
            {
                var n = await RepairEscrowAsync(e, nodes);
                if (n > 0) { added += n; await SaveEscrowAsync(e); }
            }
            return added;
        }

        /// <summary>
        /// Copy the file (Store metadata rows + every encrypted block) from a healthy replica onto new Stores
        /// until <see cref="StorageEscrow.TargetReplicas"/> healthy copies exist. Central only relays ciphertext;
        /// every block is checked against the hash the App registered. Returns the number of replicas added.
        /// </summary>
        public async Task<int> RepairEscrowAsync(StorageEscrow e, Dictionary<string, StoreNode> nodes)
        {
            if (_http == null || e.Blocks.Count == 0) return 0;
            var target = e.TargetReplicas > 0 ? e.TargetReplicas : e.StoreNodeIds.Count;
            var healthy = e.StoreNodeIds.Where(id => !e.FailedNodeIds.Contains(id) && nodes.ContainsKey(id)).ToList();
            var need = target - healthy.Count;
            if (need <= 0 || healthy.Count == 0) return 0;

            var candidates = nodes.Values
                .Where(n => n.IsActive && !e.StoreNodeIds.Contains(n.Id) && NodeAddress.Of(n.Endpoint, n.TcpEndpoint, n.PublicKey).Length > 0)
                .OrderBy(_ => Random.Shared.Next()).ToList();
            candidates = NodeOnline.Filter(candidates, n => n.LastSeen, _config); // never copy to a node that does not answer
            if (EnforceBonds) candidates = await FilterStoreNodesByBondAsync(candidates);

            var added = 0;
            foreach (var dst in candidates)
            {
                if (need <= 0) break;
                ReplicationEvent? ev = null;
                foreach (var srcId in healthy)
                {
                    ev = await CopyFileAsync(e, nodes[srcId], dst);
                    await _docs.PutAsync(CRepl, ev.Id, ev);
                    if (ev.Ok) break;
                }
                if (ev is not { Ok: true }) continue;
                e.StoreNodeIds.Add(dst.Id);
                if (!e.VerifiedNodeIds.Contains(dst.Id)) e.VerifiedNodeIds.Add(dst.Id); // CopyFileAsync challenged the new copy
                var existing = (await _storage.GetStoredFilesAsync()).FirstOrDefault(f => f.FileId == e.Id);
                await _storage.SaveStoredFileAsync(new StoredFile
                {
                    FileId = e.Id,
                    OwnerPublicKey = e.OwnerPublicKey,
                    StoreNodeId = dst.Id,
                    FileHash = existing?.FileHash ?? "",
                    FileSize = e.SizeBytes,
                    IsActive = true,
                    StoredAt = DateTime.UtcNow
                });
                need--;
                added++;
            }
            return added;
        }

        private async Task<JsonDocument?> PostInternalAsync(string endpoint, string path, object body)
        {
            using var client = _http!.CreateClient("econ");
            var json = JsonSerializer.Serialize(body);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}{path}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AuthorizeStoreCall(req, json);
            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{path}: HTTP {(int)resp.StatusCode}");
            var text = await resp.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
        }

        private async Task<ReplicationEvent> CopyFileAsync(StorageEscrow e, StoreNode src, StoreNode dst)
        {
            var ev = new ReplicationEvent { FileId = e.Id, SourceNodeId = src.Id, TargetNodeId = dst.Id };
            try
            {
                using (var meta = await PostInternalAsync(NodeAddress.Of(src.Endpoint, src.TcpEndpoint, src.PublicKey), "/api/proof/export-metadata", new { fileId = e.Id }))
                {
                    var items = meta?.RootElement.TryGetProperty("items", out var it) == true ? it.Clone() : default;
                    if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                        throw new InvalidOperationException("source has no metadata for file");
                    using var _ = await PostInternalAsync(NodeAddress.Of(dst.Endpoint, dst.TcpEndpoint, dst.PublicKey), "/api/proof/import-metadata", new { items });
                    ev.MetadataCopied = items.GetArrayLength();
                }
                foreach (var b in e.Blocks)
                {
                    var (ok, err, data, index) = await FetchBlockAsync(NodeAddress.Of(src.Endpoint, src.TcpEndpoint, src.PublicKey), b);
                    if (!ok || data == null) throw new InvalidOperationException($"block {b.BlockId}: {err}");
                    if (index < 0) throw new InvalidOperationException($"block {b.BlockId}: unknown block index");
                    using var _ = await PostInternalAsync(NodeAddress.Of(dst.Endpoint, dst.TcpEndpoint, dst.PublicKey), "/api/proof/import-block", new
                    {
                        fileId = e.Id, blockId = b.BlockId, blockIndex = index,
                        dataBase64 = Convert.ToBase64String(data), sha256 = b.Sha256
                    });
                    if (b.BlockIndex < 0) b.BlockIndex = index;
                    ev.BlocksCopied++;
                }
                var probe = e.Blocks[Random.Shared.Next(e.Blocks.Count)];
                var (verified, verr) = await ChallengeNodeAsync(NodeAddress.Of(dst.Endpoint, dst.TcpEndpoint, dst.PublicKey), probe);
                if (!verified) throw new InvalidOperationException($"new replica failed verification: {verr}");
                ev.Ok = true;
            }
            catch (Exception ex)
            {
                ev.Ok = false;
                ev.Error = ex.Message;
            }
            return ev;
        }

        public async Task<List<ReplicationEvent>> ListReplicationEventsAsync(string? fileId = null, int limit = 100) =>
            (await _docs.ListAsync<ReplicationEvent>(CRepl, r => fileId == null || r.FileId == fileId))
                .OrderByDescending(r => r.At).Take(Math.Clamp(limit, 1, 500)).ToList();

        // ---------------- bandwidth (§7) ----------------

        public async Task SetEgressCapAsync(string owner, decimal maxDPerMonth) =>
            await _docs.PutAsync(CEgressCap, owner, new EgressCap { Id = owner, MaxDPerMonth = Math.Max(0, maxDPerMonth) });

        public async Task<int> PullEgressReportsAsync()
        {
            if (_http == null) return 0;
            var count = 0;
            foreach (var node in await _storage.GetActiveNodesAsync())
            {
                try
                {
                    using var client = _http.CreateClient("econ");
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{NodeAddress.Of(node.Endpoint, node.TcpEndpoint, node.PublicKey)}/api/proof/egress/drain");
                    AuthorizeStoreCall(req, "");
                    using var resp = await client.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (!doc.RootElement.TryGetProperty("items", out var items)) continue;
                    var reported = new Dictionary<string, long>();
                    foreach (var it in items.EnumerateArray())
                    {
                        var fileId = it.TryGetProperty("fileId", out var f) ? f.GetString() : null;
                        var bytes = it.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0;
                        if (string.IsNullOrWhiteSpace(fileId) || bytes <= 0) continue;
                        reported[fileId] = (reported.TryGetValue(fileId, out var r0) ? r0 : 0) + bytes;
                    }

                    if (EgressReceiptMode == "require")
                    {
                        var receipts = doc.RootElement.TryGetProperty("receipts", out var rc) && rc.ValueKind == JsonValueKind.Array
                            ? rc.Deserialize<List<EgressReceipt>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                            : new List<EgressReceipt>();
                        var receipted = await TallyReceiptsAsync(node, receipts, DateTime.UtcNow);
                        // Bill the smaller of what the Store counted and what readers signed for.
                        reported = reported.ToDictionary(kv => kv.Key,
                            kv => Math.Min(kv.Value, receipted.TryGetValue(kv.Key, out var rb) ? rb : 0));
                    }

                    foreach (var (fileId, bytes) in reported)
                    {
                        if (bytes <= 0) continue;
                        await RecordEgressAsync(node.Id, node.PublicKey, fileId, bytes, DateTime.UtcNow);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogWarning("Egress pull from {Node} failed: {Err}", node.Endpoint, ex.Message);
                }
            }
            return count;
        }

        public static string ReceiptOrigin(string endpoint)
        {
            try { return new Uri(endpoint.Trim()).GetLeftPart(UriPartial.Authority).ToLowerInvariant(); }
            catch { return endpoint.Trim().TrimEnd('/').ToLowerInvariant(); }
        }

        /// <summary>Message a reader signs for a bandwidth receipt (must match the App).</summary>
        public static string ReceiptMessage(EgressReceipt r)
        {
            var items = string.Join(";", r.Items.Select(i => $"{i.Block}:{i.Bytes}"));
            return $"EGRESS|{r.Origin}|{r.Reader}|{r.Ts}|{Sha256Hex(items)}";
        }

        public static bool VerifyUserSignature(string publicKey, string message, string signatureBase64)
        {
            try
            {
                var pub = new NBitcoin.PubKey(publicKey);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope(message)));
                var sig = Convert.FromBase64String(signatureBase64);
                if (pub.Verify(new NBitcoin.uint256(hash), sig) || pub.Verify(new NBitcoin.uint256(hash, lendian: false), sig)) return true;
                var ecdsa = NBitcoin.Crypto.ECDSASignature.FromDER(sig).MakeCanonical();
                return pub.Verify(new NBitcoin.uint256(hash), ecdsa) || pub.Verify(new NBitcoin.uint256(hash, lendian: false), ecdsa);
            }
            catch { return false; }
        }

        /// <summary>
        /// Verify reader receipts forwarded by a Store and sum signed bytes per owner file. A receipt counts once
        /// (by signature), must name this Store's origin, be recent, and only blocks Central knows are counted.
        /// </summary>
        public async Task<Dictionary<string, long>> TallyReceiptsAsync(StoreNode node, List<EgressReceipt> receipts, DateTime now)
        {
            var result = new Dictionary<string, long>();
            if (receipts.Count == 0) return result;
            var origin = ReceiptOrigin(node.Endpoint);
            var escrows = await _docs.ListAsync<StorageEscrow>(CEscrow, e => e.StoreNodeIds.Contains(node.Id));
            var blockToFile = new Dictionary<string, string>();
            foreach (var e in escrows) foreach (var b in e.Blocks) blockToFile[b.BlockId] = e.Id;
            var fileIds = escrows.Select(e => e.Id).ToHashSet();
            var nowUnix = new DateTimeOffset(now).ToUnixTimeSeconds();

            foreach (var r in receipts)
            {
                if (string.IsNullOrWhiteSpace(r.Signature) || string.IsNullOrWhiteSpace(r.Reader)) continue;
                if (!string.Equals(r.Origin, origin, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Ts > nowUnix + 600 || r.Ts < nowUnix - EgressReceiptMaxAgeDays * 86400L) continue;
                if (r.Items.Count == 0 || r.Items.Count > 5000) continue;
                var seenId = Sha256Hex(r.Signature);
                if (await _docs.GetAsync<EconDoc>(CReceipt, seenId) != null) continue;
                if (!VerifyUserSignature(r.Reader, ReceiptMessage(r), r.Signature)) continue;
                await _docs.PutAsync(CReceipt, seenId, new EconDoc { Collection = CReceipt, Id = seenId, Json = r.Reader });
                foreach (var it in r.Items)
                {
                    if (it.Bytes <= 0) continue;
                    string? fileId = blockToFile.TryGetValue(it.Block, out var f) ? f : null;
                    if (fileId == null && it.Block.Contains('/'))
                    {
                        var prefix = it.Block[..it.Block.LastIndexOf('/')];
                        if (fileIds.Contains(prefix)) fileId = prefix;
                    }
                    if (fileId == null) continue;
                    result[fileId] = (result.TryGetValue(fileId, out var c) ? c : 0) + it.Bytes;
                }
            }
            return result;
        }

        public async Task RecordEgressAsync(string nodeId, string nodePublicKey, string fileId, long bytes, DateTime now)
        {
            var e = await GetEscrowAsync(fileId);
            if (e == null || !e.StoreNodeIds.Contains(nodeId)) return;
            // Sanity cap on store-reported numbers (Stores are paid per byte; clamp obvious inflation).
            bytes = (long)Math.Min(bytes, e.SizeBytes * MaxReportedReadsPerFile);

            var period = Epoch(now);
            var id = $"{period}|{fileId}";
            var usage = await _docs.GetAsync<EgressUsage>(CEgress, id) ?? new EgressUsage { Id = id, Period = period, FileId = fileId };
            usage.Bytes += bytes;

            var quota = (long)(e.SizeBytes * EgressQuotaMultiplier);
            var over = Math.Max(0, usage.Bytes - quota) - usage.ChargedBytes;
            if (over > 0)
            {
                var cost = Math.Round(over / (1024m * 1024m * 1024m) * EgressPricePerGb, 10);
                var cap = await _docs.GetAsync<EgressCap>(CEgressCap, e.OwnerPublicKey);
                var spendId = $"{period}|{e.OwnerPublicKey}";
                var spent = await _docs.GetAsync<EgressUsage>(CEgressSpend, spendId) ?? new EgressUsage { Id = spendId, Period = period };
                if (cap != null && cap.MaxDPerMonth > 0 && spent.ChargedD + cost > cap.MaxDPerMonth)
                {
                    e.EgressCapped = true;
                    await SaveEscrowAsync(e);
                }
                else if (cost > 0)
                {
                    var r = await _wallet.SettleFeeAsync(e.OwnerPublicKey, cost, FeeTypes.Bandwidth,
                        new[] { (nodePublicKey, 1m) }, fileId, $"Egress over quota {over} B ({period})");
                    if (r.Ok) { usage.ChargedD += cost; spent.ChargedD += cost; }
                    else usage.UnpaidD += cost;
                    await _docs.PutAsync(CEgressSpend, spendId, spent);
                }
                usage.ChargedBytes += over;
            }
            await _docs.PutAsync(CEgress, id, usage);
        }

        // ---------------- bonds & slashing (§8) ----------------

        public async Task<decimal> GetBondAsync(string nodePublicKey) =>
            await _wallet.GetBalanceAsync(EconAccounts.Bond(nodePublicKey));

        public async Task<decimal> RequiredBondAsync(string nodePublicKey)
        {
            var node = await FindNodeAsync(nodePublicKey);
            if (node == null) return 0;
            if (node.Value.Kind == "blocker") return BlockerBond;
            var committed = (await _docs.ListAsync<StorageEscrow>(CEscrow,
                    e => (e.Status == "Active" || e.Status == "Grace") && e.StoreNodeIds.Contains(node.Value.Id)))
                .Sum(e => e.SizeBytes);
            var per100Gb = Math.Ceiling(committed / (100m * 1024 * 1024 * 1024));
            return StoreBondBase + StoreBondPer100Gb * per100Gb;
        }

        public async Task<(bool Ok, string Error)> DepositBondAsync(string callerPublicKey, decimal amount)
        {
            var node = await FindNodeAsync(callerPublicKey);
            if (node == null) return (false, "Caller is not a registered Store or Block Creator node");
            if (amount <= 0) return (false, "Amount must be positive");
            return await _wallet.ApplyMovesAsync(new[]
            {
                new LedgerMove(node.Value.PublicKey, EconAccounts.Bond(node.Value.PublicKey), amount, "bond:deposit", "Node bond deposit")
            }, null);
        }

        public async Task<(bool Ok, string Error, BondWithdrawal? Request)> RequestBondWithdrawalAsync(string callerPublicKey, decimal amount)
        {
            var node = await FindNodeAsync(callerPublicKey);
            if (node == null) return (false, "Caller is not a registered node", null);
            var bal = await GetBondAsync(node.Value.PublicKey);
            if (amount <= 0 || amount > bal) return (false, $"Amount must be in (0, {bal}]", null);
            var wd = new BondWithdrawal
            {
                Id = node.Value.PublicKey,
                Amount = amount,
                UnlockAt = DateTime.UtcNow.AddDays(BondWithdrawDelayDays),
                Status = "pending"
            };
            await _docs.PutAsync(CBondWd, wd.Id, wd);
            return (true, "", wd);
        }

        public async Task<int> ProcessBondWithdrawalsAsync(DateTime? nowOverride = null)
        {
            var now = nowOverride ?? DateTime.UtcNow;
            var done = 0;
            foreach (var wd in await _docs.ListAsync<BondWithdrawal>(CBondWd, w => w.Status == "pending" && w.UnlockAt <= now))
            {
                var bal = await GetBondAsync(wd.Id);
                var amt = Math.Min(wd.Amount, bal);
                if (amt > 0)
                    await _wallet.ApplyMovesAsync(new[] { new LedgerMove(EconAccounts.Bond(wd.Id), wd.Id, amt, "bond:withdraw", "Bond withdrawal after delay") }, null);
                wd.Status = "done";
                await _docs.PutAsync(CBondWd, wd.Id, wd);
                done++;
            }
            return done;
        }

        /// <summary>Slash a node bond: 50% to affected accounts (equal split), 50% to the network fund (§8).</summary>
        public async Task<SlashEvent?> SlashAsync(string nodePublicKey, decimal percent, string reason, string? refId,
            IReadOnlyCollection<string>? affectedAccounts = null)
        {
            var bondAcc = EconAccounts.Bond(nodePublicKey);
            var bal = await _wallet.GetBalanceAsync(bondAcc);
            var amount = Math.Round(bal * Math.Clamp(percent, 0, 100) / 100m, 10);
            var ev = new SlashEvent { NodePublicKey = nodePublicKey, Percent = percent, Amount = amount, Reason = reason, RefId = refId };
            if (amount > 0)
            {
                var moves = new List<LedgerMove>();
                var affected = affectedAccounts?.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList() ?? new();
                var toAffected = affected.Count > 0 ? Math.Round(amount / 2m, 10) : 0m;
                if (toAffected > 0)
                {
                    var each = Math.Round(toAffected / affected.Count, 10);
                    foreach (var a in affected) moves.Add(new LedgerMove(bondAcc, a, each, "slash:refund", reason));
                    toAffected = each * affected.Count;
                }
                moves.Add(new LedgerMove(bondAcc, EconAccounts.NetworkFund, amount - toAffected, "slash", reason));
                var r = await _wallet.ApplyMovesAsync(moves.Where(m => m.Amount > 0).ToList(), refId);
                if (!r.Ok) { _log?.LogWarning("Slash failed for {Node}: {Err}", nodePublicKey, r.Error); return null; }
            }
            await _docs.PutAsync(CSlash, ev.Id, ev);
            return ev;
        }

        public async Task<List<SlashEvent>> ListSlashesAsync(int limit = 100) =>
            (await _docs.ListAsync<SlashEvent>(CSlash)).OrderByDescending(s => s.At).Take(limit).ToList();

        /// <summary>Selection weight for blocker scheduling: (bond + 1) × uptime factor (§8).</summary>
        public async Task<BlockCreatorNode?> PickBlockerAsync(List<BlockCreatorNode> nodes)
        {
            if (nodes.Count == 0) return null;
            var now = DateTime.UtcNow;
            var weighted = new List<(BlockCreatorNode Node, decimal W)>();
            foreach (var n in nodes)
            {
                var bond = await GetBondAsync(n.PublicKey);
                if (EnforceBonds && bond < BlockerBond) continue;
                var uptime = (now - n.LastSeen).TotalMinutes <= 10 ? 1m : 0.1m;
                weighted.Add((n, (bond + 1m) * uptime));
            }
            if (weighted.Count == 0) return null;
            var total = weighted.Sum(w => w.W);
            var pick = (decimal)Random.Shared.NextDouble() * total;
            foreach (var w in weighted)
            {
                pick -= w.W;
                if (pick <= 0) return w.Node;
            }
            return weighted[^1].Node;
        }

        public async Task<List<StoreNode>> FilterStoreNodesByBondAsync(List<StoreNode> nodes)
        {
            if (!EnforceBonds) return nodes;
            var result = new List<StoreNode>();
            foreach (var n in nodes)
                if (await GetBondAsync(n.PublicKey) >= await RequiredBondAsync(n.PublicKey)) result.Add(n);
            return result;
        }

        // ---------------- transaction fees & sponsorship (§5, §10) ----------------

        public async Task<(bool Ok, int Status, string Error, decimal Fee, string? Payer)> ChargeTxFeeAsync(string type, string from, string txId)
        {
            if (string.Equals(type, "TopUp", StringComparison.OrdinalIgnoreCase)) return (true, 200, "", 0, null);
            var social = SocialTxTypes.Contains(type);
            if (social && !TryConsumeSocialQuota(from))
                return (false, 429, $"Free social action limit reached ({SocialTxPerHour}/hour)", 0, null);
            if (!_wallet.EnforceFees || TxFee <= 0) return (true, 200, "", 0, null);

            var payer = social ? EconAccounts.NetworkFund : from;
            var r = await _wallet.SettleFeeAsync(payer, TxFee, FeeTypes.Tx,
                new[] { (EconAccounts.BlockerFeePool, 1m) }, txId, $"Tx fee {type}" + (social ? " (sponsored)" : ""), force: true);
            if (r.Ok) return (true, 200, "", TxFee, payer);
            if (social)
            {
                // §10: users never need D to interact. If the sponsor fund cannot cover it, let the action
                // through unpaid instead of blocking it (the fund refills from its fee share).
                _log?.LogWarning("Network fund could not sponsor {Type} tx {TxId}: {Err}; accepted without fee", type, txId, r.Error);
                return (true, 200, "", 0, null);
            }
            return (false, 402, $"Tx fee {TxFee} D not paid: {r.Error}", TxFee, payer);
        }

        private bool TryConsumeSocialQuota(string from)
        {
            if (SocialTxPerHour <= 0) return true;
            var q = SocialRate.GetOrAdd(from, _ => new Queue<DateTime>());
            lock (q)
            {
                var cutoff = DateTime.UtcNow.AddHours(-1);
                while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
                if (q.Count >= SocialTxPerHour) return false;
                q.Enqueue(DateTime.UtcNow);
                return true;
            }
        }

        /// <summary>Pay accumulated tx fees to the blocker granted the next block slot.</summary>
        public async Task<decimal> PayoutBlockerPoolAsync(string blockerPublicKey)
        {
            var bal = await _wallet.GetBalanceAsync(EconAccounts.BlockerFeePool);
            if (bal <= 0) return 0;
            var r = await _wallet.ApplyMovesAsync(new[] { new LedgerMove(EconAccounts.BlockerFeePool, blockerPublicKey, bal, "fee:tx:payout", "Tx fees for block slot") }, null);
            return r.Ok ? bal : 0;
        }

        // ---------------- creator economy (§9) ----------------

        public async Task<SubscriptionPlan?> GetPlanAsync(string creator) => await _docs.GetAsync<SubscriptionPlan>(CPlan, creator.Trim());

        public async Task<SubscriptionPlan> SetPlanAsync(string creator, decimal pricePerMonthD)
        {
            var plan = new SubscriptionPlan { Id = creator.Trim(), PricePerMonthD = Math.Max(0, pricePerMonthD), Active = pricePerMonthD > 0 };
            await _docs.PutAsync(CPlan, plan.Id, plan);
            return plan;
        }

        public static string NextEpoch(string epoch)
        {
            var d = DateTime.ParseExact(epoch + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Epoch(d.AddMonths(1));
        }

        public async Task<(bool Ok, int Status, string Error, Subscription? Sub)> SubscribeAsync(string subscriber, string creator, int months)
        {
            months = Math.Clamp(months, 1, 12);
            if (SameKey(subscriber, creator)) return (false, 400, "Cannot subscribe to yourself", null);
            var plan = await GetPlanAsync(creator);
            if (plan == null || !plan.Active || plan.PricePerMonthD <= 0) return (false, 404, "Creator has no active subscription plan", null);

            var id = $"{creator.Trim()}|{subscriber.Trim()}";
            var sub = await _docs.GetAsync<Subscription>(CSub, id) ?? new Subscription
            {
                Id = id, CreatorPublicKey = creator.Trim(), SubscriberPublicKey = subscriber.Trim()
            };
            var current = Epoch(DateTime.UtcNow);
            var start = string.CompareOrdinal(sub.PaidThroughEpoch, current) >= 0 ? NextEpoch(sub.PaidThroughEpoch) : current;
            var end = start;
            for (var i = 1; i < months; i++) end = NextEpoch(end);

            var cost = plan.PricePerMonthD * months;
            var r = await _wallet.SettleFeeAsync(subscriber.Trim(), cost, FeeTypes.Creator,
                new[] { (creator.Trim(), 1m) }, id.Length > 64 ? null : id, $"Subscription {start}..{end}", force: true);
            if (!r.Ok) return (false, 402, r.Error, null);

            sub.PaidThroughEpoch = end;
            sub.TotalPaidD += cost;
            sub.UpdatedAt = DateTime.UtcNow;
            await _docs.PutAsync(CSub, id, sub);
            return (true, 200, "", sub);
        }

        public async Task<bool> IsSubscribedAsync(string subscriber, string creator, string epoch)
        {
            var subs = await _docs.ListAsync<Subscription>(CSub,
                s => SameKey(s.CreatorPublicKey, creator) && SameKey(s.SubscriberPublicKey, subscriber));
            return subs.Any(s => string.CompareOrdinal(s.PaidThroughEpoch, epoch) >= 0);
        }

        public async Task<List<Subscription>> ListSubscribersAsync(string creator, string? epoch = null)
        {
            epoch ??= Epoch(DateTime.UtcNow);
            return await _docs.ListAsync<Subscription>(CSub,
                s => SameKey(s.CreatorPublicKey, creator) && string.CompareOrdinal(s.PaidThroughEpoch, epoch) >= 0);
        }

        public async Task<List<Subscription>> ListSubscriptionsOfAsync(string subscriber) =>
            await _docs.ListAsync<Subscription>(CSub, s => SameKey(s.SubscriberPublicKey, subscriber));

        /// <summary>
        /// For "subscribers:YYYY-MM" policy tags (and "subscribers-v2:YYYY-MM", PRE v2 with per-tag author keys):
        /// is the requester entitled to the rekey?
        /// </summary>
        public static bool TryParseSubscriberTag(string? tag, out string epoch)
        {
            epoch = "";
            if (tag == null) return false;
            var prefix = tag.StartsWith("subscribers:", StringComparison.Ordinal) ? "subscribers:"
                : tag.StartsWith("subscribers-v2:", StringComparison.Ordinal) ? "subscribers-v2:" : null;
            if (prefix == null) return false;
            epoch = tag[prefix.Length..];
            return DateTime.TryParseExact(epoch + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }

        public async Task<(bool Ok, int Status, string Error)> TipAsync(string from, string to, decimal amount, string? refId, string? ledgerRef = null)
        {
            if (amount <= 0) return (false, 400, "Amount must be positive");
            if (SameKey(from, to)) return (false, 400, "Cannot tip yourself");
            if (!await _storage.IsPublicKeyRegisteredAsync(to)) return (false, 404, "Recipient is not a registered user");
            var rowRef = ledgerRef ?? refId;
            var r = await _wallet.SettleFeeAsync(from.Trim(), amount, FeeTypes.Creator, new[] { (to.Trim(), 1m) },
                rowRef is { Length: > 64 } ? rowRef[..64] : rowRef, "Tip" + (refId != null ? $" for {refId}" : ""), force: true);
            return r.Ok ? (true, 200, "") : (false, 402, r.Error);
        }

        // ---------------- reserve & redemption (§2, §4) ----------------

        public async Task<Payee> SetPayeeAsync(string publicKey, bool verified, string role, string? bankRef)
        {
            var account = await ResolveAccountAsync(publicKey);
            var p = new Payee { Id = account, Verified = verified, Role = role, BankRef = bankRef, UpdatedAt = DateTime.UtcNow };
            await _docs.PutAsync(CPayee, account, p);
            return p;
        }

        public async Task<object> GetReserveAsync()
        {
            var deposits = await _wallet.GetReserveDepositsVndAsync();
            var redemptions = await _docs.ListAsync<RedemptionRequest>(CRedemption);
            var paidOut = redemptions.Where(r => r.Status == "paid").Sum(r => r.PayoutVnd);
            var reserveVnd = deposits - paidOut;
            var issuance = await _wallet.GetBalanceAsync(_wallet.TreasuryKey);
            var circulating = _wallet.HardCap - issuance;
            var required = circulating * _wallet.VndPerD;
            return new
            {
                unit = "D",
                vndPerD = _wallet.VndPerD,
                dPerDlt = _wallet.DPerDlt,
                hardCap = _wallet.HardCap,
                issuanceBalance = issuance,
                circulatingD = circulating,
                reserveVnd,
                reserveDepositsVnd = deposits,
                redeemedVnd = paidOut,
                requiredVnd = required,
                reserveRatio = required == 0 ? 1m : Math.Round(reserveVnd / required, 6),
                pendingRedemptionsD = redemptions.Where(r => r.Status is "pending" or "queued").Sum(r => r.AmountD),
                accounts = new
                {
                    networkFund = await _wallet.GetBalanceAsync(EconAccounts.NetworkFund),
                    protocolOps = await _wallet.GetBalanceAsync(EconAccounts.ProtocolOps),
                    redemptionHold = await _wallet.GetBalanceAsync(EconAccounts.RedemptionHold),
                    blockerFeePool = await _wallet.GetBalanceAsync(EconAccounts.BlockerFeePool)
                },
                asOf = DateTime.UtcNow
            };
        }

        private async Task<decimal> ReserveRatioAsync()
        {
            var deposits = await _wallet.GetReserveDepositsVndAsync();
            var paidOut = (await _docs.ListAsync<RedemptionRequest>(CRedemption, r => r.Status == "paid")).Sum(r => r.PayoutVnd);
            var circulating = _wallet.HardCap - await _wallet.GetBalanceAsync(_wallet.TreasuryKey);
            var required = circulating * _wallet.VndPerD;
            return required == 0 ? 1m : (deposits - paidOut) / required;
        }

        public async Task<(bool Ok, int Status, string Error, RedemptionRequest? Request)> RequestRedemptionAsync(string callerPublicKey, decimal amountD)
        {
            var account = await ResolveAccountAsync(callerPublicKey);
            var payee = await _docs.GetAsync<Payee>(CPayee, account);
            if (payee == null || !payee.Verified)
                return (false, 403, "Redemption is only for KYC-verified node operators and creators", null);
            if (amountD < RedeemMinD) return (false, 400, $"Minimum redemption is {RedeemMinD} D", null);

            var fee = Math.Round(amountD * RedeemFeePercent / 100m, 10);
            var req = new RedemptionRequest
            {
                PublicKey = account,
                AmountD = amountD,
                FeeD = fee,
                PayoutVnd = Math.Round((amountD - fee) * _wallet.VndPerD, 2),
                BankRef = payee.BankRef
            };
            var r = await _wallet.ApplyMovesAsync(new[]
            {
                new LedgerMove(account, EconAccounts.RedemptionHold, amountD, "redeem:hold", $"Redemption {req.Id}")
            }, req.Id);
            if (!r.Ok) return (false, 402, r.Error, null);
            req.Status = await ReserveRatioAsync() >= 1m ? "pending" : "queued";
            await _docs.PutAsync(CRedemption, req.Id, req);
            return (true, 200, "", req);
        }

        public static string RedemptionSignPayload(RedemptionRequest r) =>
            $"REDEEM|{r.Id}|{r.PublicKey}|{r.AmountD.ToString("F10", CultureInfo.InvariantCulture)}|{r.PayoutVnd.ToString("F2", CultureInfo.InvariantCulture)}";

        public async Task<RedemptionRequest?> GetRedemptionAsync(string id) => await _docs.GetAsync<RedemptionRequest>(CRedemption, id);

        public async Task<List<RedemptionRequest>> ListRedemptionsAsync(string? status = null, string? publicKey = null) =>
            (await _docs.ListAsync<RedemptionRequest>(CRedemption,
                r => (status == null || status == "all" || r.Status == status) && (publicKey == null || SameKey(r.PublicKey, publicKey))))
            .OrderByDescending(r => r.CreatedAt).ToList();

        /// <summary>Ops paid the VND out of the reserve: D (minus fee) returns to issuance, fee to protocol ops.</summary>
        public async Task<(bool Ok, string Error, RedemptionRequest? Request)> MarkRedemptionPaidAsync(string id, string? bankRef)
        {
            var req = await GetRedemptionAsync(id);
            if (req == null) return (false, "Not found", null);
            if (req.Status == "paid") return (true, "", req);
            if (req.Status is not ("pending" or "queued")) return (false, $"Status is {req.Status}", req);
            var moves = new List<LedgerMove>
            {
                new(EconAccounts.RedemptionHold, _wallet.TreasuryKey, req.AmountD - req.FeeD, "redeem:paid", $"Redeemed {req.PayoutVnd} VND"),
            };
            if (req.FeeD > 0) moves.Add(new(EconAccounts.RedemptionHold, EconAccounts.ProtocolOps, req.FeeD, "redeem:fee", "Redemption fee"));
            var r = await _wallet.ApplyMovesAsync(moves, req.Id);
            if (!r.Ok) return (false, r.Error, req);
            req.Status = "paid";
            req.BankRef = bankRef ?? req.BankRef;
            req.CompletedAt = DateTime.UtcNow;
            await _docs.PutAsync(CRedemption, req.Id, req);
            return (true, "", req);
        }

        public async Task<(bool Ok, string Error, RedemptionRequest? Request)> RejectRedemptionAsync(string id, string? note)
        {
            var req = await GetRedemptionAsync(id);
            if (req == null) return (false, "Not found", null);
            if (req.Status is not ("pending" or "queued")) return (false, $"Status is {req.Status}", req);
            var r = await _wallet.ApplyMovesAsync(new[] { new LedgerMove(EconAccounts.RedemptionHold, req.PublicKey, req.AmountD, "redeem:reject", note ?? "Rejected") }, req.Id);
            if (!r.Ok) return (false, r.Error, req);
            req.Status = "rejected";
            req.Note = note;
            req.CompletedAt = DateTime.UtcNow;
            await _docs.PutAsync(CRedemption, req.Id, req);
            return (true, "", req);
        }

        // ---------------- ledger anchors (§11 phase A) ----------------

        public static string Sha256Hex(string s) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

        /// <summary>Same pairwise construction as the blocker's transaction Merkle root.</summary>
        public static string MerkleRoot(IEnumerable<string> leafHashes)
        {
            var level = leafHashes.ToList();
            if (level.Count == 0) return Sha256Hex("");
            while (level.Count > 1)
            {
                var next = new List<string>();
                for (var i = 0; i < level.Count; i += 2)
                    next.Add(i + 1 < level.Count ? Sha256Hex(level[i] + level[i + 1]) : level[i]);
                level = next;
            }
            return level[0];
        }

        public static string LedgerLeaf(WalletLedgerEntry e) => Sha256Hex(string.Join("|",
            e.Id, e.FromPublicKey ?? "", e.ToPublicKey ?? "",
            e.Amount.ToString("F10", CultureInfo.InvariantCulture), e.Kind, e.RefId ?? "",
            DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture)));

        public static string BalanceLeafOf(string account, decimal balance) =>
            Sha256Hex($"{account}|{balance.ToString("F10", CultureInfo.InvariantCulture)}");

        public static string BalanceLeaf(WalletAccount a) => BalanceLeafOf(a.PublicKey, a.Balance);

        /// <summary>Replay ledger rows onto a balance map; zero balances are dropped.</summary>
        public static void ApplyEntries(Dictionary<string, decimal> balances, IEnumerable<WalletLedgerEntry> entries)
        {
            foreach (var e in entries)
            {
                if (!string.IsNullOrEmpty(e.FromPublicKey))
                    balances[e.FromPublicKey] = (balances.TryGetValue(e.FromPublicKey, out var f) ? f : 0m) - e.Amount;
                if (!string.IsNullOrEmpty(e.ToPublicKey))
                    balances[e.ToPublicKey] = (balances.TryGetValue(e.ToPublicKey, out var t) ? t : 0m) + e.Amount;
            }
            foreach (var k in balances.Where(kv => kv.Value == 0m).Select(kv => kv.Key).ToList()) balances.Remove(k);
        }

        /// <summary>Ordered (ordinal by account) balance leaves: the BalancesRoot input.</summary>
        public static List<(string Account, decimal Balance, string Leaf)> BalanceLeaves(Dictionary<string, decimal> balances) =>
            balances.Where(kv => kv.Value != 0m).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value, BalanceLeafOf(kv.Key, kv.Value))).ToList();

        /// <summary>Sibling path for leaf <paramref name="index"/> under <see cref="MerkleRoot"/> (odd nodes are carried up).</summary>
        public static List<MerkleStep> MerklePath(IReadOnlyList<string> leaves, int index)
        {
            var path = new List<MerkleStep>();
            var level = leaves.ToList();
            var idx = index;
            while (level.Count > 1)
            {
                var sib = idx ^ 1;
                if (sib < level.Count) path.Add(new MerkleStep { Hash = level[sib], Side = sib < idx ? "L" : "R" });
                var next = new List<string>();
                for (var i = 0; i < level.Count; i += 2)
                    next.Add(i + 1 < level.Count ? Sha256Hex(level[i] + level[i + 1]) : level[i]);
                level = next;
                idx /= 2;
            }
            return path;
        }

        public static string RootFromPath(string leaf, IEnumerable<MerkleStep> path) =>
            path.Aggregate(leaf, (cur, step) => step.Side == "L" ? Sha256Hex(step.Hash + cur) : Sha256Hex(cur + step.Hash));

        private async Task<Dictionary<string, decimal>> LoadSnapshotAsync(LedgerAnchor? anchor)
        {
            if (anchor == null) return new Dictionary<string, decimal>();
            var snap = await _docs.GetAsync<BalanceSnapshot>(CAnchorBal, anchor.Id);
            if (snap != null) return new Dictionary<string, decimal>(snap.Balances);
            // Anchors created before snapshots existed: rebuild by replaying the whole ledger.
            var balances = new Dictionary<string, decimal>();
            ApplyEntries(balances, await _wallet.GetLedgerRangeAsync(DateTime.MinValue, anchor.UpToInclusive));
            await _docs.PutAsync(CAnchorBal, anchor.Id, new BalanceSnapshot { Id = anchor.Id, Seq = anchor.Seq, Balances = balances });
            return balances;
        }

        /// <summary>
        /// Anchor the ledger: LedgerRoot over new rows, BalancesRoot over balances replayed from the ledger
        /// (previous snapshot + new rows). Replaying keeps the root deterministic, so Inspectors can recompute it;
        /// live account balances that disagree with the replay are counted in <see cref="LedgerAnchor.LiveMismatchAccounts"/>.
        /// </summary>
        public async Task<LedgerAnchor> CreateAnchorAsync(string? blockerPublicKey)
        {
            var latest = await _docs.GetAsync<LedgerAnchor>(CMeta, "latest-anchor");
            var from = latest?.UpToInclusive ?? DateTime.MinValue;
            var upTo = DateTime.UtcNow;
            var entries = await _wallet.GetLedgerRangeAsync(from, upTo);
            if (latest != null && entries.Count == 0) return latest;

            var balances = await LoadSnapshotAsync(latest);
            ApplyEntries(balances, entries);
            var leaves = BalanceLeaves(balances);

            var live = (await _wallet.GetAllBalancesAsync()).Where(a => a.Balance != 0m)
                .ToDictionary(a => a.PublicKey, a => a.Balance);
            var mismatch = live.Count(kv => !balances.TryGetValue(kv.Key, out var b) || b != kv.Value)
                           + balances.Keys.Count(k => !live.ContainsKey(k));
            if (mismatch > 0) _log?.LogWarning("Ledger anchor: {Count} live balances differ from ledger replay", mismatch);

            var seq = (latest?.Seq ?? 0) + 1;
            var anchor = new LedgerAnchor
            {
                Id = seq.ToString("D12"),
                Seq = seq,
                FromExclusive = from,
                UpToInclusive = entries.Count > 0 ? entries[^1].CreatedAt : upTo,
                EntryCount = entries.Count,
                LedgerRoot = MerkleRoot(entries.Select(LedgerLeaf)),
                BalancesRoot = MerkleRoot(leaves.Select(l => l.Leaf)),
                BlockerPublicKey = blockerPublicKey,
                LiveMismatchAccounts = mismatch
            };
            await _docs.PutAsync(CAnchorBal, anchor.Id, new BalanceSnapshot { Id = anchor.Id, Seq = seq, Balances = balances });
            await _docs.PutAsync(CAnchor, anchor.Id, anchor);
            await _docs.PutAsync(CMeta, "latest-anchor", anchor);
            return anchor;
        }

        /// <summary>Merkle proof of <paramref name="account"/>'s balance in anchor <paramref name="seq"/> (§11 phase B).</summary>
        public async Task<BalanceProof?> GetBalanceProofAsync(long seq, string account)
        {
            var anchor = await _docs.GetAsync<LedgerAnchor>(CAnchor, seq.ToString("D12"));
            if (anchor == null) return null;
            var leaves = BalanceLeaves(await LoadSnapshotAsync(anchor));
            var key = account.Trim();
            var idx = leaves.FindIndex(l => l.Account == key);
            if (idx < 0) idx = leaves.FindIndex(l => SameKey(l.Account, key));
            var proof = new BalanceProof { Seq = seq, Account = key, Root = anchor.BalancesRoot, UpToInclusive = anchor.UpToInclusive };
            if (idx < 0) return proof;
            proof.Included = true;
            proof.Index = idx;
            proof.Account = leaves[idx].Account;
            proof.Balance = leaves[idx].Balance;
            proof.Leaf = leaves[idx].Leaf;
            proof.Path = MerklePath(leaves.Select(l => l.Leaf).ToList(), idx);
            return proof;
        }

        private const string LedgerRowsCursorId = "ledger-rows-cursor";

        /// <summary>Last anchor window whose rows a Blocker accepted (LedgerRows txs); 0 = none yet.</summary>
        public async Task<long> GetLedgerRowsCursorAsync() =>
            (await _docs.GetAsync<OpsMarker>(COps, LedgerRowsCursorId)) is { } m && long.TryParse(m.Summary, out var s) ? s : 0;

        public Task SetLedgerRowsCursorAsync(long seq) =>
            _docs.PutAsync(COps, LedgerRowsCursorId, new OpsMarker { Id = LedgerRowsCursorId, DoneAt = DateTime.UtcNow, Summary = seq.ToString() });

        public async Task<List<LedgerAnchor>> ListAnchorsAsync(int limit = 50) =>
            (await _docs.ListAsync<LedgerAnchor>(CAnchor)).OrderByDescending(a => a.Seq).Take(Math.Clamp(limit, 1, 500)).ToList();

        public async Task<(LedgerAnchor? Anchor, List<WalletLedgerEntry> Entries)> GetAnchorEntriesAsync(long seq)
        {
            var a = await _docs.GetAsync<LedgerAnchor>(CAnchor, seq.ToString("D12"));
            if (a == null) return (null, new());
            return (a, await _wallet.GetLedgerRangeAsync(a.FromExclusive, a.UpToInclusive));
        }

        // ---------------- usage summary for the App ----------------

        public async Task<object> GetUsageAsync(string owner)
        {
            var escrows = await _docs.ListAsync<StorageEscrow>(CEscrow, e => SameKey(e.OwnerPublicKey, owner));
            var active = escrows.Where(e => e.Status is "Active" or "Grace").ToList();
            var period = Epoch(DateTime.UtcNow);
            long egress = 0;
            decimal egressCharged = 0, egressUnpaid = 0;
            foreach (var e in active)
            {
                var u = await _docs.GetAsync<EgressUsage>(CEgress, $"{period}|{e.Id}");
                if (u == null) continue;
                egress += u.Bytes; egressCharged += u.ChargedD; egressUnpaid += u.UnpaidD;
            }
            var cap = await _docs.GetAsync<EgressCap>(CEgressCap, owner);
            return new
            {
                balance = await _wallet.GetBalanceAsync(owner),
                freeTier = new
                {
                    eligible = await IsFreeTierEligibleAsync(owner),
                    emailVerified = await IsEmailVerifiedAsync(owner),
                    requiresVerifiedEmail = FreeTierRequireVerifiedEmail,
                    storageQuotaBytes = FreeTierStorageBytes,
                    storageUsedBytes = active.Where(e => e.FreeTier).Sum(e => e.SizeBytes * Math.Max(1, e.StoreNodeIds.Count)),
                    egressQuotaBytesPerMonth = FreeTierEgressBytes,
                    socialTxPerHour = SocialTxPerHour
                },
                storage = new
                {
                    activeContracts = active.Count,
                    paidBytes = active.Where(e => !e.FreeTier).Sum(e => e.SizeBytes * Math.Max(1, e.StoreNodeIds.Count)),
                    inGrace = active.Count(e => e.Status == "Grace"),
                    pricePerGbMonth = PricePerGbMonth,
                    minContractFee = MinContractFee
                },
                egress = new
                {
                    period,
                    bytes = egress,
                    chargedD = egressCharged,
                    unpaidD = egressUnpaid,
                    pricePerGb = EgressPricePerGb,
                    quotaMultiplier = EgressQuotaMultiplier,
                    capDPerMonth = cap?.MaxDPerMonth
                },
                subscriptions = await ListSubscriptionsOfAsync(owner),
                plan = await GetPlanAsync(owner)
            };
        }
    }
}

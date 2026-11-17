using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace deltanium_api.Tests.Services
{
    /// <summary>TOKENOMICS_V2 economics on the JSON ledger (no MySQL, no network).</summary>
    public class EconomicsServiceTests : IDisposable
    {
        private const string Alice = "alice-pk";
        private const string Bob = "bob-pk";
        private const string StoreA = "store-a-pk";
        private const string StoreB = "store-b-pk";
        private const string Blocker = "blocker-pk";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "deltanium-econ-" + Guid.NewGuid().ToString("N"));
        private readonly Mock<IStorageService> _storage = new();
        private WalletService _wallet = null!;
        private EconomicsService _econ = null!;

        public EconomicsServiceTests()
        {
            _storage.Setup(s => s.GetUsersAsync()).ReturnsAsync(new List<User>
            {
                new() { PublicKey = Alice, Email = "a@example.test" },
                new() { PublicKey = Bob, Email = null },
            });
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(It.IsAny<string>()))
                .ReturnsAsync((string pk) => pk is Alice or Bob);
            var stores = new List<StoreNode>
            {
                new() { Id = "sa", PublicKey = StoreA, Endpoint = "http://store-a" },
                new() { Id = "sb", PublicKey = StoreB, Endpoint = "http://store-b" },
            };
            _storage.Setup(s => s.GetNodesAsync()).ReturnsAsync(stores);
            _storage.Setup(s => s.GetActiveNodesAsync()).ReturnsAsync(stores);
            _storage.Setup(s => s.GetBlockCreatorNodesAsync()).ReturnsAsync(new List<BlockCreatorNode>
            {
                new() { Id = "b1", PublicKey = Blocker, Endpoint = "http://blocker", LastSeen = DateTime.UtcNow }
            });
            Build(enforceFees: true);
        }

        private void Build(bool enforceFees, Dictionary<string, string?>? extra = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Economics:DataDir"] = _dir,
                ["Economics:EnforceFees"] = enforceFees ? "true" : "false",
                ["Economics:HardCap"] = "10000000000000",
            };
            if (extra != null) foreach (var kv in extra) settings[kv.Key] = kv.Value;
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            _wallet = new WalletService(config);
            _econ = new EconomicsService(_wallet, new EconDocStore(config), _storage.Object, config);
            _wallet.EnsureReadyAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }

        private async Task Fund(string account, decimal amount) =>
            (await _wallet.ApplyMovesAsync(new[] { new LedgerMove(_wallet.TreasuryKey, account, amount, "test") }, null)).Ok.Should().BeTrue();

        // ---------- §2 genesis & §3 top-up ----------

        [Fact]
        public async Task Genesis_credits_whole_hard_cap_to_issuance()
        {
            (await _wallet.GetBalanceAsync(_wallet.TreasuryKey)).Should().Be(10_000_000_000_000m);
            var eco = await _wallet.GetEconomicsAsync();
            eco.CirculatingD.Should().Be(0);
            eco.DPerDlt.Should().Be(1_000_000m);
        }

        [Theory]
        [InlineData("transfer", 97_000)]
        [InlineData("card", 95_000)]
        public async Task TopUp_fee_depends_on_channel_and_backs_reserve_1_to_1(string channel, decimal expectedD)
        {
            var (ok, _, order) = await _wallet.CreateTopUpOrderAsync(Alice, 100_000m, channel);
            ok.Should().BeTrue();
            order!.DCredited.Should().Be(expectedD);
            order.NetworkReserveFiatVnd.Should().Be(expectedD);
            order.PlatformFiatVnd.Should().Be(100_000m - expectedD);

            (await _wallet.CompleteTopUpOrderAsync(order.Id)).Ok.Should().BeTrue();
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(expectedD);
            (await _wallet.GetReserveDepositsVndAsync()).Should().Be(expectedD);
        }

        [Fact]
        public async Task TopUp_below_minimum_is_rejected()
        {
            var (ok, err, _) = await _wallet.CreateTopUpOrderAsync(Alice, 5_000m, "transfer");
            ok.Should().BeFalse();
            err.Should().Contain("10000");
        }

        // ---------- §5 fee split ----------

        [Fact]
        public async Task SettleFee_splits_storage_80_10_10_atomically()
        {
            await Fund(Alice, 1_000m);
            (await _wallet.SettleFeeAsync(Alice, 1_000m, FeeTypes.Storage, new[] { (StoreA, 1m), (StoreB, 1m) }, "ref", "test")).Ok.Should().BeTrue();
            (await _wallet.GetBalanceAsync(StoreA)).Should().Be(400m);
            (await _wallet.GetBalanceAsync(StoreB)).Should().Be(400m);
            (await _wallet.GetBalanceAsync(EconAccounts.NetworkFund)).Should().Be(100m);
            (await _wallet.GetBalanceAsync(EconAccounts.ProtocolOps)).Should().Be(100m);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(0m);
        }

        [Fact]
        public async Task SettleFee_with_insufficient_balance_books_nothing()
        {
            await Fund(Alice, 10m);
            (await _wallet.SettleFeeAsync(Alice, 100m, FeeTypes.Storage, new[] { (StoreA, 1m) }, null, null)).Ok.Should().BeFalse();
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(10m);
            (await _wallet.GetBalanceAsync(StoreA)).Should().Be(0m);
        }

        // ---------- §6 storage escrow & proofs ----------

        private static List<FileBlockHash> Blocks() => new() { new() { BlockId = "b0", Sha256 = new string('a', 64) } };

        [Fact]
        public async Task Quote_applies_price_replicas_and_minimum()
        {
            var big = await _econ.QuoteAsync(null, 1024L * 1024 * 1024, 2, 12); // 1 GB × 2 × 12 × 800
            big.TotalFee.Should().Be(19_200m);
            var tiny = await _econ.QuoteAsync(null, 1000, 2, 1);
            tiny.TotalFee.Should().Be(10m); // MinContractFee
        }

        [Fact]
        public async Task Escrow_is_paid_by_owner_and_returns_402_when_short()
        {
            var size = 300L * 1024 * 1024; // above free tier
            var r = await _econ.OpenStorageEscrowAsync(Alice, "f1", size, new[] { "sa", "sb" }, 12, null, Blocks());
            r.Ok.Should().BeFalse();
            r.Status.Should().Be(402);

            await Fund(Alice, 100_000m);
            r = await _econ.OpenStorageEscrowAsync(Alice, "f1", size, new[] { "sa", "sb" }, 12, null, Blocks());
            r.Ok.Should().BeTrue();
            r.Escrow!.FreeTier.Should().BeFalse();
            (await _wallet.GetBalanceAsync(EconAccounts.Escrow("f1"))).Should().Be(r.Escrow.TotalFunded);
        }

        [Fact]
        public async Task Free_tier_is_paid_by_network_fund_for_users_with_email()
        {
            await Fund(EconAccounts.NetworkFund, 1_000m);
            var r = await _econ.OpenStorageEscrowAsync(Alice, "free1", 1024 * 1024, new[] { "sa", "sb" }, 12, null, Blocks());
            r.Ok.Should().BeTrue();
            r.Escrow!.FreeTier.Should().BeTrue();
            r.Escrow.PayerAccount.Should().Be(EconAccounts.NetworkFund);
            (await _econ.GetFreeTierUsedBytesAsync(Alice)).Should().Be(2 * 1024 * 1024);

            (await _econ.IsFreeTierEligibleAsync(Bob)).Should().BeFalse(); // no email
        }

        [Fact]
        public async Task Free_tier_pays_for_actual_bytes_without_the_minimum_fee()
        {
            await Fund(EconAccounts.NetworkFund, 1_000m);
            var r = await _econ.OpenStorageEscrowAsync(Alice, "like1", 600, new[] { "sa", "sb" }, 12, null, null, social: true);
            r.Ok.Should().BeTrue();
            r.Escrow!.FreeTier.Should().BeTrue();
            r.Escrow.TotalFunded.Should().BeLessThan(0.02m); // 1,200 B × 800 D/GB × 12 mo, not 10 D
            (await _econ.GetFreeTierUsedBytesAsync(Alice)).Should().Be(1200);
        }

        [Fact]
        public async Task Social_items_are_never_charged_to_the_owner()
        {
            // Bob has no email, so no free tier: a reaction is recorded unfunded instead of costing 10 D.
            var r = await _econ.OpenStorageEscrowAsync(Bob, "like2", 600, new[] { "sa" }, 12, null, null, social: true);
            r.Ok.Should().BeTrue();
            r.Escrow!.Social.Should().BeTrue();
            r.Escrow.TotalFunded.Should().Be(0);

            // A large item cannot use the social exemption.
            var big = await _econ.OpenStorageEscrowAsync(Bob, "big", EconomicsService.SocialItemMaxBytes + 1, new[] { "sa" }, 12, null, null, social: true);
            big.Ok.Should().BeFalse();
            big.Status.Should().Be(402);
        }

        [Fact]
        public async Task Social_exemption_stops_at_the_monthly_budget()
        {
            Build(enforceFees: true, new() { ["Economics:FreeTier:SocialMonthlyBytes"] = "1000" });
            var first = await _econ.OpenStorageEscrowAsync(Bob, "s1", 600, new[] { "sa" }, 12, null, null, social: true);
            first.Ok.Should().BeTrue();
            first.Escrow!.Social.Should().BeTrue();

            // 600 + 600 > 1000: Bob (no email, no D) now has to pay like any other upload.
            var second = await _econ.OpenStorageEscrowAsync(Bob, "s2", 600, new[] { "sa" }, 12, null, null, social: true);
            second.Ok.Should().BeFalse();
            second.Status.Should().Be(402);
        }

        [Fact]
        public async Task Store_that_never_passed_a_challenge_is_dropped_without_slashing()
        {
            await Fund(Alice, 1_000_000m);
            await Fund(StoreA, 100_000m);
            await Fund(StoreB, 100_000m);
            (await _econ.DepositBondAsync(StoreA, 100_000m)).Ok.Should().BeTrue();
            (await _econ.DepositBondAsync(StoreB, 100_000m)).Ok.Should().BeTrue();

            // The uploader registered hashes that match nothing on sb: it fails every challenge from day one.
            var open = await _econ.OpenStorageEscrowAsync(Alice, "junk", 1024L * 1024 * 1024, new[] { "sa", "sb" }, 1, null, Blocks());
            var e = open.Escrow!;
            for (var i = 0; i < 10; i++)
            {
                await _econ.RecordProofAsync(e, "sa", i != 0, "miss", e.StartAt.AddDays(1)); // passed before, then 10% misses
                await _econ.RecordProofAsync(e, "sb", false, "hash mismatch", e.StartAt.AddDays(1));
            }
            await _econ.SettleDueAsync(e.StartAt.AddMonths(1).AddMinutes(1));

            (await _econ.GetBondAsync(StoreB)).Should().Be(100_000m); // never verified: not slashed
            (await _econ.GetBondAsync(StoreA)).Should().Be(100_000m); // verified and 90% pass: paid pro rata, not slashed
            (await _wallet.GetBalanceAsync(StoreB)).Should().Be(0m);
            (await _econ.GetEscrowAsync("junk"))!.FailedNodeIds.Should().Contain("sb");
        }

        [Fact]
        public async Task Free_tier_is_never_charged_to_the_owner_when_the_network_fund_is_empty()
        {
            await Fund(Alice, 1_000m); // Alice could pay, but the free tier must not cost her anything
            var r = await _econ.OpenStorageEscrowAsync(Alice, "free-empty", 1024 * 1024, new[] { "sa" }, 12, null, Blocks());
            r.Ok.Should().BeTrue();
            r.Escrow!.FreeTier.Should().BeTrue();
            r.Escrow.TotalFunded.Should().Be(0);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(1_000m);
            (await _econ.GetFreeTierUsedBytesAsync(Alice)).Should().Be(1024 * 1024);
        }

        [Fact]
        public async Task Social_tx_goes_through_when_the_sponsor_fund_is_empty()
        {
            var r = await _econ.ChargeTxFeeAsync("React", Bob, "tx-empty-fund");
            r.Ok.Should().BeTrue();
            r.Fee.Should().Be(0);

            var paid = await _econ.ChargeTxFeeAsync("Transfer", Bob, "tx-transfer"); // not social: Bob has no D
            paid.Ok.Should().BeFalse();
            paid.Status.Should().Be(402);
        }

        [Fact]
        public async Task Escrow_is_unfunded_while_fees_are_not_enforced()
        {
            Build(enforceFees: false);
            var r = await _econ.OpenStorageEscrowAsync(Alice, "f-off", 300L * 1024 * 1024, new[] { "sa" }, 12, null, Blocks());
            r.Ok.Should().BeTrue();
            r.Escrow!.TotalFunded.Should().Be(0);
        }

        [Fact]
        public async Task Monthly_settlement_pays_passing_nodes_and_slashes_failing_ones()
        {
            await Fund(Alice, 1_000_000m);
            await Fund(StoreB, 100_000m);
            (await _econ.DepositBondAsync(StoreB, 100_000m)).Ok.Should().BeTrue();

            var size = 1024L * 1024 * 1024; // 1 GB → 1,600 D / month for 2 replicas
            var open = await _econ.OpenStorageEscrowAsync(Alice, "f2", size, new[] { "sa", "sb" }, 1, null, Blocks());
            open.Ok.Should().BeTrue();
            var e = open.Escrow!;
            for (var i = 0; i < 10; i++)
            {
                await _econ.RecordProofAsync(e, "sa", true, null, e.StartAt.AddDays(1));
                await _econ.RecordProofAsync(e, "sb", i < 5, "miss", e.StartAt.AddDays(1)); // 50% → fail
            }

            (await _econ.SettleDueAsync(e.StartAt.AddMonths(1).AddMinutes(1))).Should().Be(1);

            (await _wallet.GetBalanceAsync(StoreA)).Should().Be(640m); // 800 × 80%
            (await _wallet.GetBalanceAsync(StoreB)).Should().Be(0m);
            (await _econ.GetBondAsync(StoreB)).Should().Be(90_000m); // 10% slashed
            var after = await _econ.GetEscrowAsync("f2");
            after!.FailedNodeIds.Should().Contain("sb");
            after.Status.Should().Be("Closed");
            // Unreleased share of the failed node was refunded to the owner on close.
            (await _wallet.GetBalanceAsync(EconAccounts.Escrow("f2"))).Should().Be(0m);
        }

        [Fact]
        public async Task AutoRenew_charges_owner_then_enters_grace_and_expires()
        {
            await Fund(Alice, 2_000m);
            var size = 1024L * 1024 * 1024; // 800 D / month, single replica
            var open = await _econ.OpenStorageEscrowAsync(Alice, "f3", size, new[] { "sa" }, 1, "AutoRenew", Blocks());
            open.Ok.Should().BeTrue();
            var start = open.Escrow!.StartAt;

            await _econ.SettleDueAsync(start.AddMonths(1).AddMinutes(1));
            var e = await _econ.GetEscrowAsync("f3");
            e!.Months.Should().Be(2);
            e.Status.Should().Be("Active");
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(400m);

            await _econ.SettleDueAsync(start.AddMonths(2).AddMinutes(1));
            e = await _econ.GetEscrowAsync("f3");
            e!.Status.Should().Be("Grace");

            await _econ.SettleDueAsync(start.AddMonths(2).AddDays(31));
            (await _econ.GetEscrowAsync("f3"))!.Status.Should().Be("Expired");
        }

        // ---------- §7 bandwidth ----------

        [Fact]
        public async Task Egress_over_quota_is_charged_to_owner_and_paid_to_serving_store()
        {
            await Fund(Alice, 1_000_000m);
            var size = 100L * 1024 * 1024; // 100 MB → quota 10 × size / month
            (await _econ.OpenStorageEscrowAsync(Alice, "f4", size, new[] { "sa" }, 12, null, Blocks())).Ok.Should().BeTrue();
            var before = await _wallet.GetBalanceAsync(Alice);

            await _econ.RecordEgressAsync("sa", StoreA, "f4", 10 * size + 1024L * 1024 * 1024, DateTime.UtcNow); // exactly 1 GiB over
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(before - 300m);
            (await _wallet.GetBalanceAsync(StoreA)).Should().Be(255m); // 85%
        }

        [Fact]
        public async Task Egress_cap_stops_charging_and_flags_file()
        {
            await Fund(Alice, 1_000_000m);
            (await _econ.OpenStorageEscrowAsync(Alice, "f5", 100L * 1024 * 1024, new[] { "sa" }, 12, null, Blocks())).Ok.Should().BeTrue();
            await _econ.SetEgressCapAsync(Alice, 100m);
            var before = await _wallet.GetBalanceAsync(Alice);

            await _econ.RecordEgressAsync("sa", StoreA, "f5", 2L * 1024 * 1024 * 1024, DateTime.UtcNow);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(before);
            (await _econ.GetEscrowAsync("f5"))!.EgressCapped.Should().BeTrue();
        }

        // ---------- §8 bonds ----------

        [Fact]
        public async Task Bond_deposit_requires_registered_node_and_withdrawal_waits()
        {
            await Fund(Alice, 1_000m);
            (await _econ.DepositBondAsync(Alice, 100m)).Ok.Should().BeFalse();

            await Fund(StoreA, 200_000m);
            (await _econ.DepositBondAsync(StoreA, 150_000m)).Ok.Should().BeTrue();
            (await _econ.RequiredBondAsync(StoreA)).Should().Be(100_000m);

            var (ok, _, wd) = await _econ.RequestBondWithdrawalAsync(StoreA, 50_000m);
            ok.Should().BeTrue();
            (await _econ.ProcessBondWithdrawalsAsync(DateTime.UtcNow)).Should().Be(0);
            (await _econ.ProcessBondWithdrawalsAsync(wd!.UnlockAt.AddMinutes(1))).Should().Be(1);
            (await _econ.GetBondAsync(StoreA)).Should().Be(100_000m);
        }

        [Fact]
        public async Task Enforced_bonds_exclude_unbonded_blockers()
        {
            Build(enforceFees: true, new() { ["Economics:Bond:Enforce"] = "true" });
            var nodes = new List<BlockCreatorNode> { new() { PublicKey = Blocker, LastSeen = DateTime.UtcNow } };
            (await _econ.PickBlockerAsync(nodes)).Should().BeNull();
            await Fund(Blocker, 500_000m);
            (await _econ.DepositBondAsync(Blocker, 500_000m)).Ok.Should().BeTrue();
            (await _econ.PickBlockerAsync(nodes))!.PublicKey.Should().Be(Blocker);
        }

        // ---------- §9 creator economy ----------

        [Fact]
        public async Task Subscription_pays_creator_90_percent_and_gates_epoch()
        {
            await Fund(Bob, 10_000m);
            await _econ.SetPlanAsync(Alice, 1_000m);
            var (ok, _, _, sub) = await _econ.SubscribeAsync(Bob, Alice, 2);
            ok.Should().BeTrue();
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(1_800m);
            (await _wallet.GetBalanceAsync(Bob)).Should().Be(8_000m);

            var now = EconomicsService.Epoch(DateTime.UtcNow);
            (await _econ.IsSubscribedAsync(Bob, Alice, now)).Should().BeTrue();
            (await _econ.IsSubscribedAsync(Bob, Alice, EconomicsService.NextEpoch(now))).Should().BeTrue();
            (await _econ.IsSubscribedAsync(Bob, Alice, sub!.PaidThroughEpoch == now ? EconomicsService.NextEpoch(now) : EconomicsService.NextEpoch(EconomicsService.NextEpoch(now)))).Should().BeFalse();
            (await _econ.ListSubscribersAsync(Alice)).Should().ContainSingle(s => s.SubscriberPublicKey == Bob);
        }

        [Theory]
        [InlineData("subscribers:2026-11", true)]
        [InlineData("subscribers:2026-13", false)]
        [InlineData("followers:2026", false)]
        public void Subscriber_tag_parsing(string tag, bool expected) =>
            EconomicsService.TryParseSubscriberTag(tag, out _).Should().Be(expected);

        [Fact]
        public async Task Tip_requires_registered_recipient()
        {
            await Fund(Bob, 1_000m);
            (await _econ.TipAsync(Bob, "nobody", 100m, null)).Status.Should().Be(404);
            (await _econ.TipAsync(Bob, Alice, 100m, "post-1")).Ok.Should().BeTrue();
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(90m);
        }

        // ---------- §4 redemption & reserve ----------

        [Fact]
        public async Task Redemption_requires_kyc_and_returns_D_to_issuance()
        {
            var (_, _, order) = await _wallet.CreateTopUpOrderAsync(Alice, 1_000_000m, "transfer");
            await _wallet.CompleteTopUpOrderAsync(order!.Id); // 970,000 D, reserve 970,000 VND
            await _wallet.TransferAsync(Alice, StoreA, 200_000m);

            (await _econ.RequestRedemptionAsync(StoreA, 200_000m)).Status.Should().Be(403);
            await _econ.SetPayeeAsync(StoreA, true, "node", "VCB-123");
            (await _econ.RequestRedemptionAsync(StoreA, 50_000m)).Status.Should().Be(400); // below minimum

            var (ok, _, _, req) = await _econ.RequestRedemptionAsync(StoreA, 200_000m);
            ok.Should().BeTrue();
            req!.Status.Should().Be("pending");
            req.PayoutVnd.Should().Be(198_000m);

            var issuanceBefore = await _wallet.GetBalanceAsync(_wallet.TreasuryKey);
            (await _econ.MarkRedemptionPaidAsync(req.Id, null)).Ok.Should().BeTrue();
            (await _wallet.GetBalanceAsync(_wallet.TreasuryKey)).Should().Be(issuanceBefore + 198_000m);
            (await _wallet.GetBalanceAsync(EconAccounts.ProtocolOps)).Should().Be(2_000m);
        }

        [Fact]
        public async Task Rejected_redemption_refunds_payee()
        {
            await Fund(StoreA, 150_000m);
            await _econ.SetPayeeAsync(StoreA, true, "node", null);
            var (_, _, _, req) = await _econ.RequestRedemptionAsync(StoreA, 150_000m);
            (await _econ.RejectRedemptionAsync(req!.Id, "bank details wrong")).Ok.Should().BeTrue();
            (await _wallet.GetBalanceAsync(StoreA)).Should().Be(150_000m);
        }

        // ---------- §10 tx fees ----------

        [Fact]
        public async Task Social_tx_is_sponsored_and_transfer_is_paid_by_sender()
        {
            await Fund(EconAccounts.NetworkFund, 100m);
            await Fund(Alice, 100m);

            var follow = await _econ.ChargeTxFeeAsync("Follow", Alice, "t1");
            follow.Ok.Should().BeTrue();
            follow.Payer.Should().Be(EconAccounts.NetworkFund);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(100m);

            var transfer = await _econ.ChargeTxFeeAsync("Transfer", Alice, "t2");
            transfer.Payer.Should().Be(Alice);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(99m);
            (await _wallet.GetBalanceAsync(EconAccounts.BlockerFeePool)).Should().Be(1.6m);

            (await _econ.PayoutBlockerPoolAsync(Blocker)).Should().Be(1.6m);
            (await _wallet.GetBalanceAsync(Blocker)).Should().Be(1.6m);
        }

        [Fact]
        public async Task Social_actions_are_rate_limited()
        {
            Build(enforceFees: false, new() { ["Economics:FreeTier:SocialTxPerHour"] = "2" });
            var who = "rate-" + Guid.NewGuid().ToString("N");
            (await _econ.ChargeTxFeeAsync("React", who, "1")).Ok.Should().BeTrue();
            (await _econ.ChargeTxFeeAsync("React", who, "2")).Ok.Should().BeTrue();
            (await _econ.ChargeTxFeeAsync("React", who, "3")).Status.Should().Be(429);
        }

        // ---------- §11 anchors ----------

        [Fact]
        public async Task Anchor_roots_are_reproducible_from_published_entries()
        {
            await Fund(Alice, 5m);
            var a1 = await _econ.CreateAnchorAsync(Blocker);
            a1.Seq.Should().Be(1);
            a1.EntryCount.Should().Be(2); // genesis + fund

            var (anchor, entries) = await _econ.GetAnchorEntriesAsync(1);
            EconomicsService.MerkleRoot(entries.Select(EconomicsService.LedgerLeaf)).Should().Be(anchor!.LedgerRoot);

            (await _econ.CreateAnchorAsync(Blocker)).Seq.Should().Be(1); // nothing new → same anchor
            await Fund(Bob, 1m);
            var a2 = await _econ.CreateAnchorAsync(Blocker);
            a2.Seq.Should().Be(2);
            a2.EntryCount.Should().Be(1);
            a2.LedgerRoot.Should().NotBe(a1.LedgerRoot);
        }
    }
}

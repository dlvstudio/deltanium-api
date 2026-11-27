using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NBitcoin;
using Xunit;

namespace deltanium_api.Tests.Services
{
    /// <summary>Balance proofs (§11 B), reader egress receipts (§7) and automatic re-replication (§6).</summary>
    public class EconomicsVerificationTests : IDisposable, IAsyncLifetime
    {
        private const string Alice = "alice-pk";
        private const string Bob = "bob-pk";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "deltanium-econ-v-" + Guid.NewGuid().ToString("N"));
        private readonly Mock<IStorageService> _storage = new();
        private readonly FakeStores _stores = new();
        private WalletService _wallet = null!;
        private EconomicsService _econ = null!;

        private static readonly List<StoreNode> Nodes = new()
        {
            new() { Id = "sa", PublicKey = "store-a-pk", Endpoint = "http://store-a" },
            new() { Id = "sb", PublicKey = "store-b-pk", Endpoint = "http://store-b" },
            new() { Id = "sc", PublicKey = "store-c-pk", Endpoint = "http://store-c:8080" },
        };

        public EconomicsVerificationTests()
        {
            _storage.Setup(s => s.GetUsersAsync()).ReturnsAsync(new List<User> { new() { PublicKey = Alice, Email = "a@example.test" } });
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(It.IsAny<string>())).ReturnsAsync(true);
            _storage.Setup(s => s.GetNodesAsync()).ReturnsAsync(Nodes);
            _storage.Setup(s => s.GetActiveNodesAsync()).ReturnsAsync(Nodes);
            _storage.Setup(s => s.GetStoredFilesAsync()).ReturnsAsync(new List<StoredFile>());
            var settings = new Dictionary<string, string?>
            {
                ["Economics:DataDir"] = _dir,
                ["Economics:EnforceFees"] = "false",
                ["Economics:HardCap"] = "10000000000000",
                ["InternalApi:Token"] = "tok",
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var http = new Mock<IHttpClientFactory>();
            http.Setup(h => h.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_stores, disposeHandler: false));
            _wallet = new WalletService(config);
            _econ = new EconomicsService(_wallet, new EconDocStore(config), _storage.Object, config, http.Object);
        }

        public Task InitializeAsync() => _wallet.EnsureReadyAsync();
        public Task DisposeAsync() => Task.CompletedTask;

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }

        private async Task Fund(string account, decimal amount) =>
            (await _wallet.ApplyMovesAsync(new[] { new LedgerMove(_wallet.TreasuryKey, account, amount, "test") }, null)).Ok.Should().BeTrue();

        // ---------- Merkle proofs ----------

        [Fact]
        public void Merkle_path_reproduces_root_for_every_leaf_and_size()
        {
            for (var n = 1; n <= 11; n++)
            {
                var leaves = Enumerable.Range(0, n).Select(i => EconomicsService.Sha256Hex("leaf" + i)).ToList();
                var root = EconomicsService.MerkleRoot(leaves);
                for (var i = 0; i < n; i++)
                    EconomicsService.RootFromPath(leaves[i], EconomicsService.MerklePath(leaves, i)).Should().Be(root, $"n={n} i={i}");
            }
        }

        [Fact]
        public async Task Balance_proof_verifies_against_anchored_root_and_tracks_later_moves()
        {
            await Fund(Alice, 5000);
            await Fund(Bob, 70);
            var a1 = await _econ.CreateAnchorAsync("blk");
            a1.LiveMismatchAccounts.Should().Be(0);

            var p = (await _econ.GetBalanceProofAsync(a1.Seq, Alice))!;
            p.Included.Should().BeTrue();
            p.Balance.Should().Be(5000);
            p.Leaf.Should().Be(EconomicsService.BalanceLeafOf(Alice, 5000));
            EconomicsService.RootFromPath(p.Leaf, p.Path).Should().Be(a1.BalancesRoot);

            (await _wallet.TransferAsync(Alice, Bob, 1200)).Ok.Should().BeTrue();
            var a2 = await _econ.CreateAnchorAsync("blk");
            var p2 = (await _econ.GetBalanceProofAsync(a2.Seq, Bob))!;
            p2.Balance.Should().Be(1270);
            EconomicsService.RootFromPath(p2.Leaf, p2.Path).Should().Be(a2.BalancesRoot);
            // The older anchor still proves the older balance.
            (await _econ.GetBalanceProofAsync(a1.Seq, Bob))!.Balance.Should().Be(70);

            var none = (await _econ.GetBalanceProofAsync(a2.Seq, "nobody"))!;
            none.Included.Should().BeFalse();
            none.Balance.Should().Be(0);
        }

        [Fact]
        public async Task Amounts_are_booked_to_the_1e10_unit_and_dust_below_it_is_refused()
        {
            // A 0 D ledger row is refused by the Blockers' LedgerRows check, which would stall the chain's replay.
            await Fund(Alice, 10);
            var dust = await _wallet.TransferAsync(Alice, Bob, 0.00000000001m);
            dust.Ok.Should().BeFalse();
            dust.Error.Should().Contain("smallest unit");
            (await _wallet.TransferAsync(Alice, Bob, 0.00000000016m)).Ok.Should().BeTrue();   // rounds to 2e-10
            (await _wallet.GetBalanceAsync(Bob)).Should().Be(0.0000000002m);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(9.9999999998m);
        }

        [Fact]
        public async Task Balance_repair_sets_live_balances_back_to_the_ledger_replay()
        {
            await Fund(Alice, 300);
            (await _wallet.TransferAsync(Alice, Bob, 100)).Ok.Should().BeTrue();
            // Simulate a lost update: Bob's live balance no longer equals his ledger rows.
            var file = Directory.GetFiles(_dir, "wallet_accounts.json", SearchOption.AllDirectories).Single();
            File.WriteAllText(file, File.ReadAllText(file).Replace("100.0", "90.0"));
            (await _wallet.GetBalanceAsync(Bob)).Should().Be(90m);

            var changed = await _wallet.RepairBalancesFromLedgerAsync();
            changed.Should().ContainSingle(c => c.Account == Bob && c.Was == 90m && c.Now == 100m);
            (await _wallet.GetBalanceAsync(Bob)).Should().Be(100m);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(200m);
            (await _wallet.RepairBalancesFromLedgerAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task Balances_root_equals_replay_of_published_entries()
        {
            await Fund(Alice, 300);
            (await _wallet.TransferAsync(Alice, Bob, 100)).Ok.Should().BeTrue();
            var a1 = await _econ.CreateAnchorAsync(null);
            await Fund(Bob, 5);
            var a2 = await _econ.CreateAnchorAsync(null);

            var balances = new Dictionary<string, decimal>();
            foreach (var a in new[] { a1, a2 })
            {
                var (_, entries) = await _econ.GetAnchorEntriesAsync(a.Seq);
                EconomicsService.ApplyEntries(balances, entries);
                EconomicsService.MerkleRoot(EconomicsService.BalanceLeaves(balances).Select(l => l.Leaf)).Should().Be(a.BalancesRoot);
            }
        }

        // ---------- value tx ----------

        [Fact]
        public void Value_tx_sign_payload_is_the_users_signed_request()
        {
            var body = "{\"toPublicKey\":\"bob\",\"amount\":5}";
            var tx = ValueNetworkTx.BuildSubmitBody("Transfer", "r1", "alice", "bob", 5, DateTime.UtcNow,
                "POST", "/api/wallet/transfer", "1700000000", body, "sig");
            var json = JsonSerializer.Serialize(tx);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("signPayload").GetString()
                .Should().Be(SignatureAuthHelper.BuildSignedMessage("POST", "/api/wallet/transfer", "1700000000", body));
            doc.RootElement.GetProperty("data").GetProperty("amount").GetString().Should().Be("5.0000000000");
        }

        // ---------- egress receipts ----------

        private static EgressReceipt SignedReceipt(Key key, string origin, params (string Block, long Bytes)[] items)
        {
            var r = new EgressReceipt
            {
                Reader = key.PubKey.ToHex(),
                Origin = origin,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Items = items.Select(i => new EgressReceiptItem { Block = i.Block, Bytes = i.Bytes }).ToList()
            };
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(EconomicsService.ReceiptMessage(r)));
            r.Signature = Convert.ToBase64String(key.Sign(new uint256(hash)).ToDER());
            return r;
        }

        private async Task<StorageEscrow> OpenEscrow(string fileId, params string[] nodeIds)
        {
            var blocks = new List<FileBlockHash>
            {
                new() { BlockId = "b0", Sha256 = FakeStores.Hash("b0"), BlockIndex = 0 },
                new() { BlockId = "b1", Sha256 = FakeStores.Hash("b1"), BlockIndex = 1 },
            };
            var (ok, _, err, e, _) = await _econ.OpenStorageEscrowAsync(Alice, fileId, 1000, nodeIds.ToList(), 12, "TimeFixed", blocks);
            ok.Should().BeTrue(err);
            return e!;
        }

        [Fact]
        public async Task Receipts_count_only_signed_known_blocks_for_this_store_once()
        {
            await OpenEscrow("f1", "sa", "sb");
            var key = new Key();
            var good = SignedReceipt(key, "http://store-a", ("b0", 700), ("b1", 300), ("unknown", 50));
            var foreign = SignedReceipt(key, "http://store-b", ("b0", 999));
            var forged = SignedReceipt(key, "http://store-a", ("b0", 10));
            forged.Items[0].Bytes = 10_000; // changed after signing

            var tally = await _econ.TallyReceiptsAsync(Nodes[0], new List<EgressReceipt> { good, foreign, forged }, DateTime.UtcNow);
            tally.Should().ContainKey("f1").WhoseValue.Should().Be(1000);

            var replay = await _econ.TallyReceiptsAsync(Nodes[0], new List<EgressReceipt> { good }, DateTime.UtcNow);
            replay.Should().BeEmpty();
        }

        [Fact]
        public void Receipt_origin_drops_path_and_default_port()
        {
            EconomicsService.ReceiptOrigin("HTTP://Store-A:80/api/").Should().Be("http://store-a");
            EconomicsService.ReceiptOrigin("https://s.example:8443").Should().Be("https://s.example:8443");
        }

        // ---------- cancel after a failed upload ----------

        [Fact]
        public async Task Failed_upload_can_be_cancelled_and_the_payer_is_refunded()
        {
            var e = await OpenEscrow("c1", "sa", "sb");
            await Fund(EconAccounts.Escrow("c1"), 50m);
            _stores.Seed("store-a", "b0", 0); // partial upload: b1 never arrived anywhere

            var r = await _econ.CancelFailedUploadAsync(Alice, "c1");

            r.Ok.Should().BeTrue(r.Error);
            r.Refunded.Should().Be(50m);
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(50m);
            (await _wallet.GetBalanceAsync(EconAccounts.Escrow("c1"))).Should().Be(0m);
            (await _econ.GetEscrowAsync("c1"))!.Status.Should().Be("Closed");
            (await _econ.CancelFailedUploadAsync(Alice, "c1")).Ok.Should().BeTrue(); // idempotent
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(50m);
        }

        [Fact]
        public async Task Completed_upload_cannot_be_cancelled()
        {
            await OpenEscrow("c2", "sa", "sb");
            _stores.Seed("store-b", "b0", 0);
            _stores.Seed("store-b", "b1", 1);

            var r = await _econ.CancelFailedUploadAsync(Alice, "c2");

            r.Ok.Should().BeFalse();
            r.Status.Should().Be(409);
            (await _econ.GetEscrowAsync("c2"))!.Status.Should().Be("Active");
        }

        [Fact]
        public async Task Cancel_is_refused_for_other_owners_and_after_the_window()
        {
            var e = await OpenEscrow("c3", "sa");
            (await _econ.CancelFailedUploadAsync("mallory-pk", "c3")).Status.Should().Be(403);
            (await _econ.CancelFailedUploadAsync(Alice, "c3", e.StartAt.AddHours(2))).Status.Should().Be(409);
            (await _econ.CancelFailedUploadAsync(Alice, "missing")).Status.Should().Be(404);
        }

        // ---------- data lost to the old Store deploy ----------

        [Fact]
        public async Task Data_loss_reconcile_marks_unrecoverable_files_lost_refunds_and_never_slashes()
        {
            await OpenEscrow("lost1", "sa", "sb");
            await Fund(EconAccounts.Escrow("lost1"), 40m);
            var e = (await _econ.GetEscrowAsync("lost1"))!;
            e.VerifiedNodeIds.AddRange(new[] { "sa", "sb" }); // both had passed challenges before the deploy wiped them
            await _econ.RecordProofAsync(e, "sa", true, null, DateTime.UtcNow);

            var r = await _econ.ReconcileStoreDataLossAsync(DateTime.UtcNow.AddMinutes(1));

            r.Lost.Should().Be(1);
            r.NodesDropped.Should().Be(2);
            var after = (await _econ.GetEscrowAsync("lost1"))!;
            after.Status.Should().Be("Lost");
            after.FailedNodeIds.Should().BeEquivalentTo(new[] { "sa", "sb" });
            after.VerifiedNodeIds.Should().BeEmpty();
            (await _wallet.GetBalanceAsync(Alice)).Should().Be(40m);
            _storage.Verify(s => s.DeactivateStoredFileAsync("lost1"), Times.Once);
            (await _econ.ListSlashesAsync()).Should().BeEmpty();

            (await _econ.ReconcileStoreDataLossAsync(DateTime.UtcNow.AddMinutes(1))).Checked.Should().Be(0); // once per cutoff
        }

        [Fact]
        public async Task Data_loss_reconcile_drops_only_the_store_that_lost_blocks_and_settlement_does_not_slash_it()
        {
            await OpenEscrow("half", "sa", "sb");
            _stores.Seed("store-b", "b0", 0);
            _stores.Seed("store-b", "b1", 1);
            var e = (await _econ.GetEscrowAsync("half"))!;
            e.VerifiedNodeIds.AddRange(new[] { "sa", "sb" });
            await _econ.RecordProofAsync(e, "sa", true, null, DateTime.UtcNow);

            var r = await _econ.ReconcileStoreDataLossAsync(DateTime.UtcNow.AddMinutes(1));

            r.Lost.Should().Be(0);
            var after = (await _econ.GetEscrowAsync("half"))!;
            after.Status.Should().Be("Active");
            after.FailedNodeIds.Should().Contain("sa").And.NotContain("sb");
            after.VerifiedNodeIds.Should().NotContain("sa");
            await _econ.SettleDueAsync(DateTime.UtcNow.AddMonths(1).AddDays(1));
            (await _econ.ListSlashesAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task Data_loss_reconcile_ignores_escrows_opened_after_the_cutoff()
        {
            await OpenEscrow("newer", "sa");
            var r = await _econ.ReconcileStoreDataLossAsync(DateTime.UtcNow.AddHours(-1));
            r.Checked.Should().Be(0);
            (await _econ.GetEscrowAsync("newer"))!.FailedNodeIds.Should().BeEmpty();
        }

        // ---------- re-replication ----------

        [Fact]
        public async Task Failed_replica_is_copied_from_healthy_store_to_a_new_one()
        {
            var e = await OpenEscrow("f2", "sa", "sb");
            _stores.Seed("store-a", "b0", 0);
            _stores.Seed("store-a", "b1", 1);
            e.FailedNodeIds.Add("sb");

            var added = await _econ.RepairEscrowAsync(e, Nodes.ToDictionary(n => n.Id));

            added.Should().Be(1);
            e.StoreNodeIds.Should().Contain("sc");
            _stores.Blocks("store-c").Keys.Should().BeEquivalentTo("b0", "b1");
            _stores.ImportedMetadata.Should().Be(1);
            _storage.Verify(s => s.SaveStoredFileAsync(It.Is<StoredFile>(f => f.FileId == "f2" && f.StoreNodeId == "sc")), Times.Once);
            (await _econ.ListReplicationEventsAsync("f2")).Should().ContainSingle(r => r.Ok && r.BlocksCopied == 2);
        }

        [Fact]
        public async Task Upload_to_one_store_with_two_paid_replicas_is_copied_to_a_second_store()
        {
            var blocks = new List<FileBlockHash>
            {
                new() { BlockId = "b0", Sha256 = FakeStores.Hash("b0"), BlockIndex = 0 },
                new() { BlockId = "b1", Sha256 = FakeStores.Hash("b1"), BlockIndex = 1 },
            };
            var (ok, _, err, e, quote) = await _econ.OpenStorageEscrowAsync(Alice, "r2", 1000, new List<string> { "sa" }, 12, "TimeFixed", blocks, replicas: 2);
            ok.Should().BeTrue(err);
            e!.TargetReplicas.Should().Be(2);
            quote!.Replicas.Should().Be(2);
            EconomicsService.UnderReplicated(e).Should().BeTrue();

            _stores.Seed("store-a", "b0", 0);
            _stores.Seed("store-a", "b1", 1);
            (await _econ.RepairReplicasAsync()).Should().Be(1);

            var after = (await _econ.GetEscrowAsync("r2"))!;
            after.StoreNodeIds.Should().HaveCount(2).And.Contain("sa");
            EconomicsService.UnderReplicated(after).Should().BeFalse();
            (await _econ.RepairReplicasAsync()).Should().Be(0);
        }

        [Fact]
        public async Task Requested_replicas_are_capped_by_max_and_by_existing_stores()
        {
            var (_, _, _, e, _) = await _econ.OpenStorageEscrowAsync(Alice, "r9", 1000, new List<string> { "sa" }, 12, "TimeFixed", null, replicas: 9);
            e!.TargetReplicas.Should().Be(3); // 3 Stores exist, MaxReplicas defaults to 3
        }

        [Fact]
        public async Task Corrupt_source_block_is_not_replicated()
        {
            var e = await OpenEscrow("f3", "sa", "sb");
            _stores.Seed("store-a", "b0", 0);
            _stores.Seed("store-a", "b1", 1, corrupt: true);
            e.FailedNodeIds.Add("sb");

            (await _econ.RepairEscrowAsync(e, Nodes.ToDictionary(n => n.Id))).Should().Be(0);
            e.StoreNodeIds.Should().NotContain("sc");
            (await _econ.ListReplicationEventsAsync("f3")).Should().OnlyContain(r => !r.Ok);
        }

        /// <summary>In-memory Stores speaking the internal /api/proof endpoints.</summary>
        private sealed class FakeStores : HttpMessageHandler
        {
            private readonly Dictionary<string, Dictionary<string, (byte[] Data, int Index)>> _blocks = new();
            public int ImportedMetadata;

            public static byte[] Content(string blockId) => Encoding.UTF8.GetBytes("cipher-" + blockId);
            public static string Hash(string blockId) => Convert.ToHexString(SHA256.HashData(Content(blockId))).ToLowerInvariant();

            public Dictionary<string, (byte[] Data, int Index)> Blocks(string host) =>
                _blocks.TryGetValue(host, out var b) ? b : _blocks[host] = new();

            public void Seed(string host, string blockId, int index, bool corrupt = false) =>
                Blocks(host)[blockId] = (corrupt ? Encoding.UTF8.GetBytes("bad") : Content(blockId), index);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var host = request.RequestUri!.Host;
                var path = request.RequestUri.AbsolutePath;
                var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                object? result = null;
                switch (path)
                {
                    case "/api/proof/challenge":
                        var id = root.GetProperty("blockId").GetString()!;
                        if (!Blocks(host).TryGetValue(id, out var blk)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                        result = new { blockId = id, nonce = root.GetProperty("nonce").GetString(), blockIndex = blk.Index, dataBase64 = Convert.ToBase64String(blk.Data) };
                        break;
                    case "/api/proof/export-metadata":
                        result = new { items = new[] { new { fileId = root.GetProperty("fileId").GetString(), recipientPubKey = Alice } } };
                        break;
                    case "/api/proof/import-metadata":
                        ImportedMetadata += root.GetProperty("items").GetArrayLength();
                        result = new { ok = true };
                        break;
                    case "/api/proof/import-block":
                        Blocks(host)[root.GetProperty("blockId").GetString()!] =
                            (Convert.FromBase64String(root.GetProperty("dataBase64").GetString()!), root.GetProperty("blockIndex").GetInt32());
                        result = new { ok = true };
                        break;
                    default:
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
                };
            }
        }
    }
}

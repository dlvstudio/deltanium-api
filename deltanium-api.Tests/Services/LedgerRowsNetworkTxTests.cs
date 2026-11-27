using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using FluentAssertions;
using Xunit;

namespace deltanium_api.Tests.Services
{
    public class LedgerRowsNetworkTxTests
    {
        private static WalletLedgerEntry E(int i, string? from = "treasury") => new()
        {
            Id = $"row{i}",
            FromPublicKey = from,
            ToPublicKey = "alice",
            Amount = 0.7771m + i,
            Kind = "escrow:fund",
            RefId = i % 2 == 0 ? null : "ref",
            Memo = "free text that must not go on chain",
            CreatedAt = new DateTime(2026, 11, 27, 1, 2, 3, DateTimeKind.Utc).AddTicks(i * 17),
        };

        [Fact]
        public void Each_row_on_chain_hashes_to_the_same_leaf_as_the_anchor()
        {
            var entries = new[] { E(1), E(2, from: null) };
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(LedgerRowsNetworkTx.Parts(entries).Single())!;
            for (var i = 0; i < entries.Length; i++)
            {
                var r = rows[i];
                var leaf = EconomicsService.Sha256Hex(string.Join("|", r["id"], r["from"] ?? "", r["to"] ?? "", r["amount"], r["kind"], r["refId"] ?? "", r["at"]));
                leaf.Should().Be(EconomicsService.LedgerLeaf(entries[i]));
                r.Should().NotContainKey("memo");
            }
        }

        [Fact]
        public void Large_windows_split_into_parts_in_anchor_order()
        {
            var entries = Enumerable.Range(0, 450).Select(i => E(i)).ToList();
            var parts = LedgerRowsNetworkTx.Parts(entries);
            parts.Should().HaveCount(3);
            parts.SelectMany(p => JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(p)!).Select(r => r["id"])
                .Should().Equal(entries.Select(e => e.Id));
            LedgerRowsNetworkTx.Parts(new List<WalletLedgerEntry>()).Should().Equal("[]");
            LedgerRowsNetworkTx.TxId(7, 2).Should().Be("lr-000000000007-0002");
        }
    }
}

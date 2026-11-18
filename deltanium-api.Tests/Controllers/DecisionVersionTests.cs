using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DeltaniumApi.Controllers;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using FluentAssertions;
using Moq;
using NBitcoin;
using Xunit;

namespace deltanium_api.Tests.Controllers
{
    public class DecisionVersionTests
    {
        [Fact]
        public async Task Decision_carries_the_api_version_signed_by_Central_for_that_tx()
        {
            var keys = new KeyService();
            var storage = new Mock<IStorageService>();
            storage.Setup(s => s.GetActiveBlockCreatorNodesAsync()).ReturnsAsync(new List<BlockCreatorNode> { new() { PublicKey = "02bb" } });
            var c = new BlockCreatorNodeController(keys, storage.Object, null!, null!);

            var r = (await c.GetDecision(new BlockCreatorNodeController.DecisionRequest { TxId = "tx1", BlockerPubKey = "02bb" })).Value!;

            var v = r.Version!;
            v.Service.Should().Be("api");
            v.Version.Should().Be(BuildInfo.Version);
            v.Commit.Should().Be(BuildInfo.Commit);
            v.PublicKey.Should().Be(r.CentralPublicKey);
            var msg = BlockCreatorNodeController.VersionAttestation.Message("tx1", "api", v.Version, v.Commit);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope(msg)));
            new PubKey(v.PublicKey).Verify(new uint256(hash), Convert.FromBase64String(v.Signature)).Should().BeTrue();
            var other = SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope(BlockCreatorNodeController.VersionAttestation.Message("tx2", "api", v.Version, v.Commit))));
            new PubKey(v.PublicKey).Verify(new uint256(other), Convert.FromBase64String(v.Signature)).Should().BeFalse();
        }
    }
}

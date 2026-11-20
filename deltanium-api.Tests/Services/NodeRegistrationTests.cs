using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    [Collection("SuperAdminEnv")]
    public class NodeRegistrationTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "deltanium-nodereg-" + Guid.NewGuid().ToString("N"));
        private readonly Mock<IStorageService> _storage = new();
        private readonly Key _superAdmin = new();
        private readonly List<StoreNode> _stores = new();
        private readonly HashSet<string> _vouchers = new() { "V-ONE", "V-TWO" };
        private bool _nodeAnswers = true;

        private class TestService : NodeRegistrationService
        {
            private readonly Func<bool> _answers;
            public TestService(EconDocStore d, IStorageService s, KeyService k, IConfiguration c, Func<bool> answers) : base(d, s, k, c) => _answers = answers;
            public override Task<(bool Ok, string Error)> ProbeAsync(NodeRegistration reg) =>
                Task.FromResult(_answers() ? (true, "") : (false, "connection refused"));
        }

        public NodeRegistrationTests()
        {
            Environment.SetEnvironmentVariable("SUPER_ADMIN_PUBLIC_KEY", _superAdmin.PubKey.ToHex());
            Environment.SetEnvironmentVariable("SUPER_ADMIN_PRIVATE_KEY", null);
            _storage.Setup(s => s.GetNodesAsync()).ReturnsAsync(() => new List<StoreNode>(_stores));
            _storage.Setup(s => s.GetBlockCreatorNodesAsync()).ReturnsAsync(new List<BlockCreatorNode>());
            _storage.Setup(s => s.IsVoucherValidAsync(It.IsAny<string>())).ReturnsAsync((string v) => _vouchers.Contains(v));
            _storage.Setup(s => s.TryMarkVoucherUsedAsync(It.IsAny<string>())).ReturnsAsync((string v) => _vouchers.Remove(v));
            _storage.Setup(s => s.SaveNodeAsync(It.IsAny<StoreNode>())).Callback((StoreNode n) => _stores.Add(n)).Returns(Task.CompletedTask);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private NodeRegistrationService Service()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Economics:DataDir"] = _dir,
                ["Nodes:Registration:FeeVnd"] = "100000",
            }).Build();
            return new TestService(new EconDocStore(config), _storage.Object, new KeyService(), config, () => _nodeAnswers);
        }

        private static string Sign(Key key, string message) =>
            Convert.ToBase64String(key.Sign(new uint256(SHA256.HashData(Encoding.UTF8.GetBytes(NetworkScope.Scope(message))))).ToDER());

        private static Task<NodeRegistrationService.SubmitResult> Register(NodeRegistrationService svc, Key node, string voucher, Key? signer = null, long? ts = null)
        {
            var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pk = node.PubKey.ToHex();
            var sig = Sign(signer ?? node, NodeRegistrationService.RegistrationMessage("store", pk, "", "tcps://auto:7333", voucher, t));
            return svc.SubmitAsync("store", "", "tcps://auto:7333", voucher, pk, t, sig, IPAddress.Parse("203.0.113.9"));
        }

        [Fact]
        public async Task Registration_must_be_signed_by_the_node_key()
        {
            var svc = Service();
            var node = new Key();
            (await svc.SubmitAsync("store", "", "tcps://auto:7333", "V-ONE", node.PubKey.ToHex(), 0, null, IPAddress.Loopback)).Status.Should().Be(400);
            (await Register(svc, node, "V-ONE", signer: new Key())).Status.Should().Be(401, "someone else's key cannot register this node");
            (await Register(svc, node, "V-ONE", ts: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds())).Status.Should().Be(400);
            _vouchers.Should().Contain("V-ONE", "nothing was spent");
        }

        [Fact]
        public async Task A_signed_registration_waits_for_the_fee_and_does_not_spend_the_voucher()
        {
            var svc = Service();
            var r = await Register(svc, new Key(), "V-ONE");
            r.Status.Should().Be(202);
            r.Registration!.Status.Should().Be("awaiting_payment");
            r.Registration.FeeVnd.Should().Be(100000);
            r.Registration.TcpEndpoint.Should().Be("tcps://203.0.113.9:7333");
            NodeRegistrationService.TransferContent(r.Registration).Should().StartWith("NODE").And.HaveLength(14);
            _vouchers.Should().Contain("V-ONE");
            _stores.Should().BeEmpty("not a node until approved and reachable");
        }

        [Fact]
        public async Task Super_Admin_approval_spends_the_voucher_and_activates_a_node_that_answers()
        {
            var svc = Service();
            var node = new Key();
            var reg = (await Register(svc, node, "V-ONE")).Registration!;

            (await svc.ApproveAsync(reg.Id, Sign(new Key(), NodeRegistrationService.ApprovalPayload(reg)))).Status.Should().Be(401);
            _vouchers.Should().Contain("V-ONE");

            var ok = await svc.ApproveAsync(reg.Id, Sign(_superAdmin, NodeRegistrationService.ApprovalPayload(reg)));
            ok.Status.Should().Be(200);
            ok.Registration!.Status.Should().Be("active");
            _vouchers.Should().NotContain("V-ONE");
            _stores.Should().ContainSingle(n => n.PublicKey == node.PubKey.ToHex() && n.TcpEndpoint == "tcps://203.0.113.9:7333");
        }

        [Fact]
        public async Task An_approved_node_that_does_not_answer_stays_inactive_until_it_does()
        {
            var svc = Service();
            _nodeAnswers = false;
            var reg = (await Register(svc, new Key(), "V-ONE")).Registration!;
            (await svc.ApproveAsync(reg.Id, Sign(_superAdmin, NodeRegistrationService.ApprovalPayload(reg)))).Registration!.Status.Should().Be("approved");
            _stores.Should().BeEmpty();
            (await svc.ActivateApprovedAsync()).Should().Be(0);

            _nodeAnswers = true;
            (await svc.ActivateApprovedAsync()).Should().Be(1);
            _stores.Should().HaveCount(1);
        }

        [Fact]
        public async Task Squatting_cannot_burn_a_voucher_the_first_approved_registration_gets_it()
        {
            var svc = Service();
            var fake = (await Register(svc, new Key(), "V-ONE")).Registration!;
            var real = (await Register(svc, new Key(), "V-ONE")).Registration!;
            (await svc.ApproveAsync(real.Id, Sign(_superAdmin, NodeRegistrationService.ApprovalPayload(real)))).Registration!.Status.Should().Be("active");
            var late = await svc.ApproveAsync(fake.Id, Sign(_superAdmin, NodeRegistrationService.ApprovalPayload(fake)));
            late.Status.Should().Be(409);
            late.Registration!.Status.Should().Be("rejected");
        }

        [Fact]
        public void Only_recently_seen_nodes_are_offered_and_never_none()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:Provider"] = "MySql", ["Nodes:OnlineWindowMinutes"] = "30" }).Build();
            var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
            var nodes = new[] { ("fresh", now.AddMinutes(-5)), ("stale", now.AddHours(-3)) };
            NodeOnline.Filter(nodes, n => n.Item2, config, now).Should().ContainSingle(n => n.Item1 == "fresh");
            NodeOnline.Filter(new[] { ("stale", now.AddHours(-3)) }, n => n.Item2, config, now).Should().HaveCount(1, "never an empty network");
            NodeOnline.Filter(nodes, n => n.Item2, new ConfigurationBuilder().Build(), now).Should().HaveCount(2, "JSON storage keeps no LastSeen");
        }
    }
}

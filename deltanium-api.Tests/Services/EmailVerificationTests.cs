using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DeltaniumApi.Controllers;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace deltanium_api.Tests.Services
{
    /// <summary>Free tier needs a verified email (§10), one account per address; Sybil brakes on registration.</summary>
    public class EmailVerificationTests : IDisposable
    {
        private const string Alice = "alice-pk", Carol = "carol-pk", Mallory = "mallory-pk";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "deltanium-email-" + Guid.NewGuid().ToString("N"));
        private readonly Mock<IStorageService> _storage = new();
        private readonly Mock<IEmailSender> _sender = new();

        public EmailVerificationTests()
        {
            _storage.Setup(s => s.GetUsersAsync()).ReturnsAsync(new List<User>
            {
                new() { PublicKey = Alice, Email = "a@example.test", CreatedAt = DateTime.UtcNow.AddDays(-2) },
                new() { PublicKey = Carol, Email = "C@Example.test", CreatedAt = DateTime.UtcNow.AddDays(-1) },
                new() { PublicKey = Mallory, Email = "a@EXAMPLE.test", CreatedAt = DateTime.UtcNow },
            });
            _storage.Setup(s => s.IsPublicKeyRegisteredAsync(It.IsAny<string>())).ReturnsAsync(true);
            _storage.Setup(s => s.GetNodesAsync()).ReturnsAsync(new List<StoreNode> { new() { Id = "sa", PublicKey = "store-a", Endpoint = "http://a" } });
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private (EconomicsService Econ, EmailVerificationService Verify, WalletService Wallet) Build(Dictionary<string, string?>? extra = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Economics:DataDir"] = _dir,
                ["Economics:EnforceFees"] = "true",
                ["Economics:HardCap"] = "10000000000000",
            };
            if (extra != null) foreach (var kv in extra) settings[kv.Key] = kv.Value;
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var wallet = new WalletService(config);
            wallet.EnsureReadyAsync().GetAwaiter().GetResult();
            var econ = new EconomicsService(wallet, new EconDocStore(config), _storage.Object, config);
            return (econ, new EmailVerificationService(new EconDocStore(config), _sender.Object), wallet);
        }

        [Fact]
        public async Task Code_verifies_the_address_and_is_never_stored_in_clear()
        {
            var (econ, verify, _) = Build(new() { ["Economics:FreeTier:RequireVerifiedEmail"] = "true" });
            (await econ.IsFreeTierEligibleAsync(Alice)).Should().BeFalse();

            string? code = null;
            (await verify.SendCodeAsync(Alice, "a@example.test", null, c => { code = c; return Task.CompletedTask; })).Ok.Should().BeTrue();
            code.Should().MatchRegex("^[0-9]{6}$");
            var stored = await verify.GetAsync(Alice);
            stored!.CodeHash.Should().NotContain(code);

            var wrong = code == "000000" ? "111111" : "000000";
            (await verify.VerifyAsync(Alice, wrong)).Status.Should().Be(400);
            (await verify.VerifyAsync(Alice, code!)).Ok.Should().BeTrue();
            (await econ.IsFreeTierEligibleAsync(Alice)).Should().BeTrue();
        }

        [Fact]
        public async Task Wrong_codes_lock_out_and_codes_expire()
        {
            var (_, verify, _) = Build();
            string? code = null;
            var t0 = DateTime.UtcNow;
            await verify.SendCodeAsync(Alice, "a@example.test", t0, c => { code = c; return Task.CompletedTask; });
            var wrong = code == "000000" ? "111111" : "000000";
            for (var i = 0; i < EmailVerificationService.MaxAttempts; i++) await verify.VerifyAsync(Alice, wrong, t0);
            (await verify.VerifyAsync(Alice, code!, t0)).Status.Should().Be(429);

            await verify.SendCodeAsync(Alice, "a@example.test", t0.AddMinutes(2), c => { code = c; return Task.CompletedTask; });
            (await verify.VerifyAsync(Alice, code!, t0.AddMinutes(30))).Status.Should().Be(410);
        }

        [Fact]
        public async Task One_verified_address_unlocks_one_account_and_sends_are_rate_limited()
        {
            var (_, verify, _) = Build();
            string? code = null;
            await verify.SendCodeAsync(Alice, "a@example.test", null, c => { code = c; return Task.CompletedTask; });
            (await verify.VerifyAsync(Alice, code!)).Ok.Should().BeTrue();

            (await verify.SendCodeAsync(Mallory, "A@example.TEST", null, _ => Task.CompletedTask)).Status.Should().Be(409);
            (await verify.SendCodeAsync(Carol, "c@example.test", null, _ => Task.CompletedTask)).Ok.Should().BeTrue();
            (await verify.SendCodeAsync(Carol, "c@example.test", null, _ => Task.CompletedTask)).Status.Should().Be(429); // one a minute
        }

        [Fact]
        public async Task Sending_needs_smtp_configured()
        {
            var (_, verify, _) = Build();
            _sender.SetupGet(s => s.IsConfigured).Returns(false);
            (await verify.SendCodeAsync(Alice, "a@example.test")).Status.Should().Be(503);
        }

        [Fact]
        public async Task Without_verification_each_address_counts_once()
        {
            var (econ, _, _) = Build();
            (await econ.IsFreeTierEligibleAsync(Alice)).Should().BeTrue();
            (await econ.IsFreeTierEligibleAsync(Mallory)).Should().BeFalse(); // same address, registered later
            (await econ.IsFreeTierEligibleAsync(Carol)).Should().BeTrue();
        }

        [Fact]
        public async Task Unfunded_free_tier_stops_at_the_network_budget_but_social_items_still_pass()
        {
            var (econ, _, wallet) = Build(new() { ["Economics:FreeTier:UnfundedBudgetBytes"] = "3000" });
            (await econ.OpenStorageEscrowAsync(Alice, "p1", 2000, new[] { "sa" }, 12, null, null)).Ok.Should().BeTrue();
            var over = await econ.OpenStorageEscrowAsync(Carol, "p2", 2000, new[] { "sa" }, 12, null, null);
            over.Ok.Should().BeFalse();
            over.Status.Should().Be(402);
            (await econ.OpenStorageEscrowAsync(Carol, "like", 500, new[] { "sa" }, 12, null, null, social: true)).Ok.Should().BeTrue();
            (await wallet.GetBalanceAsync(Carol)).Should().Be(0m);
        }

        [Fact]
        public void Registrations_per_ip_are_capped_per_day()
        {
            var day = DateTime.UtcNow.AddYears(1);
            var ip = "198.51.100." + Random.Shared.Next(1, 250);
            UserController.RegistrationLimiter.TryAcquire(ip, 2, day).Should().BeTrue();
            UserController.RegistrationLimiter.TryAcquire(ip, 2, day).Should().BeTrue();
            UserController.RegistrationLimiter.TryAcquire(ip, 2, day).Should().BeFalse();
            UserController.RegistrationLimiter.TryAcquire(ip, 2, day.AddDays(1)).Should().BeTrue();
        }
    }
}

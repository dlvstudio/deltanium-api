using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly IWalletService _wallet;
        private readonly SignatureAuthHelper _auth;
        private readonly KeyService _keys;
        private readonly IBlockerTxSubmitter _blockerTx;
        private readonly IConfiguration _config;
        private readonly EconomicsService _econ;

        public WalletController(
            IWalletService wallet,
            SignatureAuthHelper auth,
            KeyService keys,
            IBlockerTxSubmitter blockerTx,
            IConfiguration config,
            EconomicsService econ)
        {
            _wallet = wallet;
            _auth = auth;
            _keys = keys;
            _blockerTx = blockerTx;
            _config = config;
            _econ = econ;
        }

        private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private async Task<string> ReadBodyAsync()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            return body;
        }

        private async Task<(bool Ok, string? Pk, string Body, IActionResult? Fail)> AuthAsync(bool requireRegisteredUser = true)
        {
            var body = await ReadBodyAsync();
            var (ok, pk, err) = await _auth.ValidateAsync(Request, body, requireRegisteredUser);
            if (!ok) return (false, null, body, Unauthorized(err));
            await _wallet.EnsureReadyAsync();
            return (true, pk, body, null);
        }

        public class AmountRequest
        {
            public decimal Amount { get; set; }
        }

        public class EgressCapRequest
        {
            public decimal MaxDPerMonth { get; set; }
        }

        public class PayeeRequest
        {
            public string PublicKey { get; set; } = "";
            public bool Verified { get; set; } = true;
            public string Role { get; set; } = "creator";
            public string? BankRef { get; set; }
        }

        public class RedemptionActionRequest
        {
            public string Id { get; set; } = "";
            public string? BankRef { get; set; }
            public string? Note { get; set; }
        }

        public class SlashRequest
        {
            public string NodePublicKey { get; set; } = "";
            public decimal Percent { get; set; }
            public string Reason { get; set; } = "";
            public List<string>? AffectedAccounts { get; set; }
        }

        // ---------------- v2: reserve, usage, anchors (public / signed) ----------------

        /// <summary>Public proof-of-reserve snapshot (TOKENOMICS_V2 §11).</summary>
        [HttpGet("reserve")]
        public async Task<IActionResult> Reserve()
        {
            await _wallet.EnsureReadyAsync();
            return Ok(await _econ.GetReserveAsync());
        }

        /// <summary>Caller's free tier, storage, egress, subscriptions.</summary>
        [HttpGet("usage")]
        public async Task<IActionResult> Usage()
        {
            var a = await AuthAsync();
            if (!a.Ok) return a.Fail!;
            return Ok(await _econ.GetUsageAsync(a.Pk!));
        }

        /// <summary>Uploader's monthly bandwidth spending cap (0 = no cap).</summary>
        [HttpPost("egress-cap")]
        public async Task<IActionResult> SetEgressCap()
        {
            var a = await AuthAsync();
            if (!a.Ok) return a.Fail!;
            var req = System.Text.Json.JsonSerializer.Deserialize<EgressCapRequest>(a.Body, JsonOpts);
            if (req == null || req.MaxDPerMonth < 0) return BadRequest("maxDPerMonth >= 0 required");
            await _econ.SetEgressCapAsync(a.Pk!, req.MaxDPerMonth);
            return Ok(new { maxDPerMonth = req.MaxDPerMonth });
        }

        /// <summary>Ledger anchors committed into blocks (public, §11 phase A).</summary>
        [HttpGet("anchors")]
        public async Task<IActionResult> Anchors([FromQuery] int limit = 50)
        {
            await _wallet.EnsureReadyAsync();
            return Ok(await _econ.ListAnchorsAsync(limit));
        }

        /// <summary>Ledger rows covered by one anchor so anyone can recompute its root.</summary>
        /// <summary>Merkle proof of one account's balance in an anchor's BalancesRoot (public, §11 phase B).</summary>
        [HttpGet("anchors/{seq:long}/balance-proof/{account}")]
        public async Task<IActionResult> BalanceProof(long seq, string account)
        {
            await _wallet.EnsureReadyAsync();
            var proof = await _econ.GetBalanceProofAsync(seq, Uri.UnescapeDataString(account));
            if (proof == null) return NotFound();
            return Ok(proof);
        }

        /// <summary>Mirror a booked user value move on chain with the user's signed request (§11 phase B). Best effort.</summary>
        private async Task<object> MirrorValueTxAsync(string type, string refId, string from, string to, decimal amount, string body)
        {
            try
            {
                var ledgerAt = (await _wallet.GetLedgerAsync(from, 20))
                    .Where(e => e.RefId == refId && e.FromPublicKey == from.Trim())
                    .Select(e => e.CreatedAt).DefaultIfEmpty(DateTime.UtcNow).Max();
                var txBody = ValueNetworkTx.BuildSubmitBody(type, refId, from, to, amount, ledgerAt,
                    Request.Method, $"{Request.Path}{Request.QueryString}",
                    Request.Headers["X-Timestamp"].ToString(), body, Request.Headers["X-Signature"].ToString());
                var (ok, detail) = await _blockerTx.SubmitAsync(txBody);
                return new { submitted = ok, txId = refId, detail };
            }
            catch (Exception ex)
            {
                return new { submitted = false, txId = refId, detail = ex.Message };
            }
        }

        [HttpGet("anchors/{seq:long}/entries")]
        public async Task<IActionResult> AnchorEntries(long seq)
        {
            await _wallet.EnsureReadyAsync();
            var (anchor, entries) = await _econ.GetAnchorEntriesAsync(seq);
            if (anchor == null) return NotFound();
            return Ok(new
            {
                anchor,
                leafFormat = "sha256(id|from|to|amount:F10|kind|refId|createdAt:yyyy-MM-ddTHH:mm:ss.ffffffZ), pairwise sha256(hexA+hexB)",
                entries
            });
        }

        // ---------------- v2: redemption (§4) ----------------

        [HttpPost("redeem")]
        public async Task<IActionResult> Redeem()
        {
            var a = await AuthAsync(requireRegisteredUser: false);
            if (!a.Ok) return a.Fail!;
            var req = System.Text.Json.JsonSerializer.Deserialize<AmountRequest>(a.Body, JsonOpts);
            if (req == null || req.Amount <= 0) return BadRequest("amount required");
            var (ok, status, err, r) = await _econ.RequestRedemptionAsync(a.Pk!, req.Amount);
            if (!ok) return StatusCode(status, err);
            return Ok(r);
        }

        [HttpGet("redemptions")]
        public async Task<IActionResult> MyRedemptions()
        {
            var a = await AuthAsync(requireRegisteredUser: false);
            if (!a.Ok) return a.Fail!;
            return Ok(await _econ.ListRedemptionsAsync(publicKey: await _econ.ResolveAccountAsync(a.Pk!)));
        }

        // ---------------- v2: node bonds (§8) ----------------

        [HttpGet("bond/{publicKey}")]
        public async Task<IActionResult> Bond(string publicKey)
        {
            await _wallet.EnsureReadyAsync();
            var node = await _econ.FindNodeAsync(publicKey);
            if (node == null) return NotFound("Not a registered node");
            return Ok(new
            {
                node = node.Value.PublicKey,
                kind = node.Value.Kind,
                bond = await _econ.GetBondAsync(node.Value.PublicKey),
                required = await _econ.RequiredBondAsync(node.Value.PublicKey),
                enforced = _econ.EnforceBonds
            });
        }

        [HttpPost("bond/deposit")]
        public async Task<IActionResult> BondDeposit()
        {
            var a = await AuthAsync(requireRegisteredUser: false);
            if (!a.Ok) return a.Fail!;
            var req = System.Text.Json.JsonSerializer.Deserialize<AmountRequest>(a.Body, JsonOpts);
            if (req == null || req.Amount <= 0) return BadRequest("amount required");
            var (ok, err) = await _econ.DepositBondAsync(a.Pk!, req.Amount);
            if (!ok) return BadRequest(err);
            var node = await _econ.FindNodeAsync(a.Pk!);
            return Ok(new { bond = await _econ.GetBondAsync(node!.Value.PublicKey) });
        }

        [HttpPost("bond/withdraw")]
        public async Task<IActionResult> BondWithdraw()
        {
            var a = await AuthAsync(requireRegisteredUser: false);
            if (!a.Ok) return a.Fail!;
            var req = System.Text.Json.JsonSerializer.Deserialize<AmountRequest>(a.Body, JsonOpts);
            if (req == null) return BadRequest("amount required");
            var (ok, err, wd) = await _econ.RequestBondWithdrawalAsync(a.Pk!, req.Amount);
            if (!ok) return BadRequest(err);
            return Ok(wd);
        }

        [HttpGet("slashes")]
        public async Task<IActionResult> Slashes([FromQuery] int limit = 100)
        {
            await _wallet.EnsureReadyAsync();
            return Ok(await _econ.ListSlashesAsync(limit));
        }

        // ---------------- v2: ops (admin auth: Super Admin request signature or admin token) ----------------

        [HttpPost("admin/payee")]
        public async Task<IActionResult> AdminSetPayee()
        {
            if (await AdminDeniedAsync() is { } denied) return denied;
            var body = await ReadBodyAsync();
            var req = System.Text.Json.JsonSerializer.Deserialize<PayeeRequest>(body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.PublicKey)) return BadRequest("publicKey required");
            await _wallet.EnsureReadyAsync();
            return Ok(await _econ.SetPayeeAsync(req.PublicKey.Trim(), req.Verified, req.Role, req.BankRef));
        }

        [HttpGet("admin/redemptions")]
        public async Task<IActionResult> AdminRedemptions([FromQuery] string status = "pending")
        {
            if (await AdminDeniedAsync() is { } denied) return denied;
            await _wallet.EnsureReadyAsync();
            var list = await _econ.ListRedemptionsAsync(status);
            return Ok(list.Select(r => new { request = r, signPayload = EconomicsService.RedemptionSignPayload(r) }));
        }

        /// <summary>
        /// Mark a redemption paid after the VND transfer left the reserve account.
        /// Requires X-SuperAdmin-Signature over the redemption SignPayload (the Super Admin key never leaves the admin's machine).
        /// </summary>
        [HttpPost("admin/redemption/paid")]
        public async Task<IActionResult> AdminRedemptionPaid()
        {
            // Authority is the Super Admin signature over the redemption SignPayload checked below.
            var body = await ReadBodyAsync();
            var req = System.Text.Json.JsonSerializer.Deserialize<RedemptionActionRequest>(body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.Id)) return BadRequest("id required");
            await _wallet.EnsureReadyAsync();
            var r = await _econ.GetRedemptionAsync(req.Id);
            if (r == null) return NotFound();
            if (!Request.Headers.TryGetValue("X-SuperAdmin-Signature", out var sa) ||
                !_keys.VerifySignature(_keys.GetSuperAdminPublicKey(), EconomicsService.RedemptionSignPayload(r), sa.FirstOrDefault() ?? ""))
                return Unauthorized("Invalid or missing X-SuperAdmin-Signature over the redemption SignPayload");
            var (ok, err, updated) = await _econ.MarkRedemptionPaidAsync(req.Id, req.BankRef);
            if (!ok) return BadRequest(err);
            return Ok(updated);
        }

        [HttpPost("admin/redemption/reject")]
        public async Task<IActionResult> AdminRedemptionReject()
        {
            if (await AdminDeniedAsync() is { } denied) return denied;
            var body = await ReadBodyAsync();
            var req = System.Text.Json.JsonSerializer.Deserialize<RedemptionActionRequest>(body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.Id)) return BadRequest("id required");
            await _wallet.EnsureReadyAsync();
            var (ok, err, updated) = await _econ.RejectRedemptionAsync(req.Id, req.Note);
            if (!ok) return BadRequest(err);
            return Ok(updated);
        }

        [HttpPost("admin/slash")]
        public async Task<IActionResult> AdminSlash()
        {
            if (await AdminDeniedAsync() is { } denied) return denied;
            var body = await ReadBodyAsync();
            var req = System.Text.Json.JsonSerializer.Deserialize<SlashRequest>(body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.NodePublicKey) || req.Percent <= 0)
                return BadRequest("nodePublicKey and percent required");
            await _wallet.EnsureReadyAsync();
            var node = await _econ.FindNodeAsync(req.NodePublicKey);
            if (node == null) return NotFound("Not a registered node");
            var ev = await _econ.SlashAsync(node.Value.PublicKey, req.Percent, req.Reason, null, req.AffectedAccounts);
            return ev == null ? StatusCode(500, "Slash failed") : Ok(ev);
        }

        /// <summary>Run economics jobs now (challenges | egress | settle | bonds | all).</summary>
        [HttpPost("admin/run/{job}")]
        public async Task<IActionResult> AdminRun(string job)
        {
            if (await AdminDeniedAsync() is { } denied) return denied;
            await _wallet.EnsureReadyAsync();
            object? challenges = null, egress = null, settled = null, bonds = null;
            if (job is "challenges" or "all") { var (c, p) = await _econ.RunChallengesAsync(); challenges = new { challenged = c, passed = p }; }
            if (job is "egress" or "all") egress = await _econ.PullEgressReportsAsync();
            if (job is "settle" or "all") settled = await _econ.SettleDueAsync();
            if (job is "bonds" or "all") bonds = await _econ.ProcessBondWithdrawalsAsync();
            object? repaired = null;
            if (job is "repair" or "all") repaired = await _econ.RepairReplicasAsync();
            if (challenges == null && egress == null && settled == null && bonds == null && repaired == null)
                return BadRequest("job: challenges | egress | settle | bonds | repair | all");
            return Ok(new { challenges, egressItems = egress, monthsSettled = settled, bondWithdrawals = bonds, replicasAdded = repaired });
        }

        /// <summary>Automatic re-replication history (internal).</summary>
        [HttpGet("admin/replications")]
        public async Task<IActionResult> AdminReplications([FromQuery] string? fileId = null)
        {
            if (await AdminDeniedAsync() is { } denied) return denied;
            await _wallet.EnsureReadyAsync();
            return Ok(await _econ.ListReplicationEventsAsync(fileId));
        }

        public class TopUpRequest
        {
            public decimal FiatAmountVnd { get; set; }
            /// <summary>transfer (default) | card | iap</summary>
            public string? Channel { get; set; }
        }

        public class TransferRequest
        {
            public string ToPublicKey { get; set; } = "";
            public decimal Amount { get; set; }
            public string? Memo { get; set; }
        }

        public class CompleteTopUpRequest
        {
            public string OrderId { get; set; } = "";
        }

        [HttpGet("economics")]
        public async Task<IActionResult> Economics()
        {
            await _wallet.EnsureReadyAsync();
            return Ok(await _wallet.GetEconomicsAsync());
        }

        [HttpGet("balance")]
        public async Task<IActionResult> Balance()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, pk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);

            await _wallet.EnsureReadyAsync();
            var balance = await _wallet.GetBalanceAsync(pk!);
            return Ok(new { publicKey = pk, balance, unit = "D" });
        }

        [HttpGet("ledger")]
        public async Task<IActionResult> Ledger([FromQuery] int limit = 50)
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, pk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);

            await _wallet.EnsureReadyAsync();
            var entries = await _wallet.GetLedgerAsync(pk!, limit);
            return Ok(entries);
        }

        [HttpPost("transfer")]
        public async Task<IActionResult> Transfer()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, pk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);

            var req = System.Text.Json.JsonSerializer.Deserialize<TransferRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.ToPublicKey) || req.Amount <= 0)
                return BadRequest("toPublicKey and positive amount required");
            // §13: open P2P transfer can be switched off (tips and subscriptions keep working) if counsel advises.
            if (string.Equals(_config["Economics:P2PTransfer:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
                return StatusCode(403, "Peer-to-peer transfer is disabled; use tips or subscriptions");

            await _wallet.EnsureReadyAsync();
            var refId = Guid.NewGuid().ToString("N");
            var result = await _wallet.TransferAsync(pk!, req.ToPublicKey, req.Amount, req.Memo, refId);
            if (!result.Ok) return BadRequest(result.Error);

            var network = await MirrorValueTxAsync("Transfer", refId, pk!, req.ToPublicKey, req.Amount, body);
            return Ok(new
            {
                from = pk,
                to = req.ToPublicKey,
                amount = req.Amount,
                balance = await _wallet.GetBalanceAsync(pk!),
                network
            });
        }

        /// <summary>Create a pending fiat top-up quote/order (user signed).</summary>
        [HttpPost("topup")]
        public async Task<IActionResult> CreateTopUp()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, pk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);

            var req = System.Text.Json.JsonSerializer.Deserialize<TopUpRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || req.FiatAmountVnd <= 0)
                return BadRequest("fiatAmountVnd required");

            var channel = (req.Channel ?? "transfer").Trim().ToLowerInvariant();
            if (channel == "paypal" && PayPalMinVnd() is { } minVnd && req.FiatAmountVnd < minVnd)
                return BadRequest($"PayPal minimum is {PayPalMinUsd():0} USD ({minVnd:#,0} VND) / Nạp qua PayPal tối thiểu {PayPalMinUsd():0} USD ({minVnd:#,0} VND)");
            if (channel == "card" && !string.Equals(_config["Economics:TopUp:CardEnabled"], "true", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Card top-up is not available yet; use bank transfer");

            await _wallet.EnsureReadyAsync();
            var (created, createErr, order) = await _wallet.CreateTopUpOrderAsync(pk!, req.FiatAmountVnd, channel);
            if (!created || order == null) return BadRequest(createErr);

            var bank = TopUpBank();
            var transferContent = TransferContent(order);
            if (channel == "paypal")
            {
                var pp = PayPalPayment(order);
                if (pp == null) return BadRequest("PayPal top-up is not configured");
                return Ok(new
                {
                    order,
                    paypal = pp,
                    transferContent,
                    instructions = $"Gửi {pp.AmountUsd:0.00} USD qua {pp.Url} và ghi {transferContent} trong phần ghi chú. " +
                                   "D được cộng sau khi khoản thanh toán được duyệt."
                });
            }
            return Ok(new
            {
                order,
                bank,
                transferContent,
                instructions = bank == null
                    ? "Transfer the fiat amount to the Deltanium operating account, then contact support with order.id."
                    : $"Chuyển {order.FiatAmountVnd:0} VND tới {bank.BankName} {bank.AccountNo} ({bank.AccountName}) với nội dung {transferContent}. " +
                      "D được cộng vào ví sau khi tiền về và được duyệt."
            });
        }

        /// <summary>The signed-in user's own top-up orders, newest first.</summary>
        [HttpGet("topups")]
        public async Task<IActionResult> MyTopUps()
        {
            var a = await AuthAsync(requireRegisteredUser: false);
            if (!a.Ok) return a.Fail!;
            await _wallet.EnsureReadyAsync();
            var mine = (await _wallet.ListTopUpOrdersAsync("all", 1000))
                .Where(o => o.BuyerPublicKey == a.Pk)
                .Take(20)
                .Select(o => new
                {
                    o.Id, o.FiatAmountVnd, o.FeePercent, o.DCredited, o.Channel, o.Status, o.CreatedAt, o.CompletedAt,
                    transferContent = TransferContent(o)
                });
            return Ok(new { bank = TopUpBank(), orders = mine });
        }

        /// <summary>
        /// Smallest PayPal top-up (Economics:TopUp:PayPal:MinUsd, default 20): PayPal's fixed per-transaction fee
        /// makes small international payments cost more than the channel fee.
        /// </summary>
        private decimal PayPalMinUsd() =>
            decimal.TryParse(_config["Economics:TopUp:PayPal:MinUsd"], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0 ? m : 20m;

        private decimal? PayPalMinVnd()
        {
            if (!decimal.TryParse(_config["Economics:TopUp:PayPal:VndPerUsd"], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate <= 0) return null;
            return PayPalMinUsd() * rate;
        }

        public record PayPalPaymentInfo(string Link, string Url, decimal AmountUsd, decimal VndPerUsd);

        /// <summary>
        /// PayPal.me payment for an order (Economics:TopUp:PayPal: Link, VndPerUsd). The USD amount is the
        /// order's VND amount at that rate, rounded up to the cent; ops approves after matching the note.
        /// </summary>
        private PayPalPaymentInfo? PayPalPayment(TopUpOrder o)
        {
            var s = _config.GetSection("Economics:TopUp:PayPal");
            var link = (s["Link"] ?? "").Trim().TrimEnd('/');
            if (link.Length == 0 || !decimal.TryParse(s["VndPerUsd"], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var rate) || rate <= 0)
                return null;
            var usd = Math.Ceiling(o.FiatAmountVnd / rate * 100m) / 100m;
            var url = $"{link}/{usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}USD";
            return new PayPalPaymentInfo(link, url, usd, rate);
        }

        public record TopUpBankInfo(string Bin, string BankName, string AccountNo, string AccountName);

        /// <summary>Receiving account for bank-transfer top-ups (Economics:TopUp:Bank); null when not configured.</summary>
        private TopUpBankInfo? TopUpBank()
        {
            var s = _config.GetSection("Economics:TopUp:Bank");
            var acct = s["AccountNo"];
            if (string.IsNullOrWhiteSpace(acct)) return null;
            return new TopUpBankInfo(s["Bin"] ?? "", s["BankName"] ?? "", acct, s["AccountName"] ?? "");
        }

        /// <summary>Short reference the buyer puts in the transfer content, so ops can match the bank statement.</summary>
        public static string TransferContent(TopUpOrder o) =>
            "DLT" + (o.Id.Length >= 10 ? o.Id[..10] : o.Id).ToUpperInvariant();

        /// <summary>
        /// Ops: complete top-up after fiat is received.
        /// Requires X-SuperAdmin-Signature over the deterministic TopUp SignPayload (not the HTTP path).
        /// Server adds Central signature, submits TopUp to blockers, then credits the wallet ledger.
        /// </summary>
        [HttpPost("topup/complete")]
        public async Task<IActionResult> CompleteTopUp()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var req = System.Text.Json.JsonSerializer.Deserialize<CompleteTopUpRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.OrderId))
                return BadRequest("orderId required");

            return await FinalizeTopUpAsync(req.OrderId.Trim());
        }

        /// <summary>Admin: list top-up orders (admin auth, see AdminDeniedAsync).</summary>
        [HttpGet("admin/topups")]
        public async Task<IActionResult> ListTopUps([FromQuery] string status = "pending", [FromQuery] int limit = 50)
        {
            if (await AdminDeniedAsync() is { } denied) return denied;

            await _wallet.EnsureReadyAsync();
            var orders = await _wallet.ListTopUpOrdersAsync(status, limit);
            var centralPk = _keys.GetCentralPublicKey();
            return Ok(new
            {
                centralPublicKey = centralPk,
                superAdminPublicKey = _keys.GetSuperAdminPublicKey(),
                orders = orders.Select(o => new
                {
                    o.Id,
                    o.BuyerPublicKey,
                    o.FiatAmountVnd,
                    o.FeePercent,
                    o.DCredited,
                    o.PlatformFiatVnd,
                    o.NetworkReserveFiatVnd,
                    o.Status,
                    transferContent = TransferContent(o),
                    o.Channel,
                    o.NetworkTxId,
                    o.CreatedAt,
                    o.CompletedAt,
                    signPayload = o.Status == "pending" ? TopUpNetworkTx.BuildSignPayload(o, centralPk) : null
                })
            });
        }

        /// <summary>
        /// Admin: Super Admin signature over the TopUp SignPayload → Central signs, submit to blockers,
        /// then credit ledger. Header: X-SuperAdmin-Signature.
        /// </summary>
        [HttpPost("admin/topup/approve")]
        public async Task<IActionResult> AdminApproveTopUp()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var req = System.Text.Json.JsonSerializer.Deserialize<CompleteTopUpRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.OrderId))
                return BadRequest("orderId required");

            return await FinalizeTopUpAsync(req.OrderId.Trim());
        }

        /// <summary>The Super Admin signature over the TopUp SignPayload is the authority (dual control with Central).</summary>
        private async Task<IActionResult> FinalizeTopUpAsync(string orderId)
        {
            if (!Request.Headers.TryGetValue("X-SuperAdmin-Signature", out var saHeader) ||
                string.IsNullOrWhiteSpace(saHeader.FirstOrDefault()))
                return Unauthorized("Missing X-SuperAdmin-Signature (sign the TopUp SignPayload)");

            await _wallet.EnsureReadyAsync();
            var order = await _wallet.GetTopUpOrderAsync(orderId);
            if (order == null) return NotFound("Order not found");
            if (order.Status == "completed")
                return Ok(new { order, note = "already completed" });
            if (order.Status != "pending")
                return BadRequest($"Order status is {order.Status}");

            var centralPk = _keys.GetCentralPublicKey();
            var signPayload = TopUpNetworkTx.BuildSignPayload(order, centralPk);
            var saSig = saHeader.First()!;

            if (!_keys.VerifySignature(_keys.GetSuperAdminPublicKey(), signPayload, saSig))
                return Unauthorized("Invalid Super Admin signature on TopUp SignPayload");

            var centralSig = _keys.SignDecision(signPayload);
            if (!_keys.VerifySignature(centralPk, signPayload, centralSig))
                return StatusCode(500, "Central sign/verify failed");

            var submitBody = TopUpNetworkTx.BuildSubmitBody(order, centralPk, centralSig, saSig, signPayload);
            var (netOk, netDetail) = await _blockerTx.SubmitAsync(submitBody);
            if (!netOk)
                return StatusCode(503, $"TopUp network submit failed: {netDetail}");

            var (ok, err, completed) = await _wallet.CompleteTopUpOrderAsync(orderId, order.Id);
            if (!ok) return BadRequest(err);

            return Ok(new
            {
                order = completed,
                network = new
                {
                    type = TopUpNetworkTx.TypeName,
                    txId = order.Id,
                    from = centralPk,
                    to = order.BuyerPublicKey,
                    signPayload,
                    submit = netDetail,
                    centralPublicKey = centralPk,
                    superAdminPublicKey = _keys.GetSuperAdminPublicKey()
                }
            });
        }

        /// <summary>
        /// Admin endpoints (payee, slash, jobs, lists) must not be reachable with the node-to-Central token,
        /// which every Store and Blocker operator holds. Accepted, in order:
        /// 1. X-Timestamp + X-SuperAdmin-Request-Signature: Super Admin signature over
        ///    METHOD + path+query + timestamp + sha256hex(body), the format of SignatureAuthHelper (one use each).
        /// 2. X-Admin-Token equal to InternalApi:AdminToken / ADMIN_API_TOKEN (never given to nodes), if configured.
        /// Returns null when authorized, otherwise the 401 to return.
        /// </summary>
        private async Task<IActionResult?> AdminDeniedAsync()
        {
            var body = await ReadBodyAsync();
            if (Request.Headers.TryGetValue("X-SuperAdmin-Request-Signature", out var saSig) &&
                Request.Headers.TryGetValue("X-Timestamp", out var tsHeader) &&
                long.TryParse(tsHeader.FirstOrDefault(), out var ts))
            {
                if ((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > SignatureAuthHelper.MaxClockSkew)
                    return Unauthorized("Admin timestamp expired");
                var message = SignatureAuthHelper.BuildSignedMessage(Request.Method, $"{Request.Path}{Request.QueryString}", tsHeader.First()!, body);
                var superPk = _keys.GetSuperAdminPublicKey();
                if (!_keys.VerifySignature(superPk, message, saSig.FirstOrDefault() ?? ""))
                    return Unauthorized("Invalid Super Admin request signature");
                if (!HttpMethods.IsGet(Request.Method) && !SignatureAuthHelper.TryConsumeSignature("superadmin:" + superPk, message))
                    return Unauthorized("Request already used (replayed signature)");
                return null;
            }

            var adminToken = _config["InternalApi:AdminToken"] is { Length: > 0 } t ? t : Environment.GetEnvironmentVariable("ADMIN_API_TOKEN");
            if (!string.IsNullOrWhiteSpace(adminToken) && Request.Headers.TryGetValue("X-Admin-Token", out var gotAdmin) &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(gotAdmin.FirstOrDefault() ?? ""), System.Text.Encoding.UTF8.GetBytes(adminToken)))
                return null;

            return Unauthorized("Admin authorization required (X-SuperAdmin-Request-Signature or X-Admin-Token)");
        }
    }
}

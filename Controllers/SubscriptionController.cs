using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeltaniumApi.Controllers
{
    /// <summary>
    /// Creator economy (TOKENOMICS_V2 §9): paid subscriptions gate "subscribers:YYYY-MM" PRE rekeys,
    /// and tips. Payments split 90% creator / 3% network fund / 7% protocol ops.
    /// </summary>
    [ApiController]
    public class SubscriptionController : ControllerBase
    {
        private readonly EconomicsService _econ;
        private readonly IWalletService _wallet;
        private readonly SignatureAuthHelper _auth;
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private readonly IBlockerTxSubmitter? _blockerTx;

        public SubscriptionController(EconomicsService econ, IWalletService wallet, SignatureAuthHelper auth, IBlockerTxSubmitter? blockerTx = null)
        {
            _econ = econ;
            _wallet = wallet;
            _auth = auth;
            _blockerTx = blockerTx;
        }

        public class PlanRequest { public decimal PricePerMonthD { get; set; } }
        public class SubscribeRequest { public string CreatorPublicKey { get; set; } = ""; public int Months { get; set; } = 1; }
        public class TipRequest { public string ToPublicKey { get; set; } = ""; public decimal Amount { get; set; } public string? PostId { get; set; } }

        private async Task<(bool Ok, string? Pk, string Body, IActionResult? Fail)> AuthAsync()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            var (ok, pk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return (false, null, body, Unauthorized(err));
            await _wallet.EnsureReadyAsync();
            return (true, pk, body, null);
        }

        /// <summary>Public: a creator's plan and the current epoch tag.</summary>
        [HttpGet("api/subscription/plan/{creator}")]
        public async Task<IActionResult> GetPlan(string creator)
        {
            await _wallet.EnsureReadyAsync();
            var plan = await _econ.GetPlanAsync(creator);
            var epoch = EconomicsService.Epoch(System.DateTime.UtcNow);
            return Ok(new { plan, epoch, policyTag = $"subscribers:{epoch}" });
        }

        /// <summary>Creator sets the monthly price in D (0 disables new subscriptions).</summary>
        [HttpPost("api/subscription/plan")]
        public async Task<IActionResult> SetPlan()
        {
            var a = await AuthAsync();
            if (!a.Ok) return a.Fail!;
            var req = JsonSerializer.Deserialize<PlanRequest>(a.Body, JsonOpts);
            if (req == null || req.PricePerMonthD < 0) return BadRequest("pricePerMonthD >= 0 required");
            return Ok(await _econ.SetPlanAsync(a.Pk!, req.PricePerMonthD));
        }

        [HttpPost("api/subscription/subscribe")]
        public async Task<IActionResult> Subscribe()
        {
            var a = await AuthAsync();
            if (!a.Ok) return a.Fail!;
            var req = JsonSerializer.Deserialize<SubscribeRequest>(a.Body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.CreatorPublicKey)) return BadRequest("creatorPublicKey required");
            var (ok, status, err, sub) = await _econ.SubscribeAsync(a.Pk!, req.CreatorPublicKey, req.Months);
            if (!ok) return StatusCode(status, err);
            return Ok(new { subscription = sub, balance = await _wallet.GetBalanceAsync(a.Pk!) });
        }

        /// <summary>Signed: is the caller subscribed to this creator for the given (default current) epoch?</summary>
        [HttpGet("api/subscription/status/{creator}")]
        public async Task<IActionResult> Status(string creator, [FromQuery] string? epoch = null)
        {
            var a = await AuthAsync();
            if (!a.Ok) return a.Fail!;
            epoch ??= EconomicsService.Epoch(System.DateTime.UtcNow);
            return Ok(new { creator, epoch, subscribed = await _econ.IsSubscribedAsync(a.Pk!, creator, epoch) });
        }

        /// <summary>Creator-only: subscribers paid through the epoch, to generate "subscribers:YYYY-MM" rekeys.</summary>
        [HttpGet("api/subscription/subscribers")]
        public async Task<IActionResult> Subscribers([FromQuery] string? epoch = null)
        {
            var a = await AuthAsync();
            if (!a.Ok) return a.Fail!;
            epoch ??= EconomicsService.Epoch(System.DateTime.UtcNow);
            var subs = await _econ.ListSubscribersAsync(a.Pk!, epoch);
            return Ok(new
            {
                epoch,
                policyTag = $"subscribers:{epoch}",
                subscribers = subs.Select(s => new { publicKey = s.SubscriberPublicKey, s.PaidThroughEpoch })
            });
        }

        [HttpPost("api/tip")]
        public async Task<IActionResult> Tip()
        {
            var a = await AuthAsync();
            if (!a.Ok) return a.Fail!;
            var req = JsonSerializer.Deserialize<TipRequest>(a.Body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.ToPublicKey) || req.Amount <= 0)
                return BadRequest("toPublicKey and positive amount required");
            var ledgerRef = "tip-" + System.Guid.NewGuid().ToString("N");
            var (ok, status, err) = await _econ.TipAsync(a.Pk!, req.ToPublicKey, req.Amount, req.PostId, ledgerRef);
            if (!ok) return StatusCode(status, err);

            // §11 phase B: mirror the tip on chain as a Donate carrying the user's signed request.
            object? network = null;
            if (_blockerTx != null)
            {
                try
                {
                    var ledgerAt = (await _wallet.GetLedgerAsync(a.Pk!, 20)).Where(e => e.RefId == ledgerRef)
                        .Select(e => e.CreatedAt).DefaultIfEmpty(System.DateTime.UtcNow).Max();
                    var tx = ValueNetworkTx.BuildSubmitBody("Donate", ledgerRef, a.Pk!, req.ToPublicKey, req.Amount, ledgerAt,
                        Request.Method, $"{Request.Path}{Request.QueryString}",
                        Request.Headers["X-Timestamp"].ToString(), a.Body, Request.Headers["X-Signature"].ToString());
                    var (sent, detail) = await _blockerTx.SubmitAsync(tx);
                    network = new { submitted = sent, txId = ledgerRef, detail };
                }
                catch (System.Exception ex) { network = new { submitted = false, txId = ledgerRef, detail = ex.Message }; }
            }
            return Ok(new { to = req.ToPublicKey, amount = req.Amount, balance = await _wallet.GetBalanceAsync(a.Pk!), network });
        }
    }
}

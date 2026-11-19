using System;
using System.Threading.Tasks;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeltaniumApi.Controllers
{
    /// <summary>Public chain explorer — aggregates confirmed txs from active blockers.</summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ExplorerController : ControllerBase
    {
        private readonly IChainExplorerClient _explorer;
        private readonly INodeHealthService _nodes;
        private readonly KeyService _keys;

        public ExplorerController(IChainExplorerClient explorer, INodeHealthService nodes, KeyService keys)
        {
            _explorer = explorer;
            _nodes = nodes;
            _keys = keys;
        }

        [HttpGet("status")]
        public async Task<IActionResult> Status() => Ok(await _explorer.GetStatusAsync());

        /// <summary>Active Blocker + Store nodes with live reachability, version and chain height (cached 15 s).</summary>
        [HttpGet("nodes")]
        public async Task<IActionResult> Nodes() => Ok(await _nodes.GetReportAsync(HttpContext.RequestAborted));

        /// <summary>
        /// Public keys anyone can use to verify the network: the Central API key (signs decisions, Store
        /// tokens and version attestations) and the Super Admin key (dual control on top-ups, redemptions
        /// and slashing). Public keys only; empty when not configured.
        /// </summary>
        [HttpGet("keys")]
        public IActionResult Keys() => Ok(new
        {
            network = NetworkScope.Name,
            central = new
            {
                endpoint = $"{Request.Scheme}://{Request.Host}",
                publicKey = _keys.GetCentralPublicKey(),
                version = BuildInfo.Version,
                commit = BuildInfo.Commit,
            },
            superAdmin = new { publicKey = SafeSuperAdminKey() },
        });

        private string SafeSuperAdminKey()
        {
            try { return _keys.GetSuperAdminPublicKey() ?? ""; }
            catch { return ""; }
        }

        [HttpGet("txs")]
        public async Task<IActionResult> SearchTxs(
            [FromQuery] string? q = null,
            [FromQuery] string? type = null,
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0,
            [FromQuery] bool includePending = false)
        {
            var json = await _explorer.SearchTxsJsonAsync(q, type, limit, offset, includePending);
            return Content(json, "application/json");
        }

        [HttpGet("txs/{txId}")]
        public async Task<IActionResult> GetTx(string txId)
        {
            var tx = await _explorer.GetTxAsync(txId);
            if (tx == null) return NotFound(new { error = "Transaction not found" });
            return Content(tx.Value.GetRawText(), "application/json");
        }

        [HttpGet("blocks")]
        public async Task<IActionResult> ListBlocks(
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0)
        {
            var json = await _explorer.ListBlocksJsonAsync(limit, offset);
            if (json == null)
                return Ok(new { height = -1, total = 0, offset, limit, items = Array.Empty<object>() });
            return Content(json, "application/json");
        }
    }
}

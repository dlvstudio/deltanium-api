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

        public ExplorerController(IChainExplorerClient explorer, INodeHealthService nodes)
        {
            _explorer = explorer;
            _nodes = nodes;
        }

        [HttpGet("status")]
        public async Task<IActionResult> Status() => Ok(await _explorer.GetStatusAsync());

        /// <summary>Active Blocker + Store nodes with live reachability, version and chain height (cached 15 s).</summary>
        [HttpGet("nodes")]
        public async Task<IActionResult> Nodes() => Ok(await _nodes.GetReportAsync(HttpContext.RequestAborted));

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

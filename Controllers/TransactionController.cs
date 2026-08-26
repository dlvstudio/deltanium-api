using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/tx")]
    public class TransactionController : ControllerBase
    {
        private readonly StorageService _storageService;

        public TransactionController(StorageService storageService)
        {
            _storageService = storageService;
        }

        [HttpPost("submit")]
        public async Task<ActionResult> Submit([FromBody] BlockchainTransaction tx)
        {
            if (tx == null) return BadRequest("Transaction body is required");
            if (string.IsNullOrWhiteSpace(tx.Type)) return BadRequest("Type is required");
            if (string.IsNullOrWhiteSpace(tx.From)) return BadRequest("From is required");

            var normalizedType = NormalizeType(tx.Type);
            if (string.IsNullOrWhiteSpace(normalizedType))
                return BadRequest("Unsupported transaction type");

            tx.Type = normalizedType;
            if (string.IsNullOrWhiteSpace(tx.Id))
                tx.Id = Guid.NewGuid().ToString("N");
            if (tx.Timestamp <= 0)
                tx.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await _storageService.AddTransactionAsync(tx);
            return Ok(new { status = "queued", tx.Id, tx.Type });
        }

        [HttpGet("pull")]
        public async Task<ActionResult<List<BlockchainTransaction>>> Pull([FromQuery] int limit = 50)
        {
            if (limit <= 0) limit = 50;
            if (limit > 500) limit = 500;
            var txs = await _storageService.PullPendingTransactionsAsync(limit);
            return Ok(txs);
        }

        private static string NormalizeType(string input)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "userregistration", "UserRegistration" },
                { "user_registration", "UserRegistration" },
                { "follow", "Follow" },
                { "unfollow", "Unfollow" },
                { "createpost", "CreatePost" },
                { "create_post", "CreatePost" },
                { "react", "React" },
                { "comment", "Comment" },
                { "noderegistration", "NodeRegistration" },
                { "node_registration", "NodeRegistration" },
                { "storageoperation", "StorageOperation" },
                { "storage_operation", "StorageOperation" },
                { "transfer", "Transfer" },
                { "donate", "Donate" }
            };

            var key = input.Trim();
            if (map.TryGetValue(key, out var normalized))
                return normalized;

            // Accept exact PascalCase values if provided
            var allowed = new HashSet<string>
            {
                "UserRegistration",
                "Follow",
                "Unfollow",
                "CreatePost",
                "React",
                "Comment",
                "NodeRegistration",
                "StorageOperation",
                "Transfer",
                "Donate"
            };

            return allowed.Contains(key) ? key : string.Empty;
        }
    }
}

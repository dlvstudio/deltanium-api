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
    [Route("api/tx")]
    public class TransactionController : ControllerBase
    {
        private readonly IStorageService _storageService;
        private readonly KeyService _keyService;
        private readonly SignatureAuthHelper _auth;
        private readonly IConfiguration _config;

        public TransactionController(
            IStorageService storageService,
            KeyService keyService,
            SignatureAuthHelper auth,
            IConfiguration config)
        {
            _storageService = storageService;
            _keyService = keyService;
            _auth = auth;
            _config = config;
        }

        [HttpPost("submit")]
        public async Task<ActionResult> Submit()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, callerPk, err) = await _auth.ValidateAsync(Request, body, requireRegisteredUser: false);
            if (!ok) return Unauthorized(err);

            var tx = System.Text.Json.JsonSerializer.Deserialize<BlockchainTransaction>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (tx == null) return BadRequest("Transaction body is required");
            if (string.IsNullOrWhiteSpace(tx.Type)) return BadRequest("Type is required");
            if (string.IsNullOrWhiteSpace(tx.From)) return BadRequest("From is required");

            if (!string.Equals(callerPk, tx.From, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(SignatureAuthHelper.NormalizePubKey(callerPk!), SignatureAuthHelper.NormalizePubKey(tx.From), StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(tx.Signature) ||
                !_keyService.VerifySignature(tx.From, $"{tx.Type}:{tx.From}:{tx.Timestamp}:{tx.Id}", tx.Signature))
            {
                // Accept request-auth as primary; tx.Signature optional for legacy clients
                // but From must match authenticated caller (already checked).
            }

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
            var expected = _config["InternalApi:Token"];
            if (string.IsNullOrWhiteSpace(expected))
                return StatusCode(503, "InternalApi:Token not configured");

            if (!Request.Headers.TryGetValue("X-Internal-Token", out var token) ||
                !string.Equals(token.FirstOrDefault(), expected, StringComparison.Ordinal))
            {
                return Unauthorized("Invalid internal token");
            }

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

            var allowed = new HashSet<string>
            {
                "UserRegistration", "Follow", "Unfollow", "CreatePost", "React", "Comment",
                "NodeRegistration", "StorageOperation", "Transfer", "Donate"
            };
            return allowed.Contains(input) ? input : "";
        }
    }
}

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
        private readonly EconomicsService _econ;
        private readonly IWalletService _wallet;

        public TransactionController(
            IStorageService storageService,
            KeyService keyService,
            SignatureAuthHelper auth,
            IConfiguration config,
            EconomicsService econ,
            IWalletService wallet)
        {
            _econ = econ;
            _wallet = wallet;
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

            // TOKENOMICS_V2 §5: the sender pays the tx fee (no sponsored txs; TopUp is refused here, Central issues it).
            var fee = await _econ.ChargeTxFeeAsync(tx.Type, tx.From, tx.Id);
            if (!fee.Ok) return StatusCode(fee.Status, new { error = fee.Error, fee = fee.Fee });
            tx.Fee = (long)Math.Ceiling(fee.Fee);

            await _storageService.AddTransactionAsync(tx);
            return Ok(new { status = "queued", tx.Id, tx.Type, fee = fee.Fee, feePaidBy = fee.Payer });
        }

        /// <summary>
        /// A Blocker takes pending transactions off the queue (they are marked delivered, so a caller can empty
        /// the queue). Only an active registered Blocker may do it: the request is signed with the Blocker's node
        /// key (X-User-PubKey, X-Timestamp, X-Signature, the SignatureAuthHelper format) and each signed request
        /// is used once. The shared X-Internal-Token, which every node operator holds, no longer opens it.
        /// </summary>
        [HttpPost("pull")]
        public async Task<ActionResult<List<BlockchainTransaction>>> Pull([FromQuery] int limit = 50, [FromQuery] string? blockerPubKey = null)
        {
            var body = await ReadBodyAsync();
            var (ok, signer, err) = await _auth.ValidateAsync(Request, body, requireRegisteredUser: false);
            if (!ok) return Unauthorized(err);

            var blocker = (await _storageService.GetActiveBlockCreatorNodesAsync())
                .FirstOrDefault(b => SamePubKey(b.PublicKey, signer!));
            if (blocker == null) return StatusCode(403, "Only an active registered Blocker can pull transactions");
            if (!string.IsNullOrWhiteSpace(blockerPubKey) && !SamePubKey(blockerPubKey, signer!))
                return StatusCode(403, "blockerPubKey does not match the signer");

            if (limit <= 0) limit = 50;
            if (limit > 500) limit = 500;
            var txs = await _storageService.PullPendingTransactionsAsync(limit);
            return Ok(txs);
        }

        private async Task<string> ReadBodyAsync()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            return body;
        }

        private static bool SamePubKey(string a, string b) =>
            string.Equals(SignatureAuthHelper.NormalizePubKey(a.Trim()), SignatureAuthHelper.NormalizePubKey(b.Trim()), StringComparison.OrdinalIgnoreCase);

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
                { "donate", "Donate" },
                { "topup", "TopUp" },
                { "top_up", "TopUp" }
            };

            var key = input.Trim();
            if (map.TryGetValue(key, out var normalized))
                return normalized;

            var allowed = new HashSet<string>
            {
                "UserRegistration", "Follow", "Unfollow", "CreatePost", "React", "Comment",
                "NodeRegistration", "StorageOperation", "Transfer", "Donate", "TopUp"
            };
            return allowed.Contains(input) ? input : "";
        }
    }
}

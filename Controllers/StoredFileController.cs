using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StoredFileController : ControllerBase
    {
        private readonly IStorageService _storageService;
        private readonly KeyService _keyService;
        private readonly SignatureAuthHelper _auth;
        private readonly IWalletService _wallet;
        private readonly EconomicsService _econ;

        public class NodeFilesResponse
        {
            public StoreNode Node { get; set; }
            public List<StoredFile> Files { get; set; }
        }

        public class RegisterReplicaRequest
        {
            public string FileId { get; set; } = "";
            public string OwnerPublicKey { get; set; } = "";
            public List<string> StoreNodeIds { get; set; } = new();
            public string FileHash { get; set; } = "";
            public long FileSize { get; set; }
            /// <summary>Storage term in months (default Economics:Storage:DefaultMonths).</summary>
            public int Months { get; set; }
            /// <summary>TimeFixed (default) | AutoRenew (OpenEnded store contracts map here).</summary>
            public string? ContractType { get; set; }
            /// <summary>SHA-256 of every uploaded block, used for proof-of-storage challenges.</summary>
            public List<FileBlockHash>? Blocks { get; set; }
            /// <summary>"social" for reactions, comments and chat messages (a label only: billed like any other item).</summary>
            public string? Kind { get; set; }
            /// <summary>
            /// Copies to keep (paid for). May exceed the Stores uploaded to: Central copies the file to more Stores
            /// until this many hold it. 0 = the number of <c>StoreNodeIds</c>.
            /// </summary>
            public int Replicas { get; set; }
        }

        public StoredFileController(
            IStorageService storageService,
            KeyService keyService,
            SignatureAuthHelper auth,
            IWalletService wallet,
            EconomicsService econ)
        {
            _econ = econ;
            _storageService = storageService;
            _keyService = keyService;
            _auth = auth;
            _wallet = wallet;
        }

        [HttpGet("nodes/{publicKey}")]
        public async Task<ActionResult<List<StoreNode>>> GetNodesStoringUserFiles(string publicKey)
        {
            if (!_keyService.ValidatePublicKey(publicKey))
                return BadRequest("Invalid public key format");

            return await _storageService.GetNodesStoringUserFilesAsync(publicKey);
        }

        [HttpGet("details/{publicKey}")]
        public async Task<ActionResult<List<NodeFilesResponse>>> GetDetailedNodesWithFiles(string publicKey)
        {
            if (!_keyService.ValidatePublicKey(publicKey))
                return BadRequest("Invalid public key format");

            var nodes = await _storageService.GetNodesStoringUserFilesAsync(publicKey);
            var result = new List<NodeFilesResponse>();
            foreach (var node in nodes)
            {
                var files = await _storageService.GetUserFilesOnNodeAsync(publicKey, node.Id);
                result.Add(new NodeFilesResponse { Node = node, Files = files });
            }
            return result;
        }

        /// <summary>Register a single placement (legacy). Requires signed request from owner.</summary>
        [HttpPost("register")]
        public async Task<ActionResult<StoredFile>> RegisterStoredFile()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, callerPk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);

            var file = System.Text.Json.JsonSerializer.Deserialize<StoredFile>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file == null) return BadRequest("Invalid body");

            if (!_keyService.ValidatePublicKey(file.OwnerPublicKey))
                return BadRequest("Invalid owner public key");

            if (!PubKeysMatch(callerPk!, file.OwnerPublicKey))
                return Forbid();

            var nodes = await _storageService.GetNodesAsync();
            if (!nodes.Exists(n => n.Id == file.StoreNodeId))
                return BadRequest("Invalid store node ID");

            // v2: storage is paid through the escrow opened by register-replicas.

            await _storageService.SaveStoredFileAsync(file);
            return file;
        }

        /// <summary>
        /// N-of-M: register the same fileId on multiple store nodes (one row per replica).
        /// </summary>
        [HttpPost("register-replicas")]
        public async Task<ActionResult> RegisterReplicas()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, callerPk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);

            var req = System.Text.Json.JsonSerializer.Deserialize<RegisterReplicaRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.FileId) || req.StoreNodeIds == null || req.StoreNodeIds.Count == 0)
                return BadRequest("fileId and storeNodeIds required");

            if (!_keyService.ValidatePublicKey(req.OwnerPublicKey))
                return BadRequest("Invalid owner public key");

            if (!PubKeysMatch(callerPk!, req.OwnerPublicKey))
                return Forbid();

            var nodes = await _storageService.GetNodesAsync();
            var validIds = req.StoreNodeIds.Distinct().Where(id => nodes.Exists(n => n.Id == id)).ToList();
            if (validIds.Count == 0)
                return BadRequest("No valid store node IDs");
            // Web readers can only reach Stores over HTTPS: keep at least one replica on such a Store
            // whenever the network has one (TCP-only Stores are fine for the other replicas).
            static bool HasHttps(StoreNode n) => !string.IsNullOrWhiteSpace(n.Endpoint);
            if (nodes.Exists(n => n.IsActive && HasHttps(n)) &&
                !validIds.Any(id => nodes.Exists(n => n.Id == id && HasHttps(n))))
                return BadRequest("At least one replica must be on a Store with an HTTPS endpoint");

            var files = validIds.Select(id => new StoredFile
            {
                FileId = req.FileId,
                OwnerPublicKey = req.OwnerPublicKey,
                StoreNodeId = id,
                FileHash = req.FileHash ?? "",
                FileSize = req.FileSize,
                IsActive = true,
                StoredAt = DateTime.UtcNow
            }).ToList();

            // v2 §6/§10: fund the storage escrow (free tier → network fund) before indexing placements.
            await _wallet.EnsureReadyAsync();
            var (paid, status, payErr, escrow, quote) = await _econ.OpenStorageEscrowAsync(
                callerPk!, req.FileId, req.FileSize, validIds, req.Months, req.ContractType, req.Blocks,
                social: string.Equals(req.Kind, "social", StringComparison.OrdinalIgnoreCase), replicas: req.Replicas);
            if (!paid) return StatusCode(status, new { error = payErr, quote });

            await _storageService.SaveStoredFilesBatchAsync(files);
            return Ok(new
            {
                fileId = req.FileId,
                replicas = validIds.Count,
                storeNodeIds = validIds,
                storage = new
                {
                    escrow!.ContractType,
                    escrow.Months,
                    escrow.MonthlyFee,
                    escrow.TotalFunded,
                    escrow.FreeTier,
                    escrow.Social,
                    funded = escrow.TotalFunded > 0,
                    proofBlocks = escrow.Blocks.Count
                }
            });
        }

        public class CancelEscrowRequest
        {
            public string FileId { get; set; } = "";
        }

        /// <summary>
        /// The upload failed after register-replicas paid for it: close the escrow and refund the payer.
        /// Owner-signed; refused once a Store holds the whole file or after the cancel window.
        /// </summary>
        public class StopRenewalRequest { public List<string> FileIds { get; set; } = new(); }

        /// <summary>The owner deleted these items: stop renewing their storage (owner-signed). Paid months run out.</summary>
        [HttpPost("stop-renewal")]
        public async Task<ActionResult> StopRenewal()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, callerPk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);
            var req = System.Text.Json.JsonSerializer.Deserialize<StopRenewalRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || req.FileIds.Count == 0) return BadRequest("fileIds required");

            await _wallet.EnsureReadyAsync();
            var stopped = await _econ.StopRenewalAsync(callerPk!, req.FileIds);
            return Ok(new { stopped });
        }

        /// <summary>What the caller's wallet must cover to keep their stored items (owner-signed GET).</summary>
        [HttpGet("renewals")]
        public async Task<ActionResult> Renewals()
        {
            var (ok, callerPk, err) = await _auth.ValidateAsync(Request, "");
            if (!ok) return Unauthorized(err);
            await _wallet.EnsureReadyAsync();
            return Ok(await _econ.GetRenewalStatusAsync(callerPk!));
        }

        private async Task<(StoreNode? Node, ActionResult? Error)> CallingNodeAsync(string body)
        {
            var (ok, callerPk, err) = await _auth.ValidateAsync(Request, body, requireRegisteredUser: false);
            if (!ok) return (null, Unauthorized(err));
            var node = (await _storageService.GetNodesAsync()).FirstOrDefault(n => PubKeysMatch(n.PublicKey ?? "", callerPk!));
            return node == null ? (null, StatusCode(403, "Not a registered Store")) : (node, null);
        }

        /// <summary>
        /// Store-signed: items this Store holds whose contract ended (Expired or Closed) long enough ago to delete,
        /// with their block ids. Stores poll this and delete the data themselves.
        /// </summary>
        [HttpPost("ended-for-node")]
        public async Task<ActionResult> EndedForNode()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            var (node, error) = await CallingNodeAsync(body);
            if (node == null) return error!;
            await _wallet.EnsureReadyAsync();
            var ended = await _econ.EndedForNodeAsync(node.Id);
            return Ok(new
            {
                deleteAfterDays = _econ.StoreDeleteAfterDays,
                items = ended.Select(e => new { fileId = e.Id, e.Status, e.EndedAt, blockIds = e.Blocks.Select(b => b.BlockId).ToList() })
            });
        }

        public class ChildrenEndedRequest
        {
            public string ParentFileId { get; set; } = "";
            public List<string> FileIds { get; set; } = new();
        }

        /// <summary>Store-signed: the Store deleted an ended post; stop renewing the reactions and comments on it.</summary>
        [HttpPost("children-ended")]
        public async Task<ActionResult> ChildrenEnded()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            var (node, error) = await CallingNodeAsync(body);
            if (node == null) return error!;
            var req = System.Text.Json.JsonSerializer.Deserialize<ChildrenEndedRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.ParentFileId)) return BadRequest("parentFileId required");
            await _wallet.EnsureReadyAsync();
            return Ok(new { stopped = await _econ.StopRenewalOfChildrenAsync(node.Id, req.ParentFileId, req.FileIds) });
        }

        [HttpPost("cancel-escrow")]
        public async Task<ActionResult> CancelEscrow()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;

            var (ok, callerPk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);

            var req = System.Text.Json.JsonSerializer.Deserialize<CancelEscrowRequest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrWhiteSpace(req.FileId)) return BadRequest("fileId required");

            await _wallet.EnsureReadyAsync();
            var (done, status, error, refunded) = await _econ.CancelFailedUploadAsync(callerPk!, req.FileId);
            if (!done) return StatusCode(status, new { error });
            await _storageService.DeactivateStoredFileAsync(req.FileId);
            return Ok(new { fileId = req.FileId, cancelled = true, refunded });
        }

        /// <summary>
        /// Price an item before upload (§6) with the same rules as register-replicas: pass the same fileSize,
        /// the number of Stores it goes to (storeNodes), replicas, months and kind=social. <c>charge</c> is exactly
        /// what registering it will take from the owner's wallet. There is no free tier: owner is optional.
        /// </summary>
        [HttpGet("quote")]
        public async Task<ActionResult> Quote([FromQuery] long sizeBytes, [FromQuery] int replicas = 2,
            [FromQuery] int months = 0, [FromQuery] string? owner = null, [FromQuery] int storeNodes = 0,
            [FromQuery] string? kind = null)
        {
            if (sizeBytes <= 0) return BadRequest("sizeBytes required");
            await _wallet.EnsureReadyAsync();
            var c = await _econ.QuoteChargeAsync(owner, sizeBytes, Math.Max(0, storeNodes), replicas, months,
                string.Equals(kind, "social", StringComparison.OrdinalIgnoreCase));
            // monthly: what an AutoRenew contract then takes each month (0 when the owner does not pay).
            var monthly = c.OwnerPays > 0 ? c.Quote.RenewalFee : 0m;
            return Ok(new { quote = c.Quote, charge = c.OwnerPays, monthly, replicas = c.Replicas, social = c.Social, enforceFees = _wallet.EnforceFees });
        }

        /// <summary>Storage contract status for a file (Active | Grace | Expired | Lost | Closed).</summary>
        [HttpGet("contract/{fileId}")]
        public async Task<ActionResult> Contract(string fileId)
        {
            await _wallet.EnsureReadyAsync();
            var e = await _econ.GetEscrowAsync(fileId);
            if (e == null) return NotFound();
            return Ok(new
            {
                e.Id,
                e.OwnerPublicKey,
                e.ContractType,
                e.Status,
                e.Months,
                e.MonthsSettled,
                e.MonthlyFee,
                e.TotalFunded,
                e.Released,
                e.FreeTier,
                e.StartAt,
                e.GraceUntil,
                e.EndedAt,
                e.RenewalStopped,
                e.RenewalFee,
                e.StoreNodeIds,
                e.FailedNodeIds,
                e.Social,
                proofBlocks = e.Blocks.Count,
                e.EgressCapped,
                escrowBalance = await _wallet.GetBalanceAsync(EconAccounts.Escrow(e.Id))
            });
        }

        private static bool PubKeysMatch(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                return string.Equals(
                    SignatureAuthHelper.NormalizePubKey(a),
                    SignatureAuthHelper.NormalizePubKey(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}

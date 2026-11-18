using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BlockCreatorNodeController : ControllerBase
    {
        private readonly KeyService _keyService;
        private readonly IStorageService _storageService;

        public class NodeRegistrationRequest
        {
            public string Endpoint { get; set; }
            public string Voucher { get; set; }
            public string PublicKey { get; set; }
        }

        public class NodeRegistrationResponse
        {
            public string Id { get; set; }
            public string PublicKey { get; set; }
            public string Endpoint { get; set; }
        }

        public class DecisionRequest
        {
            public string TxId { get; set; } = string.Empty;
            public string BlockerPubKey { get; set; } = string.Empty;
        }

        public class DecisionPayload
        {
            public string TxId { get; set; } = string.Empty;
            public string BlockerPubKey { get; set; } = string.Empty;
            public bool Selected { get; set; }
            public long IssuedAt { get; set; }
            public long ExpiresAt { get; set; }
            public string Nonce { get; set; } = string.Empty;
        }

        public class DecisionResponse
        {
            public DecisionPayload Decision { get; set; } = new DecisionPayload();
            public string Signature { get; set; } = string.Empty;
            public string CentralPublicKey { get; set; } = string.Empty;
            /// <summary>Central's signed statement of the API build that decided this tx; the Blocker stores it in the tx.</summary>
            public VersionAttestation? Version { get; set; }
        }

        /// <summary>Message format shared with the Blocker (Core/VersionAttestation.cs).</summary>
        public record VersionAttestation(string Service, string Version, string Commit, string PublicKey, string Signature)
        {
            public static string Message(string txId, string service, string version, string commit) =>
                $"DELTANIUM-TX-VERSION\n{txId}\n{service}\n{version}\n{commit}";
        }

        private readonly EconomicsService _econ;
        private readonly IWalletService _wallet;

        public BlockCreatorNodeController(KeyService keyService, IStorageService storageService,
            EconomicsService econ, IWalletService wallet)
        {
            _keyService = keyService;
            _storageService = storageService;
            _econ = econ;
            _wallet = wallet;
        }

        /// <summary>
        /// Canonical string Central signs for a block slot. Anchor fields are appended only when present
        /// so blockers running the pre-anchor format keep verifying.
        /// </summary>
        public static string BlockSlotSignString(BlockSlotPayload p)
        {
            var s = $"BLOCK_SLOT|{p.BlockerPubKey}|{p.IssuedAt}|{p.ExpiresAt}|{p.Nonce}|{p.Granted}";
            if (!string.IsNullOrEmpty(p.LedgerRoot))
                s += $"|{p.LedgerAnchorSeq}|{p.LedgerRoot}|{p.BalancesRoot}";
            return s;
        }

        [HttpPost("register")]
        public async Task<ActionResult<NodeRegistrationResponse>> Register([FromBody] NodeRegistrationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return BadRequest("Endpoint is required");
            }
            if (string.IsNullOrWhiteSpace(request.Voucher))
            {
                return BadRequest("Voucher is required");
            }
            if (string.IsNullOrWhiteSpace(request.PublicKey))
            {
                return BadRequest("PublicKey is required");
            }

            // Validate public key format
            if (!_keyService.ValidatePublicKey(request.PublicKey))
            {
                return BadRequest("Invalid public key format");
            }

            // Check if node already registered by public key
            var existingNodes = await _storageService.GetBlockCreatorNodesAsync();
            if (existingNodes.Any(n => n.PublicKey == request.PublicKey))
            {
                return Conflict("Block Creator node already registered");
            }

            if (!await _storageService.TryMarkVoucherUsedAsync(request.Voucher))
            {
                return BadRequest("Invalid or already used voucher");
            }

            var node = new BlockCreatorNode
            {
                PublicKey = request.PublicKey,
                Endpoint = request.Endpoint
            };

            await _storageService.SaveBlockCreatorNodeAsync(node);

            return new NodeRegistrationResponse
            {
                Id = node.Id,
                PublicKey = node.PublicKey,
                Endpoint = node.Endpoint
            };
        }

        [HttpPost("decision")]
        public async Task<ActionResult<DecisionResponse>> GetDecision([FromBody] DecisionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TxId))
                return BadRequest("TxId is required");
            if (string.IsNullOrWhiteSpace(request.BlockerPubKey))
                return BadRequest("BlockerPubKey is required");

            var nodes = await _storageService.GetActiveBlockCreatorNodesAsync();
            if (nodes.Count == 0)
                return BadRequest("No active blockers");

            var selected = nodes[new Random().Next(nodes.Count)];
            var isSelected = string.Equals(selected.PublicKey, request.BlockerPubKey, StringComparison.OrdinalIgnoreCase);

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiresAt = issuedAt + 60;
            var payload = new DecisionPayload
            {
                TxId = request.TxId,
                BlockerPubKey = request.BlockerPubKey,
                Selected = isSelected,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
                Nonce = Guid.NewGuid().ToString("N")
            };

            var toSign = $"DECISION|{payload.TxId}|{payload.BlockerPubKey}|{payload.Selected}|{payload.IssuedAt}|{payload.ExpiresAt}|{payload.Nonce}";
            var signature = _keyService.SignDecision(toSign);

            var centralPublicKey = _keyService.GetCentralPublicKey();
            return new DecisionResponse
            {
                Decision = payload,
                Signature = signature,
                CentralPublicKey = centralPublicKey,
                Version = new VersionAttestation("api", BuildInfo.Version, BuildInfo.Commit, centralPublicKey,
                    _keyService.SignDecision(VersionAttestation.Message(payload.TxId, "api", BuildInfo.Version, BuildInfo.Commit)))
            };
        }

        [HttpGet("decision-key")]
        public ActionResult GetDecisionKey()
        {
            return Ok(new { publicKey = _keyService.GetCentralPublicKey() });
        }

        public class BlockSlotRequest
        {
            public string BlockerPubKey { get; set; } = string.Empty;
        }

        public class BlockSlotPayload
        {
            public string BlockerPubKey { get; set; } = string.Empty;
            public long IssuedAt { get; set; }
            public long ExpiresAt { get; set; }
            public string Nonce { get; set; } = string.Empty;
            public bool Granted { get; set; }
            /// <summary>TOKENOMICS_V2 §11: wallet ledger anchor to embed in the block (granted slots only).</summary>
            public long LedgerAnchorSeq { get; set; }
            public string LedgerRoot { get; set; } = string.Empty;
            public string BalancesRoot { get; set; } = string.Empty;
        }

        public class BlockSlotResponse
        {
            public BlockSlotPayload Payload { get; set; } = new BlockSlotPayload();
            public string Signature { get; set; } = string.Empty;
            public string CentralPublicKey { get; set; } = string.Empty;
        }

        /// <summary>Request authorization to create the next block. Central signs the slot grant; Blocker must verify the signature with trusted Central pubkey.</summary>
        [HttpPost("block-slot")]
        public async Task<ActionResult<BlockSlotResponse>> RequestBlockSlot([FromBody] BlockSlotRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.BlockerPubKey))
                return BadRequest("BlockerPubKey is required");

            var nodes = await _storageService.GetActiveBlockCreatorNodesAsync();
            var granted = false;
            await _wallet.EnsureReadyAsync();
            // v2 §8: weighted by (bond + 1) × uptime; nodes under the minimum bond are skipped when bonds are enforced.
            var selected = await _econ.PickBlockerAsync(nodes);
            if (selected != null)
                granted = string.Equals(selected.PublicKey, request.BlockerPubKey, StringComparison.OrdinalIgnoreCase);

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiresAt = issuedAt + 120;
            var nonce = Guid.NewGuid().ToString("N");
            var payload = new BlockSlotPayload
            {
                BlockerPubKey = request.BlockerPubKey,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
                Nonce = nonce,
                Granted = granted
            };
            if (granted)
            {
                // v2 §11 phase A: anchor the wallet ledger into this block; §5: pay pending tx fees to this blocker.
                var anchor = await _econ.CreateAnchorAsync(request.BlockerPubKey);
                payload.LedgerAnchorSeq = anchor.Seq;
                payload.LedgerRoot = anchor.LedgerRoot;
                payload.BalancesRoot = anchor.BalancesRoot;
                await _econ.PayoutBlockerPoolAsync(selected!.PublicKey);
            }
            var toSign = BlockSlotSignString(payload);
            var signature = _keyService.SignDecision(toSign);

            return new BlockSlotResponse
            {
                Payload = payload,
                Signature = signature,
                CentralPublicKey = _keyService.GetCentralPublicKey()
            };
        }

        [HttpGet("list")]
        public async Task<ActionResult<List<BlockCreatorNode>>> ListNodes()
        {
            return await _storageService.GetActiveBlockCreatorNodesAsync();
        }
    }
}












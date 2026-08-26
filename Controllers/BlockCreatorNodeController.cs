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
        private readonly StorageService _storageService;

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
        }

        public BlockCreatorNodeController(KeyService keyService, StorageService storageService)
        {
            _keyService = keyService;
            _storageService = storageService;
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

            // Check voucher validity
            if (!await _storageService.IsVoucherValidAsync(request.Voucher))
            {
                return BadRequest("Invalid or already used voucher");
            }

            // Check if node already registered by public key
            var existingNodes = await _storageService.GetBlockCreatorNodesAsync();
            if (existingNodes.Any(n => n.PublicKey == request.PublicKey))
            {
                return Conflict("Block Creator node already registered");
            }

            var node = new BlockCreatorNode
            {
                PublicKey = request.PublicKey,
                Endpoint = request.Endpoint
            };

            await _storageService.SaveBlockCreatorNodeAsync(node);
            await _storageService.MarkVoucherUsedAsync(request.Voucher);

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

            return new DecisionResponse
            {
                Decision = payload,
                Signature = signature,
                CentralPublicKey = _keyService.GetCentralPublicKey()
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
            if (nodes.Count > 0)
            {
                var selected = nodes[new Random().Next(nodes.Count)];
                granted = string.Equals(selected.PublicKey, request.BlockerPubKey, StringComparison.OrdinalIgnoreCase);
            }

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
            var toSign = $"BLOCK_SLOT|{payload.BlockerPubKey}|{payload.IssuedAt}|{payload.ExpiresAt}|{payload.Nonce}|{payload.Granted}";
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












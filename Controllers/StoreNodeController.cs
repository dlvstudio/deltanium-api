using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;
using System.Linq;

namespace DeltaniumApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StoreNodeController : ControllerBase
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

        private readonly EconomicsService _econ;
        private readonly IWalletService _wallet;
        private readonly SignatureAuthHelper? _auth;

        public class NodeRekeyRequest
        {
            public string NodeId { get; set; } = "";
            public string PublicKey { get; set; } = "";
        }

        public StoreNodeController(KeyService keyService, IStorageService storageService,
            EconomicsService econ, IWalletService wallet, SignatureAuthHelper? auth = null)
        {
            _auth = auth;
            _econ = econ;
            _wallet = wallet;
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
            // Check if node already registered by public key
            var existingNodes = await _storageService.GetNodesAsync();
            if (existingNodes.Any(n => n.PublicKey == request.PublicKey))
            {
                return Conflict("Store node already registered");
            }

            // Atomic voucher consume (prevents double registration races)
            if (!await _storageService.TryMarkVoucherUsedAsync(request.Voucher))
            {
                return BadRequest("Invalid or already used voucher");
            }

            var node = new StoreNode
            {
                PublicKey = request.PublicKey,
                Endpoint = request.Endpoint
            };

            await _storageService.SaveNodeAsync(node);

            return new NodeRegistrationResponse
            {
                Id = node.Id,
                PublicKey = node.PublicKey,
                Endpoint = node.Endpoint
            };
        }

        /// <summary>
        /// Super Admin replaces a registered Store's node key (lost or wrong key on the server). Signed on the
        /// Super Admin's machine (X-Timestamp + X-SuperAdmin-Request-Signature). The Store must then run with the
        /// matching NODE_PRIVATE_KEY, or Central refuses its signed calls.
        /// </summary>
        [HttpPost("admin/rekey")]
        public async Task<IActionResult> Rekey()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (_auth == null) return StatusCode(500, "auth unavailable");
            var (ok, err) = _auth.ValidateSuperAdminRequest(Request, raw);
            if (!ok) return Unauthorized(err);

            NodeRekeyRequest? req;
            try { req = System.Text.Json.JsonSerializer.Deserialize<NodeRekeyRequest>(raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { return BadRequest("invalid JSON"); }
            if (req == null || string.IsNullOrWhiteSpace(req.NodeId) || string.IsNullOrWhiteSpace(req.PublicKey))
                return BadRequest("nodeId and publicKey required");
            var pk = req.PublicKey.Trim();
            if (!_keyService.ValidatePublicKey(pk)) return BadRequest("invalid publicKey");

            var nodes = await _storageService.GetNodesAsync();
            var norm = SignatureAuthHelper.NormalizePubKey(pk);
            if (nodes.Any(n => n.Id != req.NodeId && !string.IsNullOrWhiteSpace(n.PublicKey) &&
                               string.Equals(SignatureAuthHelper.NormalizePubKey(n.PublicKey.Trim()), norm, System.StringComparison.OrdinalIgnoreCase)))
                return Conflict("publicKey already belongs to another Store");
            var old = nodes.FirstOrDefault(n => n.Id == req.NodeId);
            if (old == null || !await _storageService.UpdateNodePublicKeyAsync(req.NodeId, pk)) return NotFound("node not found");
            return Ok(new { id = req.NodeId, endpoint = old.Endpoint, publicKey = pk, previousPublicKey = old.PublicKey });
        }

        [HttpGet("list")]
        public async Task<ActionResult<List<StoreNode>>> ListNodes()
        {
            var nodes = await _storageService.GetActiveNodesAsync();
            // v2 §8: when bonds are enforced, only nodes with the minimum bond are offered to clients.
            if (!_econ.EnforceBonds) return nodes;
            await _wallet.EnsureReadyAsync();
            return await _econ.FilterStoreNodesByBondAsync(nodes);
        }
    }
} 
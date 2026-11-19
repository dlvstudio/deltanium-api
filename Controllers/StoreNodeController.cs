using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;
using DeltaniumApi.Services;
using DeltaniumApi.Services.Dltp;
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
            /// <summary>HTTPS endpoint (optional when TcpEndpoint is given).</summary>
            public string? Endpoint { get; set; }
            /// <summary>DLTP endpoint tcps://host:port (optional when Endpoint is given).</summary>
            public string? TcpEndpoint { get; set; }
            public string Voucher { get; set; }
            public string PublicKey { get; set; }
        }

        public class NodeRegistrationResponse
        {
            public string Id { get; set; }
            public string PublicKey { get; set; }
            public string Endpoint { get; set; }
            public string? TcpEndpoint { get; set; }
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
            // A node is reachable over HTTPS, over DLTP (TCP), or both — at least one.
            var httpsEndpoint = (request.Endpoint ?? "").Trim().TrimEnd('/');
            var tcpEndpoint = string.IsNullOrWhiteSpace(request.TcpEndpoint) ? null : request.TcpEndpoint.Trim().TrimEnd('/');
            if (httpsEndpoint.Length == 0 && tcpEndpoint == null)
                return BadRequest("Endpoint (https) or TcpEndpoint (tcps://host:port) is required");
            if (httpsEndpoint.Length > 0 && !NodeAddress.IsAcceptableHttps(httpsEndpoint))
                return BadRequest("Endpoint must be an https:// URL");
            if (tcpEndpoint != null && !NodeAddress.IsAcceptableTcp(tcpEndpoint))
                return BadRequest("TcpEndpoint must look like tcps://host:port");
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
                Endpoint = httpsEndpoint,
                TcpEndpoint = tcpEndpoint
            };

            await _storageService.SaveNodeAsync(node);

            return new NodeRegistrationResponse
            {
                Id = node.Id,
                PublicKey = node.PublicKey,
                Endpoint = node.Endpoint,
                TcpEndpoint = node.TcpEndpoint
            };
        }

        /// <summary>
        /// Super Admin replaces a registered Store's node key (lost or wrong key on the server). Signed on the
        /// Super Admin's machine (X-Timestamp + X-SuperAdmin-Request-Signature). The Store must then run with the
        /// matching NODE_PRIVATE_KEY, or Central refuses its signed calls.
        /// </summary>
        public class NodeEndpointsRequest
        {
            public string PublicKey { get; set; } = "";
            /// <summary>New HTTPS endpoint; "" removes it; omitted keeps it.</summary>
            public string? Endpoint { get; set; }
            /// <summary>New DLTP endpoint (tcps://host:port); "" removes it; omitted keeps it.</summary>
            public string? TcpEndpoint { get; set; }
        }

        /// <summary>Validate and apply an endpoints change; the node must keep at least one way in.</summary>
        public static (bool Ok, string Error, string Https, string? Tcp) ApplyEndpoints(string currentHttps, string? currentTcp, NodeEndpointsRequest req)
        {
            var https = req.Endpoint == null ? (currentHttps ?? "") : req.Endpoint.Trim().TrimEnd('/');
            var tcp = req.TcpEndpoint == null ? currentTcp : (req.TcpEndpoint.Trim().Length == 0 ? null : req.TcpEndpoint.Trim().TrimEnd('/'));
            if (https.Length == 0 && tcp == null) return (false, "A node needs an https Endpoint or a TcpEndpoint", "", null);
            if (https.Length > 0 && !NodeAddress.IsAcceptableHttps(https)) return (false, "Endpoint must be an https:// URL", "", null);
            if (tcp != null && !NodeAddress.IsAcceptableTcp(tcp)) return (false, "TcpEndpoint must look like tcps://host:port", "", null);
            return (true, "", https, tcp);
        }

        /// <summary>Super Admin sets a registered Store's HTTPS and/or DLTP endpoint (e.g. adding tcps://host:7333).</summary>
        [HttpPost("admin/endpoints")]
        public async Task<IActionResult> SetEndpoints()
        {
            Request.EnableBuffering();
            string raw;
            using (var reader = new System.IO.StreamReader(Request.Body, leaveOpen: true)) raw = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            if (_auth == null) return StatusCode(500, "auth unavailable");
            var (ok, err) = _auth.ValidateSuperAdminRequest(Request, raw);
            if (!ok) return Unauthorized(err);

            NodeEndpointsRequest? req;
            try { req = System.Text.Json.JsonSerializer.Deserialize<NodeEndpointsRequest>(raw, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch { return BadRequest("invalid JSON"); }
            if (req == null || string.IsNullOrWhiteSpace(req.PublicKey)) return BadRequest("publicKey required");
            var node = (await _storageService.GetNodesAsync()).FirstOrDefault(n => n.PublicKey == req.PublicKey.Trim());
            if (node == null) return NotFound("node not found");
            var r = ApplyEndpoints(node.Endpoint, node.TcpEndpoint, req);
            if (!r.Ok) return BadRequest(r.Error);
            await _storageService.UpdateNodeEndpointsAsync(node.Id, r.Https, r.Tcp);
            return Ok(new { node.Id, node.PublicKey, endpoint = r.Https, tcpEndpoint = r.Tcp });
        }

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
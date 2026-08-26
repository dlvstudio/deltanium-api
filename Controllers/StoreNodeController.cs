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

        public StoreNodeController(KeyService keyService, StorageService storageService)
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
            // Check voucher validity
            if (!await _storageService.IsVoucherValidAsync(request.Voucher))
            {
                return BadRequest("Invalid or already used voucher");
            }

            // Check if node already registered by public key
            var existingNodes = await _storageService.GetNodesAsync();
            if (existingNodes.Any(n => n.PublicKey == request.PublicKey))
            {
                return Conflict("Store node already registered");
            }

            var node = new StoreNode
            {
                PublicKey = request.PublicKey,
                Endpoint = request.Endpoint
            };

            await _storageService.SaveNodeAsync(node);
            await _storageService.MarkVoucherUsedAsync(request.Voucher);

            return new NodeRegistrationResponse
            {
                Id = node.Id,
                PublicKey = node.PublicKey,
                Endpoint = node.Endpoint
            };
        }

        [HttpGet("list")]
        public async Task<ActionResult<List<StoreNode>>> ListNodes()
        {
            return await _storageService.GetActiveNodesAsync();
        }
    }
} 
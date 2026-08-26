using System.Collections.Generic;
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
        private readonly StorageService _storageService;
        private readonly KeyService _keyService;

        public class NodeFilesResponse
        {
            public StoreNode Node { get; set; }
            public List<StoredFile> Files { get; set; }
        }

        public StoredFileController(StorageService storageService, KeyService keyService)
        {
            _storageService = storageService;
            _keyService = keyService;
        }

        [HttpGet("nodes/{publicKey}")]
        public async Task<ActionResult<List<StoreNode>>> GetNodesStoringUserFiles(string publicKey)
        {
            if (!_keyService.ValidatePublicKey(publicKey))
            {
                return BadRequest("Invalid public key format");
            }

            return await _storageService.GetNodesStoringUserFilesAsync(publicKey);
        }

        [HttpGet("details/{publicKey}")]
        public async Task<ActionResult<List<NodeFilesResponse>>> GetDetailedNodesWithFiles(string publicKey)
        {
            if (!_keyService.ValidatePublicKey(publicKey))
            {
                return BadRequest("Invalid public key format");
            }

            var nodes = await _storageService.GetNodesStoringUserFilesAsync(publicKey);
            var result = new List<NodeFilesResponse>();

            foreach (var node in nodes)
            {
                var files = await _storageService.GetUserFilesOnNodeAsync(publicKey, node.Id);
                result.Add(new NodeFilesResponse
                {
                    Node = node,
                    Files = files
                });
            }

            return result;
        }

        [HttpPost("register")]
        public async Task<ActionResult<StoredFile>> RegisterStoredFile([FromBody] StoredFile file)
        {
            if (!_keyService.ValidatePublicKey(file.OwnerPublicKey))
            {
                return BadRequest("Invalid owner public key");
            }

            var nodes = await _storageService.GetNodesAsync();
            if (!nodes.Exists(n => n.Id == file.StoreNodeId))
            {
                return BadRequest("Invalid store node ID");
            }

            await _storageService.SaveStoredFileAsync(file);
            return file;
        }
    }
} 
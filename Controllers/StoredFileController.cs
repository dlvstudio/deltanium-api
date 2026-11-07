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
        }

        public StoredFileController(
            IStorageService storageService,
            KeyService keyService,
            SignatureAuthHelper auth)
        {
            _storageService = storageService;
            _keyService = keyService;
            _auth = auth;
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

            await _storageService.SaveStoredFilesBatchAsync(files);
            return Ok(new { fileId = req.FileId, replicas = validIds.Count, storeNodeIds = validIds });
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

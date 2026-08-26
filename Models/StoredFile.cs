using System;

namespace DeltaniumApi.Models
{
    public class StoredFile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FileId { get; set; }  // Unique identifier of the file
        public string OwnerPublicKey { get; set; }  // Public key of file owner
        public string StoreNodeId { get; set; }  // ID of the store node holding this file
        public DateTime StoredAt { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;
        public string FileHash { get; set; }  // Hash of the file for verification
        public long FileSize { get; set; }  // Size of the file in bytes
    }
} 
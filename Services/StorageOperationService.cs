using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumApi.Models;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Service for creating StorageOperation transactions.
    /// StorageOperation represents file uploads with associated storage contracts.
    /// 
    /// Flow:
    /// 1. App and Storage Node create a signed StorageContract (off-chain)
    /// 2. Storage Node creates a StorageOperation transaction with:
    ///    - The full contract object (with both signatures) as proof
    ///    - File details (blockId, fileIds, size)
    ///    - Storage Node's signature
    ///    - Fee (matching contract.TotalFee)
    /// 3. App provides cosignature to approve the transaction
    /// 4. Blocker validates: contract signatures, fee matching, file coverage, time validity
    /// </summary>
    public interface IStorageOperationService
    {
        /// <summary>
        /// Create a new StorageOperation transaction.
        /// The Store Node calls this after successful upload to create the blockchain transaction.
        /// </summary>
        StorageOperation CreateStorageOperation(
            string storageNodePubKey,
            StorageContract contract,
            List<string> fileIds,
            string? blockId,
            long fileSize,
            decimal fee);
        
        /// <summary>
        /// Create a StorageOperation but mark it pending App cosignature.
        /// App must provide its signature in the Cosignatures field.
        /// </summary>
        StorageOperation CreatePendingStorageOperation(
            string storageNodePubKey,
            StorageContract contract,
            List<string> fileIds,
            string? blockId,
            long fileSize,
            decimal fee,
            string appPubKey);
    }

    public class StorageOperationService : IStorageOperationService
    {
        public StorageOperation CreateStorageOperation(
            string storageNodePubKey,
            StorageContract contract,
            List<string> fileIds,
            string? blockId,
            long fileSize,
            decimal fee)
        {
            if (fee != contract.TotalFee)
                throw new ArgumentException($"Fee {fee} must equal contract.TotalFee {contract.TotalFee}");

            var operation = new StorageOperation
            {
                ContractId = contract.ContractId,
                ContractObject = contract,
                FileIds = fileIds ?? new(),
                BlockId = blockId,
                FileSize = fileSize,
                StorageNodePubKey = storageNodePubKey,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            return operation;
        }

        public StorageOperation CreatePendingStorageOperation(
            string storageNodePubKey,
            StorageContract contract,
            List<string> fileIds,
            string? blockId,
            long fileSize,
            decimal fee,
            string appPubKey)
        {
            var operation = CreateStorageOperation(
                storageNodePubKey,
                contract,
                fileIds,
                blockId,
                fileSize,
                fee);

            operation.AppPubKey = appPubKey;
            operation.Status = "PendingAppSignature";

            return operation;
        }
    }

    /// <summary>
    /// Represents a StorageOperation - a transaction for file storage.
    /// This data is included in BlockchainTransaction.Data for StorageOperation type txs.
    /// </summary>
    public class StorageOperation
    {
        public string ContractId { get; set; } = string.Empty;
        public StorageContract? ContractObject { get; set; }
        public List<string> FileIds { get; set; } = new();
        public string? BlockId { get; set; }
        public long FileSize { get; set; }
        public string StorageNodePubKey { get; set; } = string.Empty;
        public string? AppPubKey { get; set; } // Set when waiting for App signature
        public long Timestamp { get; set; }
        public string Status { get; set; } = "Ready"; // "Ready", "PendingAppSignature", "Signed"
    }
}

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace DeltaniumApi.Models
{
    /// <summary>
    /// Represents a signed contract between an App (user) and Storage Node for file storage.
    /// The contract specifies terms (duration, size, fees) and is signed by both parties.
    /// 
    /// Message format to sign:
    /// - TimeFixed: "TimeFixed|{startDate}|{endDate}|{appPublicKey}|{storageNodePublicKey}|{totalFee}|{totalFileSize}"
    /// - OpenEnded: "OpenEnded|{startDate}|{appPublicKey}|{storageNodePublicKey}|{totalFee}|{totalFileSize}"
    /// </summary>
    public class StorageContract
    {
        public string ContractId { get; set; } // UUID for contract identification
        public string ContractType { get; set; } // "TimeFixed" or "OpenEnded"
        
        // Parties involved
        public string AppPublicKey { get; set; } // User's public key (signer 1)
        public string StorageNodePublicKey { get; set; } // Storage Node's public key (signer 2)
        
        // Contract terms
        public long StartDateUnix { get; set; } // Unix timestamp when contract starts
        public long? EndDateUnix { get; set; } // Unix timestamp when contract ends (null for OpenEnded)
        public long TotalFileSize { get; set; } // Max total file size in bytes for this contract
        public decimal TotalFee { get; set; } // Total fee in Deltanium for storage
        public List<string> FileIds { get; set; } = new(); // List of file IDs covered by contract
        
        // Signatures and proof
        public string MessageToSign { get; set; } // The message that was signed
        public string AppSignature { get; set; } // App's signature over the message
        public string StorageNodeSignature { get; set; } // Storage Node's signature over the message
        
        // Metadata
        public long CreatedAtUnix { get; set; }
        public string Status { get; set; } = "Active"; // "Active", "Completed", "Cancelled"

        /// <summary>
        /// Generate the message that both parties must sign.
        /// </summary>
        public static string GenerateMessageToSign(
            string contractType,
            long startDate,
            long? endDate,
            string appPublicKey,
            string storageNodePublicKey,
            decimal totalFee,
            long totalFileSize)
        {
            if (contractType == "TimeFixed")
            {
                if (!endDate.HasValue)
                    throw new ArgumentException("TimeFixed contracts require endDate");
                return $"TimeFixed|{startDate}|{endDate}|{appPublicKey}|{storageNodePublicKey}|{totalFee}|{totalFileSize}";
            }
            else if (contractType == "OpenEnded")
            {
                return $"OpenEnded|{startDate}|{appPublicKey}|{storageNodePublicKey}|{totalFee}|{totalFileSize}";
            }
            else
            {
                throw new ArgumentException($"Unknown contract type: {contractType}");
            }
        }

        /// <summary>
        /// Verify that both signatures are valid for this contract.
        /// Note: Signature verification requires the actual cryptographic implementation.
        /// This is a placeholder that returns true - implement with your actual crypto library.
        /// </summary>
        public bool VerifySignatures()
        {
            // TODO: Implement signature verification using your cryptographic library
            // For now, we just verify that signatures exist and are non-empty
            if (string.IsNullOrWhiteSpace(AppSignature))
                return false;
            
            if (string.IsNullOrWhiteSpace(StorageNodeSignature))
                return false;
            
            if (string.IsNullOrWhiteSpace(MessageToSign))
                return false;

            // In production, call your signature verification library:
            // return CryptoLib.VerifySignature(AppPublicKey, MessageToSign, AppSignature)
            //     && CryptoLib.VerifySignature(StorageNodePublicKey, MessageToSign, StorageNodeSignature);
            
            return true;
        }

        /// <summary>
        /// Check if the contract is currently valid (covers the current time).
        /// </summary>
        public bool IsValidNow()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            if (Status != "Active")
                return false;
            
            if (now < StartDateUnix)
                return false; // Not yet started
            
            if (ContractType == "TimeFixed" && EndDateUnix.HasValue && now > EndDateUnix)
                return false; // Contract expired
            
            if (ContractType == "OpenEnded" && now > StartDateUnix + (365 * 24 * 60 * 60))
                return false; // OpenEnded auto-expires after 1 year
            
            return true;
        }

        /// <summary>
        /// Check if a file ID is covered by this contract.
        /// </summary>
        public bool CoversFileId(string fileId)
        {
            return FileIds.Contains(fileId);
        }

        /// <summary>
        /// Check if a file size is within contract limits.
        /// </summary>
        public bool CoversFileSize(long fileSizeBytes)
        {
            // Rough check: sum of all files should not exceed limit
            // This should be refined based on actual usage tracking
            return fileSizeBytes <= TotalFileSize;
        }
    }
}

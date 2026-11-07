using System;

namespace DeltaniumApi.Models
{
    public class User
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PublicKey { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? FullName { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? Bio { get; set; }
        public string? AvatarUrl { get; set; }
        public string? Signature { get; set; } // Registration signature for blockchain operations
        public long? RegistrationTimestamp { get; set; } // Unix timestamp used in registration signature
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
} 
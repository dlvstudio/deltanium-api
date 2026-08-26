using System;

namespace DeltaniumApi.Models.Requests
{
    public class SignedRegisterRequest
    {
        public string PublicKey { get; set; }
        public string Email { get; set; }
        public string FullName { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string Bio { get; set; }
        public string AvatarUrl { get; set; }
        public string Signature { get; set; }
        public long Timestamp { get; set; } // Unix timestamp for signature validation
    }
} 
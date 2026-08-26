using System;

namespace DeltaniumApi.Models.Requests
{
    public class RegisterRequest
    {
        public string Email { get; set; }
        public string FullName { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string Bio { get; set; }
        public string AvatarUrl { get; set; }
    }
} 
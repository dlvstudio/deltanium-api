using System.Text.Json;
using DeltaniumApi.Controllers;
using DeltaniumApi.Models;
using Xunit;

namespace DeltaniumApi.Tests.Controllers
{
    public class UserProfilePrivacyTests
    {
        [Fact]
        public void Public_profile_leaves_out_email_date_of_birth_and_signature()
        {
            var user = new User
            {
                PublicKey = "04abc",
                Email = "someone@example.com",
                FullName = "Some One",
                DateOfBirth = new DateTime(1990, 1, 2),
                Signature = "sig",
                Bio = "hi",
            };

            var json = JsonSerializer.Serialize(UserController.PublicProfile(user));

            Assert.DoesNotContain("someone@example.com", json);
            Assert.DoesNotContain("1990", json);
            Assert.DoesNotContain("\"sig\"", json);
            Assert.Contains("Some One", json);
            Assert.Contains("04abc", json);
        }
    }
}

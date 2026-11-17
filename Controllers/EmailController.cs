using System.IO;
using System.Threading.Tasks;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeltaniumApi.Controllers
{
    /// <summary>Email verification for the free tier (TOKENOMICS_V2 §10). Owner-signed requests.</summary>
    [ApiController]
    [Route("api/email")]
    public class EmailController : ControllerBase
    {
        private readonly SignatureAuthHelper _auth;
        private readonly EmailVerificationService _verify;

        public EmailController(SignatureAuthHelper auth, EmailVerificationService verify)
        {
            _auth = auth;
            _verify = verify;
        }

        public class SendCodeRequest { public string Email { get; set; } = ""; }
        public class VerifyRequest { public string Code { get; set; } = ""; }

        private async Task<string> BodyAsync()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            return body;
        }

        private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        /// <summary>Email a 6-digit code to the address (max 5 a day, one a minute).</summary>
        [HttpPost("send-code")]
        public async Task<IActionResult> SendCode()
        {
            var body = await BodyAsync();
            var (ok, pk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);
            var req = System.Text.Json.JsonSerializer.Deserialize<SendCodeRequest>(body, Json);
            var (sent, status, error) = await _verify.SendCodeAsync(pk!, req?.Email ?? "");
            return sent ? Ok(new { sent = true }) : StatusCode(status, new { error });
        }

        [HttpPost("verify")]
        public async Task<IActionResult> Verify()
        {
            var body = await BodyAsync();
            var (ok, pk, err) = await _auth.ValidateAsync(Request, body);
            if (!ok) return Unauthorized(err);
            var req = System.Text.Json.JsonSerializer.Deserialize<VerifyRequest>(body, Json);
            var (verified, status, error, email) = await _verify.VerifyAsync(pk!, req?.Code ?? "");
            return verified ? Ok(new { verified = true, email }) : StatusCode(status, new { error });
        }
    }
}

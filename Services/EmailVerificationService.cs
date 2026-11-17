using System;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeltaniumApi.Services
{
    /// <summary>One owner's email verification state (econ doc "email-verify", id = owner public key).</summary>
    public class EmailVerification
    {
        public string Id { get; set; } = "";
        public string PendingEmail { get; set; } = "";
        /// <summary>sha256(owner|email|code): the code itself is never stored or logged.</summary>
        public string CodeHash { get; set; } = "";
        public DateTime CodeExpiresAt { get; set; }
        public int Attempts { get; set; }
        public DateTime LastSentAt { get; set; }
        public string SendDay { get; set; } = "";
        public int SentToday { get; set; }
        public string? VerifiedEmail { get; set; }
        public DateTime? VerifiedAt { get; set; }
    }

    /// <summary>Which owner holds a verified address (econ doc "email-owner", id = sha256(lowercase email)).</summary>
    public class EmailOwner
    {
        public string Id { get; set; } = "";
        public string Owner { get; set; } = "";
    }

    public interface IEmailSender
    {
        bool IsConfigured { get; }
        Task SendAsync(string to, string subject, string body);
    }

    /// <summary>SMTP from Email:Smtp:{Host,Port,User,Password,From,EnableSsl}; the password belongs in the DPAPI env.</summary>
    public class SmtpEmailSender : IEmailSender
    {
        private readonly IConfiguration _config;
        public SmtpEmailSender(IConfiguration config) => _config = config;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["Email:Smtp:Host"]) && !string.IsNullOrWhiteSpace(_config["Email:Smtp:From"]);

        public async Task SendAsync(string to, string subject, string body)
        {
            using var client = new SmtpClient(_config["Email:Smtp:Host"], int.TryParse(_config["Email:Smtp:Port"], out var p) ? p : 587)
            {
                EnableSsl = !string.Equals(_config["Email:Smtp:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase),
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
            if (!string.IsNullOrWhiteSpace(_config["Email:Smtp:User"]))
                client.Credentials = new NetworkCredential(_config["Email:Smtp:User"], _config["Email:Smtp:Password"]);
            using var msg = new MailMessage(_config["Email:Smtp:From"]!, to, subject, body);
            await client.SendMailAsync(msg);
        }
    }

    /// <summary>
    /// Proves that a user controls an email address before it unlocks the free tier (TOKENOMICS_V2 §10:
    /// "verified email or phone"). Without it anyone can register any number of accounts with made-up
    /// addresses, each with 200 MB paid by the network fund. One verified address unlocks one account.
    /// </summary>
    public class EmailVerificationService
    {
        public const string CVerify = "email-verify", COwner = "email-owner";
        private static readonly Regex EmailShape = new(@"^[^@\s]{1,64}@[^@\s]{1,255}\.[^@\s]{2,}$", RegexOptions.Compiled);

        private readonly EconDocStore _docs;
        private readonly IEmailSender _sender;
        private readonly ILogger<EmailVerificationService>? _log;

        public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan ResendDelay = TimeSpan.FromSeconds(60);
        public const int MaxSendsPerDay = 5, MaxAttempts = 5;

        public EmailVerificationService(EconDocStore docs, IEmailSender sender, ILogger<EmailVerificationService>? log = null)
        {
            _docs = docs;
            _sender = sender;
            _log = log;
        }

        public static string Normalize(string email) => email.Trim().ToLowerInvariant();
        public static string EmailId(string email) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(email)))).ToLowerInvariant();
        private static string CodeHash(string owner, string email, string code) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}|{Normalize(email)}|{code.Trim()}"))).ToLowerInvariant();

        public async Task<EmailVerification?> GetAsync(string owner) => await _docs.GetAsync<EmailVerification>(CVerify, owner);

        public async Task<(bool Ok, int Status, string Error)> SendCodeAsync(string owner, string email, DateTime? nowUtc = null, Func<string, Task>? deliverForTests = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            email = (email ?? "").Trim();
            if (!EmailShape.IsMatch(email)) return (false, 400, "Invalid email address");
            if (deliverForTests == null && !_sender.IsConfigured) return (false, 503, "Email sending is not configured on this server");

            var holder = await _docs.GetAsync<EmailOwner>(COwner, EmailId(email));
            if (holder != null && holder.Owner != owner) return (false, 409, "This email already verifies another account");

            var v = await GetAsync(owner) ?? new EmailVerification { Id = owner };
            var day = now.ToString("yyyy-MM-dd");
            if (v.SendDay != day) { v.SendDay = day; v.SentToday = 0; }
            if (v.SentToday >= MaxSendsPerDay) return (false, 429, "Too many codes today; try again tomorrow");
            if (now - v.LastSentAt < ResendDelay) return (false, 429, "Wait a minute before asking for another code");

            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            v.PendingEmail = email;
            v.CodeHash = CodeHash(owner, email, code);
            v.CodeExpiresAt = now + CodeLifetime;
            v.Attempts = 0;
            v.LastSentAt = now;
            v.SentToday++;
            await _docs.PutAsync(CVerify, owner, v);

            try
            {
                if (deliverForTests != null) await deliverForTests(code);
                else await _sender.SendAsync(email, "Your Deltanium verification code",
                    $"Your Deltanium verification code is {code}.\n\nIt expires in {CodeLifetime.TotalMinutes:0} minutes. " +
                    "If you did not ask for it, ignore this email.");
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Verification email to an address of {Owner} failed: {Err}", owner, ex.Message);
                return (false, 502, "Could not send the email; check the address and try again");
            }
            return (true, 200, "");
        }

        public async Task<(bool Ok, int Status, string Error, string? Email)> VerifyAsync(string owner, string code, DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var v = await GetAsync(owner);
            if (v == null || string.IsNullOrEmpty(v.CodeHash)) return (false, 400, "Ask for a code first", null);
            if (now > v.CodeExpiresAt) return (false, 410, "Code expired; ask for a new one", null);
            if (v.Attempts >= MaxAttempts) return (false, 429, "Too many wrong codes; ask for a new one", null);

            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(CodeHash(owner, v.PendingEmail, code ?? "")), Encoding.UTF8.GetBytes(v.CodeHash)))
            {
                v.Attempts++;
                await _docs.PutAsync(CVerify, owner, v);
                return (false, 400, "Wrong code", null);
            }

            var id = EmailId(v.PendingEmail);
            var holder = await _docs.GetAsync<EmailOwner>(COwner, id);
            if (holder != null && holder.Owner != owner) return (false, 409, "This email already verifies another account", null);

            // Moving to a new address releases the old one.
            if (!string.IsNullOrEmpty(v.VerifiedEmail) && EmailId(v.VerifiedEmail) != id)
                await _docs.DeleteAsync(COwner, EmailId(v.VerifiedEmail));
            await _docs.PutAsync(COwner, id, new EmailOwner { Id = id, Owner = owner });
            v.VerifiedEmail = v.PendingEmail;
            v.VerifiedAt = now;
            v.CodeHash = "";
            v.Attempts = 0;
            await _docs.PutAsync(CVerify, owner, v);
            return (true, 200, "", v.VerifiedEmail);
        }
    }
}

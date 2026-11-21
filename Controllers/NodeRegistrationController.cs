using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DeltaniumApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DeltaniumApi.Controllers
{
    /// <summary>
    /// Node registrations waiting for their fee and the Super Admin (see <see cref="NodeRegistrationService"/>).
    /// Public: GET {publicKey} (status and how to pay). Super Admin: admin/list, admin/approve, admin/reject.
    /// </summary>
    [ApiController]
    [Route("api/noderegistration")]
    public class NodeRegistrationController : ControllerBase
    {
        private readonly NodeRegistrationService _regs;
        private readonly SignatureAuthHelper _auth;
        private readonly IConfiguration _config;

        public NodeRegistrationController(NodeRegistrationService regs, SignatureAuthHelper auth, IConfiguration config)
        {
            _regs = regs;
            _auth = auth;
            _config = config;
        }

        /// <summary>The registration status of a node and, while unpaid, how to pay (no voucher in the answer).</summary>
        [HttpGet("{publicKey}")]
        public async Task<IActionResult> Status(string publicKey)
        {
            var reg = await _regs.FindByKeyAsync(publicKey);
            return reg == null ? NotFound("No registration for this key") : Ok(PublicView(reg, _config));
        }

        [HttpGet("admin/list")]
        public async Task<IActionResult> List([FromQuery] string status = "awaiting_payment")
        {
            if (!_auth.ValidateSuperAdminRequest(Request, "").Ok) return Unauthorized("Super Admin request signature required");
            var list = await _regs.ListAsync(status);
            return Ok(list.Select(r => new
            {
                registration = PublicView(r, _config),
                voucherHint = r.Voucher.Length > 6 ? r.Voucher[..6] + "…" : r.Voucher,
                signPayload = NodeRegistrationService.ApprovalPayload(r)
            }));
        }

        /// <summary>Approve after the fee arrived: X-SuperAdmin-Signature over the registration's signPayload.</summary>
        [HttpPost("admin/approve")]
        public async Task<IActionResult> Approve()
        {
            var body = await ReadBodyAsync();
            if (!Request.Headers.TryGetValue("X-SuperAdmin-Signature", out var sig) || string.IsNullOrWhiteSpace(sig.FirstOrDefault()))
                return Unauthorized("Missing X-SuperAdmin-Signature (sign the registration's signPayload)");
            var id = ReadString(body, "id");
            if (string.IsNullOrWhiteSpace(id)) return BadRequest("id required");
            var r = await _regs.ApproveAsync(id.Trim(), sig.First()!);
            return r.Status == 200 ? Ok(PublicView(r.Registration!, _config)) : StatusCode(r.Status, r.Error);
        }

        [HttpPost("admin/reject")]
        public async Task<IActionResult> Reject()
        {
            var body = await ReadBodyAsync();
            var (ok, err) = _auth.ValidateSuperAdminRequest(Request, body);
            if (!ok) return Unauthorized(err);
            var id = ReadString(body, "id");
            if (string.IsNullOrWhiteSpace(id)) return BadRequest("id required");
            var r = await _regs.RejectAsync(id.Trim(), ReadString(body, "reason"));
            return r.Status == 200 ? Ok(PublicView(r.Registration!, _config)) : StatusCode(r.Status, r.Error);
        }

        /// <summary>The shape nodes, installers and the admin page read; payment details while the fee is due.</summary>
        public static object PublicView(NodeRegistration r, IConfiguration config)
        {
            var bank = config.GetSection("Economics:TopUp:Bank");
            var pp = config.GetSection("Economics:TopUp:PayPal");
            var content = NodeRegistrationService.TransferContent(r);
            object? paypal = null;
            var link = (pp["Link"] ?? "").Trim().TrimEnd('/');
            if (link.Length > 0 && decimal.TryParse(pp["VndPerUsd"], NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) && rate > 0)
            {
                var usd = Math.Ceiling(r.FeeVnd / rate * 100m) / 100m;
                paypal = new { link, url = $"{link}/{usd.ToString("0.00", CultureInfo.InvariantCulture)}USD", amountUsd = usd };
            }
            var due = r.Status == "awaiting_payment";
            return new
            {
                id = r.Id,
                role = r.Role,
                publicKey = r.PublicKey,
                endpoint = r.Endpoint,
                tcpEndpoint = r.TcpEndpoint,
                status = r.Status,
                reason = r.Reason,
                feeVnd = r.FeeVnd,
                transferContent = content,
                createdAt = r.CreatedAt,
                approvedAt = r.ApprovedAt,
                activatedAt = r.ActivatedAt,
                lastProbeError = r.Status == "approved" ? r.LastProbeError : null,
                bank = due && !string.IsNullOrWhiteSpace(bank["AccountNo"])
                    ? new { bin = bank["Bin"], bankName = bank["BankName"], accountNo = bank["AccountNo"], accountName = bank["AccountName"] }
                    : null,
                paypal = due ? paypal : null,
                instructions = r.Status switch
                {
                    "awaiting_payment" =>
                        $"Pay the {r.FeeVnd:#,0} VND registration fee: bank transfer to {bank["BankName"]} {bank["AccountNo"]} ({bank["AccountName"]}) " +
                        $"with the content {content}, or PayPal with {content} in the note. The Super Admin approves it, then the network activates the node. / " +
                        $"Chuyển {r.FeeVnd:#,0} VND phí đăng ký tới {bank["BankName"]} {bank["AccountNo"]} ({bank["AccountName"]}) với nội dung {content}, " +
                        $"hoặc PayPal ghi {content}. Super Admin duyệt xong, mạng sẽ kích hoạt node.",
                    "approved" => "Fee approved. The network activates the node as soon as it answers on its endpoint (check port forwarding). / " +
                                  "Đã duyệt phí. Node được kích hoạt ngay khi trả lời ở địa chỉ đã đăng ký (kiểm tra mở cổng).",
                    "active" => "Active on the network. / Node đã hoạt động trên mạng.",
                    "expired" => $"The registration fee was not received in time. If you paid, send the proof with the transfer content {content} to the Super Admin. / " +
                                 $"Chưa nhận được phí đăng ký đúng hạn. Nếu đã chuyển, gửi chứng từ kèm nội dung {content} cho Super Admin.",
                    _ => r.Reason ?? r.Status
                }
            };
        }

        private async Task<string> ReadBodyAsync()
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0;
            return body;
        }

        private static string? ReadString(string body, string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
    }
}

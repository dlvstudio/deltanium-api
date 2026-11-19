using DeltaniumApi.Controllers;
using DeltaniumApi.Models;
using FluentAssertions;
using Xunit;

namespace deltanium_api.Tests.Controllers
{
    public class TopUpTransferContentTests
    {
        [Fact]
        public void Transfer_content_is_DLT_plus_the_first_ten_order_id_chars_upper_case()
        {
            var o = new TopUpOrder { Id = "3f9a0c7e12bd4c6e9a1b2c3d4e5f6a7b" };
            WalletController.TransferContent(o).Should().Be("DLT3F9A0C7E12");
        }

        [Fact]
        public void Transfer_content_fits_bank_memo_rules()
        {
            var c = WalletController.TransferContent(new TopUpOrder());
            c.Should().HaveLength(13).And.MatchRegex("^[A-Z0-9]+$");
        }
    }
}

using FluentAssertions;
using DeltaniumApi.Services;
using Xunit;

namespace deltanium_api.Tests.Services;

public class BuildInfoTests
{
    [Fact]
    public void Reports_csproj_version_and_build_time()
    {
        BuildInfo.Version.Should().Be("1.2.0");
        BuildInfo.BuiltAtUtc.Should().NotBeNullOrEmpty();
        BuildInfo.Describe("x").Should().NotBeNull();
    }
}

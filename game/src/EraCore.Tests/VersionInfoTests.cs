using EraCore;
using Xunit;

namespace EraCore.Tests;

public class VersionInfoTests
{
    [Fact]
    public void Current_reports_wave0_scaffold_version()
    {
        Assert.Equal("0.0.1-wave0", VersionInfo.Current);
    }

    [Fact]
    public void VersionString_is_compile_time_constant_matching_Current()
    {
        Assert.Equal(VersionInfo.VersionString, VersionInfo.Current);
    }
}

using EraCore;
using Xunit;

namespace EraCore.Tests;

// [C-30] 测试基建：版本探针证明 EraCore→Tests→Godot 管线存活（T-028/T-029）
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

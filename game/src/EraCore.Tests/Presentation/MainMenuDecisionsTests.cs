using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// T-161 (F-125) 主菜单决策表测试——纯逻辑半边的五级覆盖（G1 承载）：
//   入口表完备性/顺序/标签唯一 + 时代色板键封闭（与 VisualAssetCatalog
//   EraKeys 同域） + 背景双保险链三态 + logo 横裁带几何 + 版本角标归一。

// [F-125] [C-21] 类级关联：本测试类全部方法覆盖 T-161 主菜单场景的纯决策面。
public sealed class MainMenuDecisionsTests
{
    [Fact]
    public void Entries_SixThemeRowsInTaskOrder_WithUniqueLabelsAndPaletteKeys()
    {
        IReadOnlyList<MainMenuDecisions.Entry> entries = MainMenuDecisions.Entries();

        Assert.Equal(
            new[]
            {
                MainMenuDecisions.EntryId.Campaign,
                MainMenuDecisions.EntryId.Endless,
                MainMenuDecisions.EntryId.Procedural,
                MainMenuDecisions.EntryId.Net,
                MainMenuDecisions.EntryId.Settings,
                MainMenuDecisions.EntryId.Quit,
            },
            entries.Select(entry => entry.Id).ToArray());
        Assert.Equal(new[] { "战役", "无尽", "程序关卡", "联机", "设置", "退出" }, entries.Select(e => e.LabelZh));
        Assert.Equal(entries.Count, entries.Select(e => e.LabelZh).Distinct().Count());
        Assert.Equal("e1", entries[0].PaletteKey);
        Assert.Equal("e6", entries[^1].PaletteKey);
    }

    [Fact]
    public void Entries_PaletteKeysStayInsideTheClosedEraTable()
    {
        // 与 Cinematics.VisualAssetCatalog 的封闭键表同域（RL7：色值在
        // game/content/cinematics/visual-assets.json，代码只留键）。
        HashSet<string> paletteKeys = MainMenuDecisions.Entries().Select(e => e.PaletteKey).ToHashSet();
        Assert.Subset(EraCore.Cinematics.VisualAssetCatalog.EraKeys.ToHashSet(), paletteKeys);
    }

    [Theory]
    [InlineData(true, true, MainMenuDecisions.BackgroundKind.MenuLoopVideo)]
    [InlineData(true, false, MainMenuDecisions.BackgroundKind.MenuLoopVideo)]
    [InlineData(false, true, MainMenuDecisions.BackgroundKind.KeyartStill)]
    [InlineData(false, false, MainMenuDecisions.BackgroundKind.DarkFallback)]
    public void PickBackground_DoubleFallbackChain_VideoFirstKeyartSecondDarkLast(
        bool video, bool keyart, MainMenuDecisions.BackgroundKind expected)
    {
        Assert.Equal(expected, MainMenuDecisions.PickBackground(video, keyart));
    }

    [Theory]
    [InlineData(1080, 0.34f, 356, 367)]   // 中央带：band=round(1080*0.34)=367, y=(1080-367)/2=356
    [InlineData(360, 0.5f, 90, 180)]
    [InlineData(100, 1f, 0, 100)]          // 全高=整图（上限）
    [InlineData(100, 0f, 49, 1)]           // 零比=钳到 1px（下限保护，不抛出）
    [InlineData(0, 0.5f, 0, 0)]            // 不可读图=空带
    public void LogoCropBand_CentersTheBand_AndClampsDegenerateInput(
        int height, float ratio, int expectedY, int expectedHeight)
    {
        (int y, int bandHeight) = MainMenuDecisions.LogoCropBand(height, ratio);
        Assert.Equal((expectedY, expectedHeight), (y, bandHeight));
    }

    [Fact]
    public void LogoCropBand_AlwaysStaysInsideTheImage()
    {
        foreach (int height in new[] { 1, 7, 64, 217, 1080 })
        {
            foreach (float ratio in new[] { -0.5f, 0.01f, 0.34f, 0.9f, 1.5f })
            {
                (int y, int bandHeight) = MainMenuDecisions.LogoCropBand(height, ratio);
                Assert.InRange(y, 0, height);
                Assert.InRange(bandHeight, 0, height);
                Assert.True(y + bandHeight <= height, $"band overflows: y={y} h={bandHeight} img={height}");
            }
        }
    }

    [Theory]
    [InlineData("0.0.1-wave0", "v0.0.1-wave0")]
    [InlineData("v1.1.1", "v1.1.1")]        // 已带 v 前缀不叠加
    [InlineData("V2.0", "v2.0")]            // 大小写归一
    [InlineData("", "v")]
    public void VersionBadge_NormalizesThePrefix(string raw, string expected)
    {
        Assert.Equal(expected, MainMenuDecisions.VersionBadge(raw));
    }

    [Fact]
    public void MenuBgmState_IsTheCalmLoop()
    {
        // 菜单 BGM=AudioDirector calm 态（任务卡口径的锚定断言——改态须过这行）。
        Assert.Equal("calm", MainMenuDecisions.MenuBgmState);
        Assert.Equal("我在游戏觉醒系统", MainMenuDecisions.TitleZh);
        Assert.Equal("Awakened System TD", MainMenuDecisions.TitleEn);
    }

    [Fact]
    public void AssetPaths_MatchTheRegisteredSlots()
    {
        // logo 牌匾槽位=主管落图位（res://textures/ui/logo-banner.png）；
        // keyart 回退=docs 轨 concept-keyart-eras.png（任务卡双保险链落点）。
        Assert.Equal("res://textures/ui/logo-banner.png", MainMenuDecisions.LogoTexturePath);
        Assert.Equal("docs/assets/concept-keyart-eras.png", MainMenuDecisions.KeyartRelativePath);
    }
}

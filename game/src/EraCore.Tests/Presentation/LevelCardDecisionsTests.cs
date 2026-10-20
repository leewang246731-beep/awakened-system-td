using System.Text.Json;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// T-162 (W10 B 批/F-005) 选关卡片决策表测试——纯逻辑半边覆盖（G1 承载）：
//   banner 主干推导（时代+序号确定性，slug 不参与）/目录清单唯一挑选
//  +威胁三档分界 +星槽归一 +真文件 29/29 映射验证（e1~e6.json 数据行
//   ↔banner 目录逐行命中——缺图=接线错，不是缺资产）。

// [F-005] [C-21] 类级关联：本测试类全部方法覆盖 T-162 选关卡片的纯决策面。
public sealed class LevelCardDecisionsTests
{
    // ---- banner 主干推导 ---------------------------------------------------

    [Theory]
    [InlineData(1, 1, false, "banner-e1-l1-")]        // e1 第 1 行 ↔ l1
    [InlineData(1, 4, false, "banner-e1-l4-")]        // e1 非终关末行 ↔ l4
    [InlineData(2, 3, false, "banner-e2-l3-")]        // e2 三非终关 ↔ l1~l3
    [InlineData(6, 4, false, "banner-e6-l4-")]
    [InlineData(1, 5, true, "banner-e1-final-")]      // final 行 ↔ final（slot 只是装饰）
    [InlineData(2, 4, true, "banner-e2-final-")]
    [InlineData(6, 5, true, "banner-e6-final-")]
    [InlineData(3, 0, true, "banner-e3-final-")]      // 章节题图=该时代终关 banner
    public void LevelBannerStem_EraPlusSlotDerivation_SlugNeverEnters(
        int era, int slot, bool isFinal, string expected)
    {
        Assert.Equal(expected, LevelCardDecisions.LevelBannerStem(era, slot, isFinal));
    }

    [Fact]
    public void LevelBannerStem_ClampsOutOfRangeEraAndSlotToMinimum()
    {
        Assert.Equal("banner-e1-l1-", LevelCardDecisions.LevelBannerStem(0, 0, isFinal: false));
        Assert.Equal("banner-e1-final-", LevelCardDecisions.LevelBannerStem(-3, -1, isFinal: true));
    }

    [Fact]
    public void ChapterBannerStem_EqualsTheEraFinalStem()
    {
        for (int era = 1; era <= 6; era++)
        {
            Assert.Equal(
                LevelCardDecisions.LevelBannerStem(era, 5, isFinal: true),
                LevelCardDecisions.ChapterBannerStem(era));
        }
    }

    [Fact]
    public void IsFinalLevel_FollowsTheExistingFinalIdConventionOnly()
    {
        Assert.True(LevelCardDecisions.IsFinalLevel("campaign:level-e1-final"));
        Assert.True(LevelCardDecisions.IsFinalLevel("campaign:level-e6-final"));
        Assert.False(LevelCardDecisions.IsFinalLevel("campaign:level-e1-01"));
        Assert.False(LevelCardDecisions.IsFinalLevel("campaign:level-e1-01-finalx"));
    }

    // ---- 目录清单唯一挑选 ---------------------------------------------------

    [Fact]
    public void PickBannerFile_MatchesByStemPrefixAndPicksTheSmallestName()
    {
        string[] files =
        {
            "banner-e1-l1-awakening-grove.png",
            "banner-e1-l2-dry-river-pass.png",
            "banner-e1-final-treader-court.png",
            "banner-mode-endless.png",
            "notes.txt",
        };

        Assert.Equal("banner-e1-l1-awakening-grove.png",
            LevelCardDecisions.PickBannerFile("banner-e1-l1-", files));
        Assert.Equal("banner-e1-final-treader-court.png",
            LevelCardDecisions.PickBannerFile("banner-e1-final-", files));
        Assert.Equal("banner-mode-endless.png",
            LevelCardDecisions.PickBannerFile(LevelCardDecisions.EndlessModeBannerStem, files));
        Assert.Null(LevelCardDecisions.PickBannerFile("banner-e9-l1-", files));
        Assert.Null(LevelCardDecisions.PickBannerFile(string.Empty, files));
    }

    [Fact]
    public void PickBannerFile_SlugCollisionsResolveDeterministically()
    {
        // 素材侧同名主干冗余：字典序最小者胜——选关不因冗余阻断。
        string[] files = { "banner-e1-l1-zeta.png", "banner-e1-l1-alpha.png" };
        Assert.Equal("banner-e1-l1-alpha.png", LevelCardDecisions.PickBannerFile("banner-e1-l1-", files));
    }

    [Fact]
    public void PickBannerFile_IgnoresNonPngAndCaseInsensitiveSuffix()
    {
        string[] files = { "banner-e1-l1-x.PNG", "banner-e1-l1-y.jpg" };
        Assert.Equal("banner-e1-l1-x.PNG", LevelCardDecisions.PickBannerFile("banner-e1-l1-", files));
    }

    // ---- 威胁三档 + 星槽 ----------------------------------------------------

    [Theory]
    [InlineData(0.3, LevelCardDecisions.ThreatTier.Low)]
    [InlineData(0.44, LevelCardDecisions.ThreatTier.Low)]
    [InlineData(0.45, LevelCardDecisions.ThreatTier.Mid)]
    [InlineData(0.6, LevelCardDecisions.ThreatTier.Mid)]
    [InlineData(0.74, LevelCardDecisions.ThreatTier.Mid)]
    [InlineData(0.75, LevelCardDecisions.ThreatTier.High)]
    [InlineData(0.9, LevelCardDecisions.ThreatTier.High)]
    public void ThreatTierOf_SplitsTheExistingThreatValueSet(
        double threat, LevelCardDecisions.ThreatTier expected)
    {
        Assert.Equal(expected, LevelCardDecisions.ThreatTierOf(threat));
    }

    [Fact]
    public void ThreatBadgeZh_HasThreeDistinctShortLabels()
    {
        string[] labels =
        {
            LevelCardDecisions.ThreatBadgeZh(LevelCardDecisions.ThreatTier.Low),
            LevelCardDecisions.ThreatBadgeZh(LevelCardDecisions.ThreatTier.Mid),
            LevelCardDecisions.ThreatBadgeZh(LevelCardDecisions.ThreatTier.High),
        };
        Assert.Equal(new[] { "威胁·低", "威胁·中", "威胁·高" }, labels);
        Assert.Equal(labels.Length, labels.Distinct().Count());
    }

    [Theory]
    [InlineData(0, "☆☆☆")]
    [InlineData(1, "★☆☆")]
    [InlineData(2, "★★☆")]
    [InlineData(3, "★★★")]
    [InlineData(5, "★★★")]   // 越界钳到上限
    [InlineData(-1, "☆☆☆")]  // 越界钳到下限
    public void StarsText_AlwaysThreeSlots_FilledPlusDim(int stars, string expected)
    {
        Assert.Equal(expected, LevelCardDecisions.StarsText(stars));
    }

    [Fact]
    public void ModeBannerStems_AreTheShippedFileNames()
    {
        Assert.Equal("banner-mode-endless", LevelCardDecisions.EndlessModeBannerStem);
        Assert.Equal("banner-mode-procedural", LevelCardDecisions.ProceduralModeBannerStem);
        Assert.Equal("game/assets/expand-v1/P1/banners", LevelCardDecisions.BannerDirRelative);
    }

    // ---- 真文件 29/29 映射验证（e1~e6.json ↔ banner 目录） --------------------

    [Fact]
    public void RepoBanners_CoverAllTwentyNineCampaignRowsAndBothModeCards()
    {
        string bannerDir = FindRepoDir(LevelCardDecisions.BannerDirRelative);
        string levelsDir = FindRepoDir(System.IO.Path.Combine("game", "content", "levels"));
        string[] banners = Directory.GetFiles(bannerDir, "*.png")
            .Select(file => Path.GetFileName(file)!)
            .ToArray();
        Assert.True(banners.Length >= 31, $"banner 目录应 ≥31 张（29 关+2 模式卡），实得 {banners.Length}");

        var mapped = new List<string>();
        for (int era = 1; era <= 6; era++)
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(levelsDir, $"e{era}.json")));
            foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                string id = item.GetProperty("id").GetString()!;
                int slot = item.GetProperty("slot").GetInt32();
                string stem = LevelCardDecisions.LevelBannerStem(era, slot, LevelCardDecisions.IsFinalLevel(id));
                string? file = LevelCardDecisions.PickBannerFile(stem, banners);
                Assert.True(file is not null, $"{id}（{stem}）未命中 banner——缺图=接线错不是缺资产");
                mapped.Add($"{id}:{file}");
            }
        }

        Assert.Equal(29, mapped.Count); // e1=5、e2=4、e3~e6 各 5 → 29 全覆盖
        Assert.NotNull(LevelCardDecisions.PickBannerFile(LevelCardDecisions.EndlessModeBannerStem, banners));
        Assert.NotNull(LevelCardDecisions.PickBannerFile(LevelCardDecisions.ProceduralModeBannerStem, banners));
    }

    /// <summary>仓库相对目录 walk-up（UiContentFilesTests 同律——测试须在仓库内运行）.</summary>
    private static string FindRepoDir(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(dir.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到仓库相对目录 {relative}（测试须在仓库内运行）");
    }
}

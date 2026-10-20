using System.Text.Json;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// T-163 (W10 C 批/F-125) 战斗 HUD 图标化决策表测试——纯逻辑半边覆盖（G1 承载）：
//   id→文件名主干推导（冒号转横线）/res:// 路径三族+立绘/槽位快捷键序
//  （跳 R/C 零回退律）/era.json 资源行解析/boss 尾约定/预告条目集
//  +真文件 92/92 映射验证（era{1..6} 内容清单 ↔ textures/ui/icons 三目录
//   逐 id 命中——缺图=接线错，不是缺资产）。

// [F-125] [C-21] 类级关联：本测试类全部方法覆盖 T-163 战斗 HUD 图标化的纯决策面。
public sealed class HudIconDecisionsTests
{
    // ---- id → 文件名主干 / res:// 路径 ---------------------------------------

    [Theory]
    [InlineData("era1:tower-wood-arrow", "era1-tower-wood-arrow")]
    [InlineData("era3:tower-gatling", "era3-tower-gatling")]
    [InlineData("era1:enemy-boss", "era1-enemy-boss")]
    [InlineData("era4:res-oil", "era4-res-oil")]
    [InlineData("era6:res-dark-matter", "era6-res-dark-matter")]
    public void IconStemOf_ColonToDash_DerivationOnly(string id, string expectedStem)
    {
        Assert.Equal(expectedStem, HudIconDecisions.IconStemOf(id));
    }

    [Fact]
    public void IconPaths_RouteByKindIntoTheirIconSubdirectories()
    {
        Assert.Equal("res://textures/ui/icons/towers/era1-tower-wood-arrow.png",
            HudIconDecisions.TowerIconPath("era1:tower-wood-arrow"));
        Assert.Equal("res://textures/ui/icons/enemies/era1-enemy-boss.png",
            HudIconDecisions.EnemyIconPath("era1:enemy-boss"));
        Assert.Equal("res://textures/ui/icons/resources/era4-res-oil.png",
            HudIconDecisions.ResourceIconPath("era4:res-oil"));
    }

    [Theory]
    [InlineData(1, "res://textures/ui/portraits/era1-advisor.png")]
    [InlineData(6, "res://textures/ui/portraits/era6-advisor.png")]
    [InlineData(0, "res://textures/ui/portraits/era1-advisor.png")]  // 钳位下界
    [InlineData(9, "res://textures/ui/portraits/era6-advisor.png")]  // 钳位上界
    public void AdvisorPortraitPath_ClampsEraIntoTheSixEraRange(int era, string expected)
    {
        Assert.Equal(expected, HudIconDecisions.AdvisorPortraitPath(era));
    }

    // ---- 槽位快捷键序（跳 R/C——既有整行/整列快捷零回退律） --------------------

    [Fact]
    public void SlotHotkeys_StartQWE_ThenSkipTheTakenRowAndColumnKeys()
    {
        // 前三键=任务卡示意 Q/W/E；R（整行）与 C（整列）不在序列——既有语义零回退。
        Assert.Equal('Q', HudIconDecisions.HotkeyOfSlot(0));
        Assert.Equal('W', HudIconDecisions.HotkeyOfSlot(1));
        Assert.Equal('E', HudIconDecisions.HotkeyOfSlot(2));
        Assert.Equal('T', HudIconDecisions.HotkeyOfSlot(3)); // 第 4 槽跳过 R
        Assert.DoesNotContain('R', HudIconDecisions.SlotHotkeys);
        Assert.DoesNotContain('C', HudIconDecisions.SlotHotkeys);
        Assert.True(HudIconDecisions.SlotHotkeys.Length >= 5); // 最宽时代（era2 五塔）全覆盖
    }

    [Fact]
    public void HotkeyOfSlot_OutOfRangeSlotsHaveNoHotkey()
    {
        Assert.Equal('\0', HudIconDecisions.HotkeyOfSlot(-1));
        Assert.Equal('\0', HudIconDecisions.HotkeyOfSlot(HudIconDecisions.SlotHotkeys.Length));
    }

    // ---- era.json 资源行解析 ---------------------------------------------------

    [Fact]
    public void EraResources_ParsesIdAndNameZhInDataOrder()
    {
        const string json = """
            { "items": [ { "id": "era1:era-ancient", "resources": [
                { "id": "era1:res-wood", "name_zh": "木材", "name_en": "Wood" },
                { "id": "era1:res-stone", "name_zh": "石料" },
                { "id": "era1:res-food", "name_zh": "食物" } ] } ] }
            """;

        IReadOnlyList<(string Id, string NameZh)> rows =
            HudIconDecisions.EraResources(json, "era1/era.json");

        Assert.Equal(3, rows.Count);
        Assert.Equal(("era1:res-wood", "木材"), rows[0]); // 行序=数据序（别重排）
        Assert.Equal(("era1:res-food", "食物"), rows[2]);
    }

    [Fact]
    public void EraResources_NameZhMissingFallsBackToTheId()
    {
        const string json = """
            { "items": [ { "id": "era1:era-ancient", "resources": [
                { "id": "era1:res-wood" } ] } ] }
            """;

        Assert.Equal(("era1:res-wood", "era1:res-wood"),
            HudIconDecisions.EraResources(json, "era1/era.json")[0]);
    }

    [Fact]
    public void EraResources_MalformedShapesThrowWithSourceLabel()
    {
        Assert.Throws<ArgumentException>(() =>
            HudIconDecisions.EraResources("""{ "items": [] }""", "eraX/era.json"));
        Assert.Throws<ArgumentException>(() =>
            HudIconDecisions.EraResources("""{ "items": [ { "id": "x" } ] }""", "eraX/era.json"));
        Assert.Throws<ArgumentException>(() =>
            HudIconDecisions.EraResources(
                """{ "items": [ { "resources": [ { "name_zh": "无名" } ] } ] }""", "eraX/era.json"));
    }

    // ---- boss 尾约定 + 预告条目集 ----------------------------------------------

    [Theory]
    [InlineData("era1:enemy-boss", true)]
    [InlineData("era6:enemy-boss", true)]
    [InlineData("era1:enemy-beastling", false)]
    [InlineData("era4:enemy-bosk-rider", false)] // 前缀不算——只认 -boss 尾
    public void IsBossEnemyId_FollowsTheBossIdTailConvention(string id, bool expected)
    {
        Assert.Equal(expected, HudIconDecisions.IsBossEnemyId(id));
    }

    [Fact]
    public void WavePreviewEntries_KeepInputOrder_FlagBossAndDropEmptyRows()
    {
        var entries = HudIconDecisions.WavePreviewEntries(
            new[] { ("era1:enemy-beastling", 12), ("era1:enemy-boss", 0), ("era1:enemy-boss", 1) });

        Assert.Equal(2, entries.Count);
        Assert.Equal(new HudIconDecisions.WavePreviewEntry("era1:enemy-beastling", 12, IsBoss: false), entries[0]);
        Assert.Equal(new HudIconDecisions.WavePreviewEntry("era1:enemy-boss", 1, IsBoss: true), entries[1]);
    }

    // ---- 真文件 92/92 映射验证（内容清单 ↔ textures/ui/icons 三目录） ----------

    [Fact]
    public void RepoIcons_CoverEveryTowerEnemyAndResourceRowOfAllSixEras()
    {
        string iconsRoot = FindRepoDir(
            System.IO.Path.Combine("game", "presentation", "EraTD", "textures", "ui", "icons"));

        int towers = 0, enemies = 0, resources = 0;
        var seenResources = new HashSet<string>(StringComparer.Ordinal);
        for (int era = 1; era <= 6; era++)
        {
            string eraDir = FindRepoDir(System.IO.Path.Combine("game", "content", $"era{era}"));

            using (JsonDocument towersDoc = JsonDocument.Parse(
                       File.ReadAllText(Path.Combine(eraDir, "towers.json"))))
            {
                foreach (JsonElement item in towersDoc.RootElement.GetProperty("items").EnumerateArray())
                {
                    string id = item.GetProperty("id").GetString()!;
                    Assert.True(File.Exists(Combine(iconsRoot, HudIconDecisions.TowerIconPath(id))),
                        $"{id} 未命中塔 icon——缺图=接线错不是缺资产");
                    towers++;
                }
            }

            using (JsonDocument enemiesDoc = JsonDocument.Parse(
                       File.ReadAllText(Path.Combine(eraDir, "enemies.json"))))
            {
                foreach (JsonElement item in enemiesDoc.RootElement.GetProperty("items").EnumerateArray())
                {
                    string id = item.GetProperty("id").GetString()!;
                    Assert.True(File.Exists(Combine(iconsRoot, HudIconDecisions.EnemyIconPath(id))),
                        $"{id} 未命中敌 icon——缺图=接线错不是缺资产");
                    enemies++;
                }
            }

            foreach ((string id, _) in HudIconDecisions.EraResources(
                         File.ReadAllText(Path.Combine(eraDir, "era.json")), $"era{era}/era.json"))
            {
                Assert.True(File.Exists(Combine(iconsRoot, HudIconDecisions.ResourceIconPath(id))),
                    $"{id} 未命中资源 icon——缺图=接线错不是缺资产");
                seenResources.Add(id); // 资源跨时代去重后计数（era1~6 数据面共 17）
            }
        }

        resources = seenResources.Count;
        Assert.Equal(21, towers);  // 塔 21（3+5+4+4+3+2）
        Assert.Equal(54, enemies); // 敌 54
        Assert.Equal(17, resources); // 资源去重 17
    }

    /// <summary>res:// 路径 → 图标根下实文件（"res://textures/ui/icons/…" → 根内相对尾段）.</summary>
    private static string Combine(string iconsRoot, string resPath) =>
        Path.Combine(iconsRoot, resPath[("res://textures/ui/icons/".Length)..]
            .Replace('/', Path.DirectorySeparatorChar));

    /// <summary>仓库相对目录 walk-up（LevelCardDecisionsTests 同律——测试须在仓库内运行）.</summary>
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

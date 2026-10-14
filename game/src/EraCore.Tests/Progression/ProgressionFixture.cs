using EraCore.Contracts.Foundation;
using EraCore.Economy;

namespace EraCore.Tests.Progression;

// T-073 (C-19) 测试基建：campaign 内容目录寻址（RL9：禁盘符绝对路径，
// 同 GrammarTestPaths 手法）+ 可拨墙钟 Fake（CS-1.4：时间必经端口）+
// 内联最小科技树 JSON（引擎矩阵用，数值=结构旋钮非设计锚点）。

/// <summary>
/// Shared helpers of the campaign-progression suite: content-dir lookup
/// under game/content/campaign, a mutable wall-clock fake and the minimal
/// inline tech-tree documents the engine matrices run on (structural knobs
/// only — every design number the assertions check comes from the real
/// content files or the frozen formulas).
/// </summary>
internal static class ProgressionFixture
{
    public const long Seed = 20261014;

    public static readonly ResourceKey Wood = new("era1:res-wood");

    public static readonly ResourceKey Stone = new("era1:res-stone");

    public static readonly ResourceKey Food = new("era1:res-food");

    /// <summary>game/content/campaign 目录（techtree/objectives/chests/signin）。</summary>
    public static string CampaignContentDir()
        => FindContentDir(Path.Combine("game", "content", "campaign"), "game/content/campaign");

    /// <summary>读取 campaign 内容目录下的一个文件全文。</summary>
    public static string ReadCampaignContent(string fileName)
        => File.ReadAllText(Path.Combine(CampaignContentDir(), fileName));

    private static string FindContentDir(string relative, string label)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 {label}（测试须在仓库内运行）");
    }

    /// <summary>
    /// 最小合法科技树（引擎矩阵）：e1-root（无闸门）→ e1-mid（三重闸门全挂：
    /// 拓扑前置 root + 材料 石 40 + 关卡绑定 level-x）+ e1-sys（系统能力独立
    /// 旁支根，F-052）。Σ成本 30/60/25 为结构旋钮。
    /// </summary>
    public const string MinimalTechTree =
        """
        {
          "content_type": "Tech",
          "items": [
            {
              "id": "t:root",
              "era_index": 1,
              "branch": "main",
              "kind": "tower_unlock",
              "prerequisites": [],
              "materials": [{ "resource": "era1:res-wood", "amount": 20 }],
              "bound_levels": [],
              "cost_sum": 30,
              "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }],
              "unlocks": ["era1:tower-wood-arrow"]
            },
            {
              "id": "t:mid",
              "era_index": 1,
              "branch": "main",
              "kind": "tower_unlock",
              "prerequisites": ["t:root"],
              "materials": [{ "resource": "era1:res-stone", "amount": 40 }],
              "bound_levels": ["campaign:level-x"],
              "cost_sum": 60,
              "cost_mix": [{ "resource": "era1:res-stone", "share": 1.0 }],
              "unlocks": ["era1:tower-rockfall"]
            },
            {
              "id": "t:sys",
              "era_index": 1,
              "branch": "system",
              "kind": "system_upgrade",
              "prerequisites": [],
              "materials": [{ "resource": "era1:res-food", "amount": 15 }],
              "bound_levels": [],
              "cost_sum": 25,
              "cost_mix": [{ "resource": "era1:res-food", "share": 1.0 }],
              "unlocks": ["campaign:capability-x"]
            },
            {
              "id": "t:future",
              "era_index": 2,
              "branch": "main",
              "kind": "scroll_unlock",
              "prerequisites": [],
              "materials": [{ "resource": "era1:res-wood", "amount": 10 }],
              "bound_levels": [],
              "cost_sum": 35,
              "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }],
              "unlocks": ["era2:scroll-x"]
            }
          ]
        }
        """;

    /// <summary>最小合法宝箱表：三稀有度+四档位+三件装饰物品（结构旋钮）。</summary>
    public const string MinimalChests =
        """
        {
          "content_type": "DropTable",
          "rarity_weights": { "common": 60, "rare": 30, "legendary": 10 },
          "tiers": [
            { "tier": "bronze", "item_rolls": 1, "bonus_rolls": 0 },
            { "tier": "silver", "item_rolls": 2, "bonus_rolls": 0 },
            { "tier": "gold", "item_rolls": 3, "bonus_rolls": 0 },
            { "tier": "gold_extra", "item_rolls": 3, "bonus_rolls": 1 }
          ],
          "items": [
            { "id": "c:common-1", "rarity": "common", "reward_class": "decoration" },
            { "id": "c:common-2", "rarity": "common", "reward_class": "presentation" },
            { "id": "c:rare-1", "rarity": "rare", "reward_class": "title" },
            { "id": "c:legend-1", "rarity": "legendary", "reward_class": "voice" }
          ]
        }
        """;

    /// <summary>最小合法签到/离线表：G.4 冻结值 + 单里程碑（结构旋钮）。</summary>
    public const string MinimalSignIn =
        """
        {
          "content_type": "DropTable",
          "offline": {
            "full_rate_hours": 4,
            "diminished_rate": 0.25,
            "equivalent_cap_hours": 8,
            "catchup_turn_cap": 50,
            "turns_per_hour": 1,
            "invasion_chance_per_turn": 1.0,
            "income": [{ "resource": "era1:res-wood", "per_hour": 12.0 }]
          },
          "milestones": [
            { "day": 3, "reward": { "id": "c:streak-3", "reward_class": "decoration" } }
          ]
        }
        """;

    /// <summary>可拨墙钟（CS-1.4 端口 Fake：离线岁月/签到时间源）。</summary>
    public sealed class MutableWallClock : IClock
    {
        public long Tick { get; set; }

        public long NowMs { get; set; }
    }

    /// <summary>A fresh E1 pool at warehouse level 1 over a mutable clock.</summary>
    public static ResourceStock FreshPool(MutableWallClock clock) =>
        new(clock, ResourceStock.E1WarehouseLevel);
}

namespace EraCore.Presentation;

/// <summary>
/// T-162 (W10 B 批/F-005) 选关卡片决策表——纯逻辑半边（表现层
/// CampaignMenuUi/EndlessMenuUi/ProceduralMenuUi 消费）。任务卡口径：
/// <list type="bullet">
/// <item>banner 映射按「时代+序号」确定性推导（e1 json 第 1 行↔l1、
/// final 行↔final）——文件名尾部的 slug 只是装饰，<b>不参与映射</b>；
/// 表现层拿 <see cref="LevelBannerStem"/> 前缀在 banner 目录清单里挑
/// 唯一文件（<see cref="PickBannerFile"/>）；</item>
/// <item>威胁角标=关卡既有 <c>threat</c> 字段（e1~e6.json 数据行，别不
/// 新造口径）三档呈现（<see cref="ThreatTierOf"/>）；</item>
/// <item>星级=战役进度既有三重目标口径（★=通过数，0~3——
/// <see cref="StarsText"/> 只做呈现归一）。</item>
/// </list>
/// </summary>
public static class LevelCardDecisions
{
    /// <summary>
    /// banner 目录（仓库/发布包内相对根）：<c>game/assets/expand-v1/P1/banners</c>
    /// （73e1219 入库的 29 关全覆盖+2 模式卡，768×512；build 脚本随包）。
    /// </summary>
    public const string BannerDirRelative = "game/assets/expand-v1/P1/banners";

    /// <summary>无尽模式卡 banner 文件名主干（banner-mode-endless.png）.</summary>
    public const string EndlessModeBannerStem = "banner-mode-endless";

    /// <summary>程序关模式卡 banner 文件名主干（banner-mode-procedural.png）.</summary>
    public const string ProceduralModeBannerStem = "banner-mode-procedural";

    /// <summary>终关 id 后缀（campaign:level-e{N}-final——终关判定只看 id 约定，不看 slug）.</summary>
    public const string FinalLevelIdSuffix = "-final";

    /// <summary>每关三重目标星数上限（★ 槽位数）.</summary>
    public const int StarsPerLevel = 3;

    /// <summary>终关判定：id 以 <c>-final</c> 结尾（既有 id 约定，映射确定性输入之一）.</summary>
    public static bool IsFinalLevel(string levelId) =>
        levelId.EndsWith(FinalLevelIdSuffix, StringComparison.Ordinal);

    /// <summary>
    /// 一关的 banner 文件名主干（映射=时代+序号确定性推导，slug 不参与）：
    /// 非终关=<c>banner-e{时代}-l{序}-</c>；终关=<c>banner-e{时代}-final-</c>。
    /// eraIndex/槽位非法（&lt;1）时钳到 1——缺图由表现层降级（深色垫底），不抛。
    /// </summary>
    public static string LevelBannerStem(int eraIndex, int slot, bool isFinal)
    {
        int era = Math.Max(1, eraIndex);
        return isFinal
            ? $"banner-e{era}-final-"
            : $"banner-e{era}-l{Math.Max(1, slot)}-";
    }

    /// <summary>
    /// 章节通栏题图主干=该时代终关 banner（章节主视觉复用终关图——任务卡
    /// 「用该时代终关 banner 横裁或整幅」口径的整幅侧）。
    /// </summary>
    public static string ChapterBannerStem(int eraIndex) => LevelBannerStem(eraIndex, slot: 0, isFinal: true);

    /// <summary>
    /// 从 banner 目录文件名清单里挑主干匹配的唯一文件：按主干前缀+.png 过滤、
    /// 序号序取首个（同名主干多文件时确定性地取字典序最小——slug 冲突属
    /// 素材侧冗余，不阻断选关）；无匹配返回 null（缺图降级）。
    /// </summary>
    public static string? PickBannerFile(string stem, IEnumerable<string> fileNames)
    {
        if (string.IsNullOrEmpty(stem))
        {
            return null;
        }

        string? best = null;
        foreach (string name in fileNames)
        {
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(stem, StringComparison.Ordinal))
            {
                continue;
            }

            if (best is null || string.CompareOrdinal(name, best) < 0)
            {
                best = name;
            }
        }

        return best;
    }

    /// <summary>威胁角标三档（数据源=关卡既有 threat 字段的呈现分档）.</summary>
    public enum ThreatTier
    {
        /// <summary>低威胁（threat &lt; 0.45——教学/起步关）.</summary>
        Low,

        /// <summary>中威胁（0.45 ≤ threat &lt; 0.75）.</summary>
        Mid,

        /// <summary>高威胁（threat ≥ 0.75——压轴/终局关）.</summary>
        High,
    }

    /// <summary>
    /// 威胁分档：&lt;0.45 低 / &lt;0.75 中 / ≥0.75 高（既有 threat 定值集
    /// 0.3/0.45/0.6/0.75/0.9 的三档切分——低=起步、中=推进、高=压轴）。
    /// </summary>
    public static ThreatTier ThreatTierOf(double threat) =>
        threat >= 0.75 ? ThreatTier.High : threat >= 0.45 ? ThreatTier.Mid : ThreatTier.Low;

    /// <summary>威胁角标文案（三档中文短标）.</summary>
    public static string ThreatBadgeZh(ThreatTier tier) => tier switch
    {
        ThreatTier.High => "威胁·高",
        ThreatTier.Mid => "威胁·中",
        _ => "威胁·低",
    };

    /// <summary>
    /// 星槽文案：已得星 ★ + 未得星 ☆，恒 <see cref="StarsPerLevel"/> 槽
    /// （未通关也显示空槽——卡片视觉网格稳定）；星数钳到 [0, 上限]。
    /// </summary>
    public static string StarsText(int stars)
    {
        int filled = Math.Clamp(stars, 0, StarsPerLevel);
        return new string('★', filled) + new string('☆', StarsPerLevel - filled);
    }
}

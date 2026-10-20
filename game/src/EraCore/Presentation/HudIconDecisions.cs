using System.Text.Json;

namespace EraCore.Presentation;

/// <summary>
/// T-163 (W10 C 批/F-125) 战斗 HUD 图标化决策表——纯逻辑半边（表现层
/// BattleHudUi/BuildInteractionUi/SystemPanel 消费）。任务卡口径：
/// <list type="bullet">
/// <item>图标文件名=<b>内容 id 冒号转横线</b>的确定性推导（
/// <c>era1:tower-wood-arrow</c> → <c>era1-tower-wood-arrow.png</c>），
/// 禁散装映射表（T3 纪律）——清单权威源=game/content/era{1..6}/
/// {era,towers,enemies}.json（塔 21/敌 54/资源去重 17，92 全对齐）；</item>
/// <item>图标在 Godot 项目内=<c>res://</c> 装载轨（与 banner 的包外松散轨
/// 不同）——缺图由表现层优雅回退（类色色块/程序头像），不阻断；</item>
/// <item>建造栏快捷键按槽位序分配，但<b>跳过既有语义键</b>（R=整行快捷、
/// C=整列快捷、空格/1-2-3=暂停变速、ESC=取消——既有语义零回退律，
/// BuildInteractionUi._UnhandledKeyInput 冻结面）。</item>
/// </list>
/// </summary>
public static class HudIconDecisions
{
    /// <summary>
    /// HUD 图标根（res:// 项目内轨；73e1219 入库 128×128 PNG，
    /// .import 属 gitignored 本地件——首次构建前 godot --headless --import 补齐）。
    /// </summary>
    public const string IconRoot = "textures/ui/icons";

    /// <summary>六时代军师立绘目录（res:// 项目内轨，256×256）.</summary>
    public const string PortraitDir = "textures/ui/portraits";

    /// <summary>顶部资源条 icon 档位（px，任务卡「icon 48px 档位」）.</summary>
    public const int ResourceIconPx = 48; // ci-allow: R7 T-163 表现层排版档位（icon px，非平衡数值）

    /// <summary>建造栏塔 icon 档位（px；按钮 72×64 内 icon+价+角标三件）.</summary>
    public const int BuildIconPx = 40; // ci-allow: R7 T-163 表现层排版档位（icon px，非平衡数值）

    /// <summary>波次预告横幅敌 icon 档位（px；icon×数量徽记行）.</summary>
    public const int EnemyIconPx = 32; // ci-allow: R7 T-163 表现层排版档位（icon px，非平衡数值）

    /// <summary>军师立绘档位（px；256×256 立绘在军师面板 report 行的展示位）.</summary>
    public const int AdvisorPortraitPx = 36; // ci-allow: R7 T-163 表现层排版档位（立绘展示 px，非平衡数值）

    /// <summary>
    /// 建造栏槽位快捷键序列（按槽位序；跳 R/C——既有整行/整列快捷零回退）：
    /// 前三键 Q/W/E 与任务卡示意一致，第 4 槽起跳过 R 用 T，以此类推；
    /// 超出序列的槽位无快捷键（'\0'——表现层不渲染角标、不绑键）。
    /// </summary>
    public const string SlotHotkeys = "QWETASDGFZXB";

    /// <summary>内容 id → 图标文件名主干（冒号转横线——T3 确定性推导）.</summary>
    public static string IconStemOf(string contentId) => contentId.Replace(':', '-');

    /// <summary>塔内容 id → res:// 图标路径（towers 子目录）.</summary>
    public static string TowerIconPath(string towerId) =>
        $"res://{IconRoot}/towers/{IconStemOf(towerId)}.png";

    /// <summary>敌内容 id → res:// 图标路径（enemies 子目录）.</summary>
    public static string EnemyIconPath(string enemyId) =>
        $"res://{IconRoot}/enemies/{IconStemOf(enemyId)}.png";

    /// <summary>资源内容 id → res:// 图标路径（resources 子目录）.</summary>
    public static string ResourceIconPath(string resourceId) =>
        $"res://{IconRoot}/resources/{IconStemOf(resourceId)}.png";

    /// <summary>
    /// 时代序 → 军师立绘 res:// 路径（era{N}-advisor.png；钳到 1..6——
    /// 缺图由表现层回落程序头像，落地后随重绑自动出现）。
    /// </summary>
    public static string AdvisorPortraitPath(int eraIndex) =>
        $"res://{PortraitDir}/era{Math.Clamp(eraIndex, 1, 6)}-advisor.png";

    /// <summary>
    /// 槽位序号（0 起）→ 快捷键字符；越界返回 '\0'（无快捷键）。
    /// </summary>
    public static char HotkeyOfSlot(int slot) =>
        slot >= 0 && slot < SlotHotkeys.Length ? SlotHotkeys[slot] : '\0';

    /// <summary>
    /// boss 敌判定（内容 id 约定 <c>-boss</c> 尾——era1~era6 六行
    /// <c>era{N}:enemy-boss</c> 全对齐；预告横幅 boss 波特殊描边的数据位）。
    /// </summary>
    public static bool IsBossEnemyId(string enemyId) =>
        enemyId.EndsWith("-boss", StringComparison.Ordinal);

    /// <summary>
    /// era.json 时代资源行（顶部资源条的渲染清单权威源）：解析
    /// items[0].resources 的 (id, name_zh) 对；行序=数据序（时代资源面
    /// 的既有冻结序，别按字母重排）。数量由表现层从 live ResourceStock 读。
    /// </summary>
    /// <param name="eraJson">era.json 全文（内容侧单一数据源）.</param>
    /// <param name="sourceLabel">来源标签（异常消息定位用，如 "era2/era.json"）.</param>
    /// <exception cref="ArgumentException">JSON 结构缺失 resources 行.</exception>
    public static IReadOnlyList<(string Id, string NameZh)> EraResources(string eraJson, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(eraJson);
        using JsonDocument doc = JsonDocument.Parse(eraJson);
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items)
            || items.GetArrayLength() == 0
            || !items[0].TryGetProperty("resources", out JsonElement resources))
        {
            throw new ArgumentException($"era.json 缺 items[0].resources 行：{sourceLabel}", nameof(eraJson));
        }

        var rows = new List<(string, string)>();
        foreach (JsonElement row in resources.EnumerateArray())
        {
            if (!row.TryGetProperty("id", out JsonElement idElement)
                || idElement.GetString() is not { Length: > 0 } id)
            {
                throw new ArgumentException($"era.json resources 行缺 id：{sourceLabel}", nameof(eraJson));
            }

            rows.Add((id, row.TryGetProperty("name_zh", out JsonElement name) ? name.GetString() ?? id : id));
        }

        return rows;
    }

    /// <summary>波次预告横幅一行条目（敌 id × 数量徽记 + boss 标志）.</summary>
    /// <param name="EnemyId">敌内容 id（icon+tooltip 的确定性输入）.</param>
    /// <param name="Count">该类数量（徽记数字）.</param>
    /// <param name="IsBoss">boss 类（横幅/条目特殊描边的数据位）.</param>
    public sealed record WavePreviewEntry(string EnemyId, int Count, bool IsBoss);

    /// <summary>
    /// 波次预告横幅条目集（每类 icon×数量徽记）：行序=输入序（
    /// <see cref="WaveComposition"/> 的 species-id 升序冻结律），boss 类
    /// 由 <see cref="IsBossEnemyId"/> 推导。数量 ≤0 的行剔除（空行不占槽）。
    /// </summary>
    public static IReadOnlyList<WavePreviewEntry> WavePreviewEntries(
        IEnumerable<(string EnemyId, int Count)> waveRows)
    {
        ArgumentNullException.ThrowIfNull(waveRows);
        return waveRows
            .Where(row => row.Count > 0)
            .Select(row => new WavePreviewEntry(row.EnemyId, row.Count, IsBossEnemyId(row.EnemyId)))
            .ToList();
    }
}

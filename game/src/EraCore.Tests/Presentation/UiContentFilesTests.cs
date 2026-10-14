using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-110] [F-117] [F-124] T-093 UI 内容文件回归：game/content/ui/
// 三件真文件（glossary/hints/zones）过各自严格装载器 + 互链完备（每个 hint
// term 在词汇表可解析）+ 分区与令簿 lane 锚同源互证（zones.east_gate ==
// SimHost.EastGateAnchor 等运行时锚——此处钉格值，Godot 冒烟侧做运行时互证）。
// 期望值出处：zones.json _source 列（SimHost T-053 锚点）；损坏即红。
public class UiContentFilesTests
{
    private static string FindUiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "ui");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/ui（测试须在仓库内运行）");
    }

    private static string ReadUi(string fileName) =>
        File.ReadAllText(Path.Combine(FindUiDir(), fileName));

    // ---- legal -----------------------------------------------------------

    [Fact] // legal — 词汇表真文件：≥10 词条、四术语（威胁度/系统点/信任度/效率）在册
    public void Real_glossary_loads_with_the_four_core_terms()
    {
        Glossary glossary = Glossary.Parse(ReadUi("glossary.json"), "ui/glossary.json");

        Assert.True(glossary.Count >= 10, $"glossary too thin: {glossary.Count}");
        foreach (string id in new[] { "threat", "system_points", "trust", "efficiency" })
        {
            Assert.True(glossary.TryGet(id, out _), $"missing core term {id}");
        }

        Assert.Single(glossary.Search("威胁度"));
        Assert.Single(glossary.Search("trust"));
    }

    [Fact] // legal — 提示库真文件：谓词全覆盖（8 类谓词各有行）+ term 全可解析（互链完备）
    public void Real_hints_cover_every_predicate_and_link_terms_resolvably()
    {
        Glossary glossary = Glossary.Parse(ReadUi("glossary.json"), "ui/glossary.json");
        HintRulebook book = HintRulebook.Parse(ReadUi("hints.json"), "ui/hints.json", glossary);

        var covered = new HashSet<HintPredicateKind>(book.Rules.Select(rule => rule.Predicate));
        foreach (HintPredicateKind kind in Enum.GetValues<HintPredicateKind>())
        {
            Assert.True(covered.Contains(kind), $"predicate {kind} has no hint row");
        }

        // 因果型提示（F-110 第二类）至少两行且带 term 链
        Assert.Contains(book.Rules, rule => rule.Predicate == HintPredicateKind.FirstBottleneck && rule.Term is not null);
        Assert.Contains(book.Rules, rule => rule.Predicate == HintPredicateKind.LowIntegrity && rule.Term is not null);
    }

    [Fact] // legal — 分区真文件：四区在册，格值与令簿 lane 锚点同源（T-053 SimHost 锚）
    public void Real_zones_match_the_order_lane_anchors()
    {
        ZoneLabelTable table = ZoneLabelTable.Parse(ReadUi("zones.json"), "ui/zones.json", 16, 24);

        Assert.Equal(4, table.Count);
        Assert.Equal(new(12, 12), (table.TryResolve("east_gate")!.Cell.X, table.TryResolve("east_gate")!.Cell.Y));   // EastGateAnchor
        Assert.Equal(new(4, 8), (table.TryResolve("south_gate")!.Cell.X, table.TryResolve("south_gate")!.Cell.Y));    // SouthGateAnchor
        Assert.Equal(new(2, 14), (table.TryResolve("warehouse")!.Cell.X, table.TryResolve("warehouse")!.Cell.Y));     // WarehouseCell
        Assert.Equal(new(8, 23), (table.TryResolve("base_camp")!.Cell.X, table.TryResolve("base_camp")!.Cell.Y));     // BaseCell
        Assert.NotNull(table.TryResolve("东门")); // 令簿中文名可解析
    }

    // ---- boundary --------------------------------------------------------

    [Fact] // boundary — 全图窗=全部分区浮显（16×24 图，所有 cell 在窗内）
    public void Full_map_window_shows_every_zone()
    {
        ZoneLabelTable table = ZoneLabelTable.Parse(ReadUi("zones.json"), "ui/zones.json", 16, 24);

        Assert.Equal(table.Count, table.VisibleIn(0, 0, 15, 23).Count);
    }

    // ---- illegal ---------------------------------------------------------

    [Fact] // illegal — 故意违反应被拦截：真 zones 用错误地图边界装载（高×1）→ 越界格抛
    public void Wrong_map_bounds_reject_the_real_zone_cells()
    {
        Assert.Throws<ArgumentException>(
            () => ZoneLabelTable.Parse(ReadUi("zones.json"), "ui/zones.json", 16, 1));
    }
}

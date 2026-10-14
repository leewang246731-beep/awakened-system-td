using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;
using EraCore.Sim;
using EraCore.Tests.Sim;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// T-051 (C-03) 测试基建：K-1 端口捕获 Fake（ILogSink——CS-3.4 白名单）、
// 19 动作合法样例与越界变体构造器（K-3 冻结字段表口径）、真实 Simulation
// 视图夹具（复用 Sim 模块的 E1 关卡夹具，CS-3.4 禁 mock 被测系统）。

/// <summary>K-1 端口 Fake：按序捕获 ILogSink 条目供断言（CS-3.4 白名单端口）。</summary>
internal sealed class CapturingLogSink : ILogSink
{
    public sealed record Entry(LogLevel Level, long Tick, string Message);

    public List<Entry> Entries { get; } = new();

    public void Write(LogLevel level, long tick, string message) => Entries.Add(new Entry(level, tick, message));

    public string JoinedMessages => string.Join("\n", Entries.Select(e => e.Message));
}

/// <summary>
/// 19 动作合法载荷样例（K-3 冻结表口径；cells 数取 2 ⇒ 在 E1/E2 两种
/// 时代口径下均低于 4+2×era 上限）。供注册表/schema/拒绝矩阵复用。
/// 注意：载荷一律经本类构造——测试方法体内不出现字面 JSON 花括号
/// （T4 扫描器按花括号配对截取方法体，内嵌 JSON 会切断断言扫描）。
/// </summary>
internal static class ActionJsonSamples
{
    // —— 参数化变体（正反矩阵表驱动用；负参直接产出越界载荷） ——

    public static string SpawnScoutDuration(double duration) =>
        "{\"action\":\"SpawnScout\",\"lane\":\"lane-north\",\"duration\":" + duration.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

    public static string SetAlarmAfter(double seconds) =>
        "{\"action\":\"SetAlarm\",\"after_seconds\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"reason\":\"敌军逼近\"}";

    public static string RerouteRate(double rate) =>
        "{\"action\":\"RerouteLogistics\",\"from\":3,\"to\":4,\"item\":\"era1:res-wood\",\"rate\":" + rate.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

    public static string UpgradeTier(int tier) =>
        "{\"action\":\"UpgradeTower\",\"target_id\":5,\"to_tier\":" + tier + "}";

    public static string QueryHeadLimit(int limit) =>
        "{\"action\":\"QueryWorld\",\"key\":\"wave\",\"scope\":\"lane\",\"head_limit\":" + limit + "}";

    public static string InspectTileAspect(string aspect) =>
        "{\"action\":\"InspectTile\",\"cell\":{\"x\":1,\"y\":2},\"aspect\":\"" + aspect + "\"}";

    public static string EnumPayload(string field, string value) => field switch
    {
        "stance" => "{\"action\":\"SetRally\",\"lane\":\"lane\",\"stance\":\"" + value + "\"}",
        "mode" => "{\"action\":\"SetTowerMode\",\"target_id\":[5],\"mode\":\"" + value + "\"}",
        "order" => "{\"action\":\"HeroOrder\",\"order\":\"" + value + "\"}",
        "verdict" => ReportToPlayer(verdict: value),
        "aspect" => InspectTileAspect(value),
        _ => throw new ArgumentException($"未登记的字段 {field}", nameof(field)),
    };

    public static string BuildPlanCells(string cellsJson, bool confirmOverwrite = false) =>
        "{\"action\":\"BuildPlan\",\"tower_id\":\"era1:tower-wood-arrow\",\"cells\":" + cellsJson
        + (confirmOverwrite ? ",\"confirm_overwrite\":true" : string.Empty) + "}";

    public static string ReportFindings(int count) =>
        "{\"action\":\"ReportToPlayer\",\"summary\":\"ok\",\"findings\":["
        + string.Join(",", Enumerable.Range(0, count).Select(i => $"\"发现{i}\""))
        + "],\"verdict\":\"CONFIRMED\"}";

    /// <param name="label">显式给出时逐项使用原文（定长 label 断言用）；缺省为“选项”+序号。</param>
    public static string AskPlayerOptions(int count, string? label = null) =>
        "{\"action\":\"AskPlayer\",\"question\":\"q\",\"options\":["
        + string.Join(",", Enumerable.Range(0, count).Select(i => "{\"label\":\"" + (label ?? ("选项" + i)) + "\"}"))
        + "],\"multi_select\":false}";

    public static string SetTowerModeIds(int count) =>
        "{\"action\":\"SetTowerMode\",\"target_id\":["
        + string.Join(",", Enumerable.Range(1, count).Select(i => i.ToString()))
        + "],\"mode\":\"first\"}";

    /// <summary>多行（\n 转义、单 C# 行内花括号平衡）样例——行号式错误断言用。</summary>
    public static string MalformedMultiline() =>
        "{\n  \"action\": \"LookAround\",\n  \"oops\":\n";

    /// <summary>多行合法根+第 3 行越界 duration——指针+行号双精确断言用。</summary>
    public static string MultilineBadDuration() =>
        "{\n  \"action\": \"SpawnScout\",\n  \"duration\": 999.0, \"lane\": \"lane-north\"\n}";

    /// <summary>根级非法形态（数组/标量/空/截断等）。</summary>
    public static string RootKind(string kind) => kind switch
    {
        "array" => "[1,2]",
        "string" => "\"LookAround\"",
        "number" => "42",
        "empty" => string.Empty,
        "whitespace" => "   ",
        "truncated" => "{\"verb\":",
        _ => throw new ArgumentException($"未登记的根形态 {kind}", nameof(kind)),
    };

    /// <summary>缺判别字段的载荷（只带一个普通字段）。</summary>
    public static string TowerOnlyPayload() => "{\"tower_id\":\"era1:tower-wood-arrow\"}";

    /// <summary>从合法样例中移除一个字段后的载荷（缺失必填字段矩阵用）。</summary>
    public static string WithFieldRemoved(string verb, string field)
    {
        string payload = All()[verb]();
        int probe = payload.IndexOf("\"" + field + "\":", StringComparison.Ordinal);
        Assert.True(probe >= 0, $"{verb} 样例应含字段 {field}");
        int valueStart = payload.IndexOf(':', probe) + 1;
        while (payload[valueStart] == ' ')
        {
            valueStart++;
        }

        int valueEnd = ScanJsonScalarOrContainerEnd(payload, valueStart);
        int cutStart = payload[probe - 1] == ',' ? probe - 1 : probe;
        int cutEnd = payload[valueEnd] == ',' && cutStart == probe ? valueEnd + 1 : valueEnd;
        return payload.Remove(cutStart, cutEnd - cutStart);
    }

    /// <summary>返回从 <paramref name="start"/> 起的 JSON 值之后的首个下标（串/对象/数组/标量）。</summary>
    private static int ScanJsonScalarOrContainerEnd(string text, int start)
    {
        if (text[start] == '"')
        {
            int j = start + 1;
            while (text[j] != '"')
            {
                j++;
            }

            return j + 1;
        }

        if (text[start] is '{' or '[')
        {
            char open = text[start];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            for (int j = start; j < text.Length; j++)
            {
                if (text[j] == '"')
                {
                    j++;
                    while (text[j] != '"')
                    {
                        j++;
                    }
                }
                else if (text[j] == open)
                {
                    depth++;
                }
                else if (text[j] == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return j + 1;
                    }
                }
            }

            return text.Length;
        }

        int k = start;
        while (k < text.Length && text[k] != ',' && text[k] != '}')
        {
            k++;
        }

        return k;
    }

    /// <summary>类型错载荷（字段在、类型错）。</summary>
    public static string TypeMismatch(string kind) => kind switch
    {
        "duration-string" => "{\"action\":\"SpawnScout\",\"lane\":\"lane\",\"duration\":\"60\"}",
        "after-bool" => "{\"action\":\"SetAlarm\",\"after_seconds\":true,\"reason\":\"r\"}",
        "target-string" => "{\"action\":\"FocusFire\",\"target_id\":\"1000001\"}",
        "cells-object" => "{\"action\":\"BuildPlan\",\"tower_id\":\"era1:tower-wood-arrow\",\"cells\":{\"x\":1,\"y\":1}}",
        "target-fractional" => "{\"action\":\"UpgradeTower\",\"target_id\":5.5,\"to_tier\":2}",
        "aspect-number" => "{\"action\":\"InspectTile\",\"cell\":{\"x\":1,\"y\":2},\"aspect\":1}",
        _ => throw new ArgumentException($"未登记的类型错形态 {kind}", nameof(kind)),
    };

    public static string QueryWorld(string key) =>
        "{\"action\":\"QueryWorld\",\"key\":\"" + key + "\",\"scope\":\"lane-north\"}";

    /// <summary>SellTower 危险位变体：true / false / 缺失。</summary>
    public static string SellTowerDangerBit(string? confirmSell = "true") =>
        confirmSell is null
            ? "{\"action\":\"SellTower\",\"target_id\":5}"
            : "{\"action\":\"SellTower\",\"target_id\":5,\"confirm_sell\":" + confirmSell + "}";

    /// <summary>在合法样例上追加一个清单外字段（封闭 schema 拒绝矩阵用）。</summary>
    public static string WithSurpriseField(string verb) =>
        All()[verb]().TrimEnd('}') + ",\"surprise\":42}";

    public static string LookAround() => "{\"action\":\"LookAround\"}";

    public static string InspectTile() =>
        "{\"action\":\"InspectTile\",\"cell\":{\"x\":3,\"y\":4},\"aspect\":\"threat\"}";

    public static string QueryWorld() =>
        "{\"action\":\"QueryWorld\",\"key\":\"wave\",\"scope\":\"lane-north\",\"head_limit\":20}";

    public static string BuildPlan(int cellCount = 2, bool confirmOverwrite = false, string towerId = "era1:tower-wood-arrow") =>
        "{\"action\":\"BuildPlan\",\"tower_id\":\"" + towerId + "\",\"cells\":["
        + string.Join(",", Enumerable.Range(0, cellCount).Select(i => "{\"x\":" + (6 + i) + ",\"y\":21}"))
        + "]"
        + (confirmOverwrite ? ",\"confirm_overwrite\":true" : string.Empty) + "}";

    public static string UpgradeTower() =>
        "{\"action\":\"UpgradeTower\",\"target_id\":5,\"to_tier\":2}";

    public static string SellTower() =>
        "{\"action\":\"SellTower\",\"target_id\":5,\"confirm_sell\":true}";

    public static string SetRally() =>
        "{\"action\":\"SetRally\",\"lane\":\"lane-north\",\"stance\":\"hold\"}";

    public static string RerouteLogistics() =>
        "{\"action\":\"RerouteLogistics\",\"from\":3,\"to\":4,\"item\":\"era1:res-wood\",\"rate\":0.5}";

    public static string SetStockpile() =>
        "{\"action\":\"SetStockpile\",\"item_id\":\"era1:res-wood\",\"min_amount\":50.0}";

    public static string FocusFire() =>
        "{\"action\":\"FocusFire\",\"target_id\":1000001}";

    public static string SetTowerMode() =>
        "{\"action\":\"SetTowerMode\",\"target_id\":[5,7],\"mode\":\"first\"}";

    public static string TriggerScroll() =>
        "{\"action\":\"TriggerScroll\",\"scroll_id\":\"era1:scroll-arrow-rain\"}";

    public static string HeroOrder() =>
        "{\"action\":\"HeroOrder\",\"order\":\"move\"}";

    public static string AskPlayer() =>
        "{\"action\":\"AskPlayer\",\"question\":\"先补箭塔还是先修墙？\",\"options\":[{\"label\":\"补塔\"},{\"label\":\"修墙\"}],\"multi_select\":false}";

    public static string ReportToPlayer(string summary = "北门第一波已经入场，箭塔弹药充足。", string verdict = "CONFIRMED") =>
        "{\"action\":\"ReportToPlayer\",\"summary\":\"" + summary + "\",\"findings\":[\"北门出现 3 名敌兵\",\"箭塔余弹 20 发\"],\"verdict\":\"" + verdict + "\"}";

    public static string TodoBoard() =>
        "{\"action\":\"TodoBoard\",\"todos\":[\"北门补一座箭塔\",\"给西侧塔供弹\"]}";

    public static string SpawnScout() =>
        "{\"action\":\"SpawnScout\",\"lane\":\"lane-north\",\"duration\":60.0}";

    public static string SetAlarm() =>
        "{\"action\":\"SetAlarm\",\"after_seconds\":90.0,\"reason\":\"北门敌军逼近\"}";

    public static string StopTask() =>
        "{\"action\":\"StopTask\",\"task_id\":\"task-17\"}";

    /// <summary>合法样例按 19 动作名索引（schema 矩阵表驱动用）。</summary>
    public static IReadOnlyDictionary<string, Func<string>> All() => new Dictionary<string, Func<string>>
    {
        ["LookAround"] = LookAround,
        ["InspectTile"] = InspectTile,
        ["QueryWorld"] = QueryWorld,
        ["BuildPlan"] = () => BuildPlan(),
        ["UpgradeTower"] = UpgradeTower,
        ["SellTower"] = SellTower,
        ["SetRally"] = SetRally,
        ["RerouteLogistics"] = RerouteLogistics,
        ["SetStockpile"] = SetStockpile,
        ["FocusFire"] = FocusFire,
        ["SetTowerMode"] = SetTowerMode,
        ["TriggerScroll"] = TriggerScroll,
        ["HeroOrder"] = HeroOrder,
        ["AskPlayer"] = AskPlayer,
        ["ReportToPlayer"] = () => ReportToPlayer(),
        ["TodoBoard"] = TodoBoard,
        ["SpawnScout"] = SpawnScout,
        ["SetAlarm"] = SetAlarm,
        ["StopTask"] = StopTask,
    };

    /// <summary>The 15 verbs that stay in the deliberate not-implemented tier (HeroOrder went real in T-066).</summary>
    public static readonly IReadOnlyList<string> NotImplementedVerbs = new[]
    {
        "InspectTile", "QueryWorld", "UpgradeTower", "SellTower", "SetRally",
        "RerouteLogistics", "SetStockpile", "FocusFire", "SetTowerMode",
        "TriggerScroll", "AskPlayer", "TodoBoard", "SpawnScout",
        "SetAlarm", "StopTask",
    };
}

/// <summary>
/// 真实 E1 关卡上的注册表夹具（CS-3.4：被测系统禁 mock——全部走真
/// Simulation 公共视图）。stepToTick 先跑若干 tick 让命令落地/波次开局。
/// </summary>
internal static class RegistryFixture
{
    public static (Simulation Simulation, SimulationActionView View) Create(
        double waterRatio = 0.0,
        IReadOnlyList<KeyValuePair<ResourceKey, double>>? initialStock = null,
        bool runMilestone = false,
        long stepToTick = 0)
    {
        var config = SimE1Fixture.Config(waterRatio: waterRatio, initialStock: initialStock);
        var simulation = new Simulation(config, new SimRng(SimE1Fixture.Seed), SimE1Fixture.Seed);
        if (runMilestone)
        {
            simulation.EnqueueRange(SimE1Fixture.MilestoneCommands());
            simulation.Run(700);
        }
        else if (stepToTick > 0)
        {
            simulation.Run(stepToTick);
        }

        return (simulation, new SimulationActionView(simulation, config));
    }

    /// <summary>
    /// 扫描出 count 个真实可建格（seed 固定地形下按行序取前 n 格——测试不
    /// 依赖具体地形随机数落点，杜绝"碰巧是岩石格"的脆弱断言）。
    /// </summary>
    public static IReadOnlyList<EraCore.Contracts.Foundation.CellCoord> FreeCells(SimulationActionView view, int count)
    {
        var cells = new List<EraCore.Contracts.Foundation.CellCoord>(count);
        for (int y = 0; cells.Count < count && view.IsInsideGrid(new EraCore.Contracts.Foundation.CellCoord(0, y)); y++)
        {
            for (int x = 0; cells.Count < count && view.IsInsideGrid(new EraCore.Contracts.Foundation.CellCoord(x, y)); x++)
            {
                var cell = new EraCore.Contracts.Foundation.CellCoord(x, y);
                if (view.IsBuildable(cell, out _))
                {
                    cells.Add(cell);
                }
            }
        }

        if (cells.Count < count)
        {
            throw new InvalidOperationException($"可建格不足（需 {count} 得 {cells.Count}）");
        }

        return cells;
    }
}

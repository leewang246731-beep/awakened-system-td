using System.Text.Json;
using EraCore.Progression;
using Xunit;
using static EraCore.Tests.Progression.ProgressionFixture;

namespace EraCore.Tests.Progression;

// [F-050][F-051][F-052] 科技树内容数据（T-073）：techtree.json 锚点比对
// （24 节点/三 kind/两支系/塔节点 Σ成本=towers.json cost_total 逐值对齐/
// 系统能力旁支对主线零依赖）+ 装载器三行（合法/边界/非法）。[C-19][C-02]
// 2026-10-14 批 2/3（REV-20261014-wave3-round2 #2）：+4 守城智典卷轴节点
// （系统旁支），并锚定卷轴 research 获取引用 ↔ 科技节点 id 双向对齐。
public class TechTreeDataTests
{
    private static IReadOnlyList<TechNodeSpec> LoadReal() =>
        TechTreeData.Parse(ReadCampaignContent("techtree.json"));

    // ------------------------------------------------------- legal

    [Fact] // legal — 节点量：E1+E2 共 24 个（首发 20 + 批 2/3 卷轴典 4，任务卡 "~20" 量级）
    public void Real_tree_carries_the_first_release_node_count()
    {
        IReadOnlyList<TechNodeSpec> nodes = LoadReal();
        Assert.Equal(24, nodes.Count);
        Assert.Equal(10, nodes.Count(node => node.EraIndex == 1));
        Assert.Equal(14, nodes.Count(node => node.EraIndex == 2));
    }

    [Fact] // legal — 三类节点齐备：塔解锁/系统升级/卷轴解锁（任务卡口径）
    public void All_three_kinds_are_present()
    {
        IReadOnlyList<TechNodeSpec> nodes = LoadReal();
        Assert.Equal(9, nodes.Count(node => node.Kind == TechNodeKind.TowerUnlock));
        Assert.Equal(7, nodes.Count(node => node.Kind == TechNodeKind.SystemUpgrade));
        Assert.Equal(8, nodes.Count(node => node.Kind == TechNodeKind.ScrollUnlock));
    }

    [Fact] // legal — 塔解锁节点 Σ成本与 towers.json cost_total 逐值对齐（E1+E2 全 9 塔）
    public void Tower_node_cost_sums_match_towers_json_exactly()
    {
        var towerCosts = new Dictionary<string, long>();
        foreach (string era in new[] { "era1", "era2" })
        {
            var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(FindRepoRoot(), "game", "content", era, "towers.json")));
            foreach (JsonElement item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                towerCosts[item.GetProperty("id").GetString()!] =
                    item.GetProperty("cost_total").GetInt64();
            }
        }

        IReadOnlyList<TechNodeSpec> nodes = LoadReal();
        var towerNodes = nodes.Where(node => node.Kind == TechNodeKind.TowerUnlock).ToList();
        Assert.Equal(towerCosts.Count, towerNodes.Count); // 每座塔恰一个解锁节点
        foreach (TechNodeSpec node in towerNodes)
        {
            string towerId = Assert.Single(node.Unlocks);
            Assert.True(towerCosts.TryGetValue(towerId, out long cost), $"解锁目标 {towerId} 不在 towers.json");
            Assert.Equal(cost, node.CostSum); // Σ成本=塔 cost_total（v3.2 §7 n=该塔成本）
        }
    }

    [Fact] // legal — F-052 系统能力独立旁支：主线任何节点的传递前置都不含旁支节点
    public void System_branch_is_a_truly_independent_side_branch()
    {
        IReadOnlyList<TechNodeSpec> nodes = LoadReal();
        var byId = nodes.ToDictionary(node => node.Id, node => node);
        foreach (TechNodeSpec node in nodes.Where(node => node.Branch == TechBranch.Main))
        {
            foreach (string ancestor in TransitivePrerequisites(node.Id, byId))
            {
                Assert.NotEqual(TechBranch.System, byId[ancestor].Branch); // 主线不依赖旁支
            }
        }

        Assert.Contains(nodes, node => node.Branch == TechBranch.System); // 旁支确实存在
        Assert.Equal(9, nodes.Count(node => node.Branch == TechBranch.System)); // E1 三支+E2 六支（两能力支+四守城智典卷轴支）
    }

    [Fact] // legal — 三重闸门字段在数据中真实使用：前置/材料/关卡绑定各有节点挂载
    public void Triple_gate_fields_are_actively_used()
    {
        IReadOnlyList<TechNodeSpec> nodes = LoadReal();
        Assert.Contains(nodes, node => node.Prerequisites.Count > 0);
        Assert.True(nodes.All(node => node.Materials.Count > 0)); // 每节点都有材料依赖
        Assert.Contains(nodes, node => node.BoundLevelIds.Count > 0);
        Assert.Equal(1, nodes.Count(node => node.Id == "campaign:tech-e1-rockfall"
            && node.BoundLevelIds.Contains("campaign:level-e1-02")));
    }

    [Fact] // legal — 卷轴解锁节点对齐 scrolls 内容（T-071 era2 scrolls.json 实存 id）
    public void Scroll_unlocks_align_with_existing_scroll_content()
    {
        IReadOnlyList<TechNodeSpec> nodes = LoadReal();
        var scrollIds = new HashSet<string>();
        var scrollDoc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "game", "content", "scrolls", "era2.json")));
        foreach (JsonElement item in scrollDoc.RootElement.GetProperty("items").EnumerateArray())
        {
            scrollIds.Add(item.GetProperty("id").GetString()!);
        }

        var e2ScrollNodes = nodes.Where(node => node.Kind == TechNodeKind.ScrollUnlock && node.EraIndex == 2).ToList();
        Assert.Equal(6, e2ScrollNodes.Count);
        foreach (TechNodeSpec node in e2ScrollNodes)
        {
            Assert.Contains(Assert.Single(node.Unlocks), scrollIds);
        }
    }

    [Fact] // legal — 卷轴 research 获取引用 ↔ 科技节点 id 双向对齐（REV-20261014-wave3-round2 #2：
           // era2.json 曾前指 era2:tech-codex-i..iv 四个悬空节点名——T-073 科技树从未落同名节点；
           // 本断言锚定两侧，任何一侧改名/漏节点即红，杜绝该悬空类回归）
    public void Research_acquisition_refs_resolve_to_real_tech_nodes_both_ways()
    {
        IReadOnlyList<TechNodeSpec> nodes = LoadReal();
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();

        var scrollDoc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "game", "content", "scrolls", "era2.json")));
        foreach (JsonElement item in scrollDoc.RootElement.GetProperty("items").EnumerateArray())
        {
            JsonElement acquisition = item.GetProperty("acquisition");
            if (acquisition.GetProperty("kind").GetString() != "research")
            {
                continue; // boss_drop/era_gift 引用走各自内容域，不进科技树对齐
            }

            string refId = acquisition.GetProperty("ref").GetString()!;
            Assert.Contains(refId, nodeIds); // 引用侧：research ref 必须命中实存节点
        }

        // 节点侧：每个 E2 卷轴解锁节点的 unlocks 必须命中实存卷轴 id（含四典节点）
        var scrollIds = new HashSet<string>();
        foreach (JsonElement item in scrollDoc.RootElement.GetProperty("items").EnumerateArray())
        {
            scrollIds.Add(item.GetProperty("id").GetString()!);
        }

        foreach (TechNodeSpec node in nodes.Where(node => node.Kind == TechNodeKind.ScrollUnlock && node.EraIndex == 2))
        {
            string unlocked = Assert.Single(node.Unlocks);
            Assert.Contains(unlocked, scrollIds);
            if (node.Id.StartsWith("campaign:tech-sys-e2-codex-", StringComparison.Ordinal))
            {
                // 四典节点零前置（F-052 旁支根，不发明依赖链）
                Assert.Empty(node.Prerequisites);
            }
        }
    }

    // ------------------------------------------------------- boundary

    [Fact] // boundary — 空表：items 缺省=零节点，引擎构造侧另有非空闸
    public void Empty_items_parse_to_an_empty_table()
    {
        Assert.Empty(TechTreeData.Parse("""{ "content_type": "Tech", "items": [] }"""));
    }

    [Fact] // boundary — Σ成本 0 合法（公式落到基项 60+0）
    public void Zero_cost_sum_is_legal()
    {
        IReadOnlyList<TechNodeSpec> nodes = TechTreeData.Parse(
            """{ "items": [ { "id": "t:zero", "era_index": 1, "branch": "main", "kind": "system_upgrade", "prerequisites": [], "materials": [], "bound_levels": [], "cost_sum": 0, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }], "unlocks": [] } ] }""");
        Assert.Equal(0, nodes[0].CostSum);
    }

    // ------------------------------------------------------- illegal

    [Fact] // illegal — 重复 id / 未知前置 / 前置环 / 负 Σ成本 / 越界时代 / 非法材料量
    public void Structural_defects_are_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => TechTreeData.Parse(
            """{ "items": [ { "id": "t:a", "era_index": 1, "cost_sum": 1, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }] }, { "id": "t:a", "era_index": 1, "cost_sum": 1, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }] } ] }"""));

        Assert.ThrowsAny<ArgumentException>(() => TechTreeData.Parse(
            """{ "items": [ { "id": "t:a", "era_index": 1, "prerequisites": ["t:ghost"], "cost_sum": 1, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }] } ] }"""));

        Assert.ThrowsAny<ArgumentException>(() => TechTreeData.Parse(
            """{ "items": [ { "id": "t:a", "era_index": 1, "prerequisites": ["t:b"], "cost_sum": 1, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }] }, { "id": "t:b", "era_index": 1, "prerequisites": ["t:a"], "cost_sum": 1, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }] } ] }"""));

        Assert.ThrowsAny<ArgumentException>(() => TechTreeData.Parse(
            """{ "items": [ { "id": "t:a", "era_index": 1, "cost_sum": -5, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }] } ] }"""));

        Assert.ThrowsAny<ArgumentException>(() => TechTreeData.Parse(
            """{ "items": [ { "id": "t:a", "era_index": 7, "cost_sum": 1, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }] } ] }"""));

        Assert.ThrowsAny<ArgumentException>(() => TechTreeData.Parse(
            """{ "items": [ { "id": "t:a", "era_index": 1, "cost_sum": 1, "cost_mix": [{ "resource": "era1:res-wood", "share": 1.0 }], "materials": [ { "resource": "era1:res-wood", "amount": 0 } ] } ] }"""));
    }

    [Fact] // illegal — 损坏 JSON：JsonException 直抛（内容校验红）
    public void Damaged_json_fails_to_parse()
    {
        Assert.Throws<JsonException>(() => TechTreeData.Parse("""{ "items": [ { "id": """));
    }

    // ------------------------------------------------------- helpers

    private static IEnumerable<string> TransitivePrerequisites(
        string nodeId, IReadOnlyDictionary<string, TechNodeSpec> byId)
    {
        var visited = new HashSet<string>();
        var stack = new Stack<string>(byId[nodeId].Prerequisites);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;
            foreach (string next in byId[current].Prerequisites)
            {
                stack.Push(next);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "game", "content")))
        {
            dir = dir.Parent!;
        }

        return dir!.FullName;
    }
}

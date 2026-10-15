using System.Text.Json;
using EraCore.Contracts.Content;
using EraCore.Mods;
using Xunit;

namespace EraCore.Tests.Mods;

// [F-156][F-165] C-02 模组装载器全量矩阵（T-103）：目录扫描/依赖拓扑/命名空间/
// patch 覆盖/行级报错/多错误收集/装载序确定化/坏模组不崩继续装载。测试树全部
// 写入临时目录并 Dispose 清理（CS-3.8）；三行覆盖合法/边界/非法。

public sealed class ModSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "eratd-modtests-" + Guid.NewGuid().ToString("N"));

    public string ModsDir => Path.Combine(Root, "mods");

    public string WriteMod(string modId, string relativePath, string content)
    {
        string path = Path.Combine(ModsDir, modId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void WriteRootFile(string fileName, string content)
    {
        Directory.CreateDirectory(ModsDir);
        File.WriteAllText(Path.Combine(ModsDir, fileName), content);
    }

    public string WriteFirstParty(string fileName, string content)
    {
        string dir = Path.Combine(Root, "content");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试判定（CS-3.8 精神：尽力清理）
        }
    }
}

public class ModLoaderTests
{
    private const string ManifestHead = """
        {
          "mod_id": "{0}",
          "name_zh": "模组{0}",
          "name_en": "Mod {0}",
          "version": "1.0.0",
          "requires": [{1}]
        }
        """;

    private static string Manifest(string modId, params string[] requires) =>
        ManifestHead.Replace("{0}", modId).Replace("{1}", requires.Length == 0 ? "" : string.Join(", ", requires.Select(r => $"\"{r}\"")));

    private static string BuildingDoc(string itemId, string? patch = null, string dps = "15.0") =>
        """
        {
          "content_type": "Building",
          "items": [
            {
              "id": "__ID__",
              "content_type": "Building",__PATCH__
              "class": "pierce",
              "name_zh": "测试塔__ID__",
              "name_en": "Tower __ID__",
              "aliases": ["塔__ID__"],
              "tier": 1,
              "max_tier": 2,
              "cost_total": 45,
              "dps": __DPS__,
              "damage_per_shot": 15,
              "reload_ticks": 30,
              "range_cells": 4.5,
              "hp": 130,
              "ammo_per_shot": 0,
              "tier_growth": {
                "dps_multiplier_per_tier": 2.0,
                "cost_multiplier_per_tier": 3.0,
                "range_multiplier_per_tier": 1.25
              },
              "traits": {}
            }
          ]
        }
        """
        .Replace("__ID__", itemId)
        .Replace("__PATCH__", patch is null ? "" : $" \"patch\": {patch.ToLowerInvariant()},")
        .Replace("__DPS__", dps);

    private static readonly string[] NoFirstParty = Array.Empty<string>();

    // ---- legal: 新增 ------------------------------------------------------

    [Fact] // [F-165] 新增模式：裸 id 自动命名空间化 modid:contentid，注册表可查（外置 JSON 增生效）
    public void AddMode_BareIdNamespaced_ModItemReachableInRegistry()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("alpha", "mod.json", Manifest("alpha"));
        sandbox.WriteMod("alpha", "content/buildings.json", BuildingDoc("tower-alpha"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.True(result.Ok);
        Assert.Equal("alpha", Assert.Single(result.Packages).ModId);
        JsonContentItem item = (JsonContentItem)registry.Get(ContentType.Building, "alpha:tower-alpha");
        Assert.Equal("alpha", item.SourceModId);
        Assert.False(item.Patched);
        Assert.Equal("alpha:tower-alpha", registry.GetByName(ContentType.Building, "塔tower-alpha").Id);
    }

    [Fact] // [F-165] 单文件模组：mods 根下的 .json 自带 mod 头即可装载
    public void SingleFileMod_RootJson_Loads()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteRootFile("lone.json", """
            {
              "mod_id": "lone",
              "name_zh": "单体模组",
              "name_en": "Lone Mod",
              "version": "0.9.0",
              "content_type": "Building",
              "items": [
                { "id": "tower-lone", "content_type": "Building", "class": "support",
                  "name_zh": "孤塔", "name_en": "Lone Tower", "tier": 1, "max_tier": 1,
                  "cost_total": 40, "dps": 0, "damage_per_shot": 0, "reload_ticks": null,
                  "range_cells": 3.0, "hp": 90, "ammo_per_shot": 0,
                  "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
                  "traits": {} }
              ]
            }
            """);
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.True(result.Ok);
        Assert.Equal("lone", Assert.Single(result.Packages).ModId);
        Assert.Equal("lone:tower-lone", registry.Get(ContentType.Building, "lone:tower-lone").Id);
    }

    // ---- legal: patch 覆盖（外置改生效）-----------------------------------

    [Fact] // [F-165] patch 模式：patch:true 全名 id 覆盖本体内容——注册表返回模组版 + overlay 报告
    public void PatchMode_OverridesFirstPartyItem_RegistryReturnsModVersion()
    {
        using var sandbox = new ModSandbox();
        string firstParty = sandbox.WriteFirstParty("era1.json", BuildingDoc("era1:tower-wood", dps: "10.0"));
        sandbox.WriteMod("rebal", "mod.json", Manifest("rebal"));
        sandbox.WriteMod("rebal", "content/buildings.json", BuildingDoc("era1:tower-wood", patch: "true", dps: "12.0"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(new[] { firstParty }, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.True(result.Ok);
        JsonContentItem patched = (JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood");
        Assert.True(patched.Patched);
        Assert.Equal("rebal", patched.SourceModId);
        Assert.Equal(12.0, JsonDocument.Parse(patched.RawJson).RootElement.GetProperty("dps").GetDouble());

        ModOverlayReport report = Assert.Single(result.Reports);
        Assert.Equal("era1:tower-wood", report.TargetId);
        Assert.Equal("rebal", report.PatchModId);
        Assert.Null(report.SupersededPatchModId); // 目标是本体内容，无前序补丁被顶替
        Assert.Single(result.Items);              // 覆盖不复制条目
    }

    [Fact] // [F-165] 双补丁同目标：装载序（id 序）后到者胜，前序补丁记录于 SupersededPatchModId
    public void PatchMode_TwoPatchesSameTarget_LaterInLoadOrderWins()
    {
        using var sandbox = new ModSandbox();
        string firstParty = sandbox.WriteFirstParty("era1.json", BuildingDoc("era1:tower-wood", dps: "10.0"));
        // zz-patch 先建目录但 id 排序在后 → 后装载 → 胜出
        sandbox.WriteMod("zz-patch", "mod.json", Manifest("zz-patch"));
        sandbox.WriteMod("zz-patch", "content/buildings.json", BuildingDoc("era1:tower-wood", patch: "true", dps: "18.0"));
        sandbox.WriteMod("aa-patch", "mod.json", Manifest("aa-patch"));
        sandbox.WriteMod("aa-patch", "content/buildings.json", BuildingDoc("era1:tower-wood", patch: "true", dps: "11.0"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(new[] { firstParty }, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        JsonContentItem winner = (JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood");
        Assert.Equal("zz-patch", winner.SourceModId);
        Assert.Equal(18.0, JsonDocument.Parse(winner.RawJson).RootElement.GetProperty("dps").GetDouble());

        Assert.Equal(2, result.Reports.Count);
        Assert.Equal("zz-patch", result.Reports[1].PatchModId);
        Assert.Equal("aa-patch", result.Reports[1].SupersededPatchModId);
    }

    // ---- legal: 依赖拓扑 ---------------------------------------------------

    [Fact] // [F-165] 依赖拓扑：requires 决定装载序（被依赖者先装），与目录创建序无关
    public void Dependencies_TopologicalOrder_RegardlessOfDirectoryOrder()
    {
        using var sandbox = new ModSandbox();
        // 建目录顺序刻意倒置：c→b→a 依赖链，c 最先创建
        sandbox.WriteMod("mod-c", "mod.json", Manifest("mod-c", "mod-b"));
        sandbox.WriteMod("mod-c", "content/buildings.json", BuildingDoc("tower-c"));
        sandbox.WriteMod("mod-b", "mod.json", Manifest("mod-b", "mod-a"));
        sandbox.WriteMod("mod-b", "content/buildings.json", BuildingDoc("tower-b"));
        sandbox.WriteMod("mod-a", "mod.json", Manifest("mod-a"));
        sandbox.WriteMod("mod-a", "content/buildings.json", BuildingDoc("tower-a"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        Assert.True(result.Ok);
        Assert.Equal(new[] { "mod-a", "mod-b", "mod-c" }, result.Packages.Select(p => p.ModId).ToArray());
    }

    [Fact] // [F-165] 无依赖关系的模组按 id 序装载（装载序确定化）
    public void NoDependencies_IdSortedLoadOrder()
    {
        using var sandbox = new ModSandbox();
        foreach (string id in new[] { "zeta", "beta", "alpha" })
        {
            sandbox.WriteMod(id, "mod.json", Manifest(id));
            sandbox.WriteMod(id, "content/buildings.json", BuildingDoc("tower-" + id));
        }

        ModLoadResult result = new ModLoader().Compose(NoFirstParty, sandbox.ModsDir);

        Assert.Equal(new[] { "alpha", "beta", "zeta" }, result.Packages.Select(p => p.ModId).ToArray());
    }

    // ---- 确定性 -----------------------------------------------------------

    [Fact] // [F-165] 装载序确定化：同输入两次 Compose 的条目序与错误序逐项相等
    public void Determinism_TwoRunsProduceIdenticalSequences()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("alpha", "mod.json", Manifest("alpha"));
        sandbox.WriteMod("alpha", "content/buildings.json", BuildingDoc("tower-b2"));
        sandbox.WriteMod("alpha", "content/more.json", BuildingDoc("tower-a1"));
        sandbox.WriteMod("beta", "mod.json", Manifest("beta"));
        sandbox.WriteMod("beta", "content/buildings.json", BuildingDoc("tower-b9"));
        var loader = new ModLoader();

        ModLoadResult first = loader.Compose(NoFirstParty, sandbox.ModsDir);
        ModLoadResult second = loader.Compose(NoFirstParty, sandbox.ModsDir);

        Assert.Equal(first.Items.Select(i => (i.ContentType, i.Id)), second.Items.Select(i => (i.ContentType, i.Id)));
        Assert.Equal(first.Errors.Select(e => e.ToString()), second.Errors.Select(e => e.ToString()));
        Assert.Equal(first.Packages.Select(p => p.ModId), second.Packages.Select(p => p.ModId));
    }

    // ---- illegal: 冲突/命名空间/patch 目标 ----------------------------------

    [Fact] // [F-165] 重名冲突：同一模组内两条 item 解析到同一 full id（裸+显式自命名空间）
    //        → Duplicate 错误，后到项剔除，先到项保留，其余模组继续装载
    public void DuplicateId_SameResolvedId_ErrorAndFirstKept()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("alpha", "mod.json", Manifest("alpha"));
        string item = """
              {
                "id": "__ID__",
                "content_type": "Building",
                "class": "pierce",
                "name_zh": "塔__ID__",
                "name_en": "Tower",
                "tier": 1, "max_tier": 2, "cost_total": 45, "dps": 15.0, "damage_per_shot": 15,
                "reload_ticks": 30, "range_cells": 4.5, "hp": 130, "ammo_per_shot": 0,
                "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
                "traits": {}
              }
            """.Replace("__ID__", "PLACEHOLDER");
        string doc = "{\n  \"content_type\": \"Building\",\n  \"items\": [\n"
            + item.Replace("PLACEHOLDER", "tower-clash") + ",\n"
            + item.Replace("PLACEHOLDER", "alpha:tower-clash") + "\n  ]\n}";
        sandbox.WriteMod("alpha", "content/buildings.json", doc);
        sandbox.WriteMod("beta", "mod.json", Manifest("beta"));
        sandbox.WriteMod("beta", "content/buildings.json", BuildingDoc("tower-beta"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e =>
            e.Kind == ModErrorKind.Duplicate
            && e.ModId == "alpha"
            && e.File == "alpha/content/buildings.json"
            && e.Path == "/items/1/id"
            && e.Message.Contains("alpha:tower-clash"));
        Assert.Equal("alpha", ((JsonContentItem)registry.Get(ContentType.Building, "alpha:tower-clash")).SourceModId);
        Assert.Equal("beta", ((JsonContentItem)registry.Get(ContentType.Building, "beta:tower-beta")).SourceModId);
    }

    [Fact] // [F-165] 跨命名空间 id 无 patch → Namespace 错误（模组只能增于己方命名空间或 patch 既有内容）
    public void CrossNamespaceId_WithoutPatch_Rejected()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("beta", "mod.json", Manifest("beta"));
        sandbox.WriteMod("beta", "content/buildings.json", BuildingDoc("alpha:tower-x"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        ModLoadError error = Assert.Single(result.Errors);
        Assert.Equal(ModErrorKind.Namespace, error.Kind);
        Assert.Contains("foreign namespace 'alpha'", error.Message);
        Assert.Empty(result.Items);
    }

    [Fact] // [F-165] patch 目标不存在 → PatchTargetMissing 错误
    public void PatchTargetMissing_Rejected()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("beta", "mod.json", Manifest("beta"));
        sandbox.WriteMod("beta", "content/buildings.json", BuildingDoc("era1:tower-ghost", patch: "true"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        ModLoadError error = Assert.Single(result.Errors);
        Assert.Equal(ModErrorKind.PatchTargetMissing, error.Kind);
        Assert.Contains("era1:tower-ghost", error.Message);
        Assert.Empty(result.Items);
    }

    // ---- illegal: 依赖 ------------------------------------------------------

    [Fact] // [F-165] 缺依赖：报 Dependency 且级联（依赖失败者的依赖者也失败），无关模组照常装载
    public void MissingDependency_Cascades_UnrelatedModStillLoads()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("need-ghost", "mod.json", Manifest("need-ghost", "ghost"));
        sandbox.WriteMod("needs-needer", "mod.json", Manifest("needs-needer", "need-ghost"));
        sandbox.WriteMod("independent", "mod.json", Manifest("independent"));
        sandbox.WriteMod("independent", "content/buildings.json", BuildingDoc("tower-free"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        Assert.False(result.Ok);
        Assert.Equal(
            new[] { "independent" },
            result.Packages.Select(p => p.ModId).ToArray());
        Assert.Contains(result.Errors, e => e.ModId == "need-ghost" && e.Kind == ModErrorKind.Dependency && e.Message.Contains("'ghost'"));
        Assert.Contains(result.Errors, e => e.ModId == "needs-needer" && e.Kind == ModErrorKind.Dependency);
    }

    [Fact] // [F-165] 循环依赖：环上成员全部报 Cycle 并剔除，环外模组继续
    public void DependencyCycle_MembersDropped_OthersLoad()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("x-ring", "mod.json", Manifest("x-ring", "y-ring"));
        sandbox.WriteMod("y-ring", "mod.json", Manifest("y-ring", "x-ring"));
        sandbox.WriteMod("outside", "mod.json", Manifest("outside"));
        sandbox.WriteMod("outside", "content/buildings.json", BuildingDoc("tower-out"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        Assert.Equal(new[] { "outside" }, result.Packages.Select(p => p.ModId).ToArray());
        Assert.Equal(2, result.Errors.Count(e => e.Kind == ModErrorKind.Cycle));
        Assert.Contains(result.Errors, e => e.ModId == "x-ring" && e.Message.Contains("x-ring -> y-ring"));
    }

    // ---- illegal: schema/JSON 行级报错 ---------------------------------------

    [Fact] // [F-165] 坏 schema 行级报错：class 越闭枚举 → 错误含模组文件名 + 真实行号 + 指针
    public void BadSchema_ErrorCarriesFileAndTrueLineNumber()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("badmod", "mod.json", Manifest("badmod"));
        string doc = """
            {
              "content_type": "Building",
              "items": [
                {
                  "id": "tower-bad",
                  "content_type": "Building",
                  "class": "laser",
                  "name_zh": "坏塔",
                  "name_en": "Bad Tower",
                  "tier": 1,
                  "max_tier": 2,
                  "cost_total": 45,
                  "dps": 15.0,
                  "damage_per_shot": 15,
                  "reload_ticks": 30,
                  "range_cells": 4.5,
                  "hp": 130,
                  "ammo_per_shot": 0,
                  "tier_growth": { "dps_multiplier_per_tier": 2.0, "cost_multiplier_per_tier": 3.0, "range_multiplier_per_tier": 1.25 },
                  "traits": {}
                }
              ]
            }
            """;
        sandbox.WriteMod("badmod", "content/buildings.json", doc);
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        ModLoadError error = Assert.Single(result.Errors);
        Assert.Equal(ModErrorKind.Schema, error.Kind);
        Assert.Equal("badmod", error.ModId);
        Assert.Equal("badmod/content/buildings.json", error.File);
        Assert.Equal(7, error.Line); // "class": "laser" 行
        Assert.Equal("/items/0/class", error.Path);
        Assert.Empty(result.Items);
    }

    [Fact] // [F-165] 坏 JSON 行级报错：解析器行号（JsonErrorKind=Json）
    public void MalformedJson_ParserLineCited()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("broken", "mod.json", Manifest("broken"));
        sandbox.WriteMod("broken", "content/buildings.json", "{\n  \"content_type\": \"Building\",\n  \"items\": [ oops\n");
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        ModLoadError error = Assert.Single(result.Errors);
        Assert.Equal(ModErrorKind.Json, error.Kind);
        Assert.Equal("broken/content/buildings.json", error.File);
        Assert.Equal(3, error.Line);
    }

    [Fact] // [F-165] 多错误收集：一次 Compose 报出全部错误（不遇错即停），好模组照常装载
    public void MultipleErrors_AllCollected_GoodModsStillLoad()
    {
        using var sandbox = new ModSandbox();
        // badmod：两条坏 item（各一条 schema 错）
        sandbox.WriteMod("badmod", "mod.json", Manifest("badmod"));
        sandbox.WriteMod("badmod", "content/buildings.json", """
            {
              "content_type": "Building",
              "items": [
                { "id": "b1", "content_type": "Building", "class": "not-a-class", "name_zh": "一", "name_en": "One", "tier": 1, "max_tier": 1, "cost_total": 1, "dps": 1, "damage_per_shot": 1, "reload_ticks": 30, "range_cells": 1, "hp": 1 },
                { "id": "b2", "content_type": "Building", "class": "pierce", "name_zh": "二", "name_en": "Two", "tier": 9, "max_tier": 2, "cost_total": 1, "dps": 1, "damage_per_shot": 1, "reload_ticks": 30, "range_cells": 1, "hp": 1 }
              ]
            }
            """);
        // nomodjson：目录包缺 manifest
        sandbox.WriteMod("nomodjson", "content/buildings.json", "{}");
        // goodmod：正常
        sandbox.WriteMod("goodmod", "mod.json", Manifest("goodmod"));
        sandbox.WriteMod("goodmod", "content/buildings.json", BuildingDoc("tower-good"));
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.False(result.Ok);
        Assert.Equal(3, result.Errors.Count); // badmod 两条 item 错 + nomodjson manifest 错
        Assert.Contains(result.Errors, e => e.ModId == "badmod" && e.Path == "/items/0/class");
        Assert.Contains(result.Errors, e => e.ModId == "badmod" && e.Path == "/items/1/tier");
        Assert.Contains(result.Errors, e => e.Kind == ModErrorKind.Manifest && e.Message.Contains("nomodjson"));
        // badmod 的 manifest 合格 → 包保留装载（其坏 item 已剔除）；nomodjson 整包剔除
        Assert.Equal(new[] { "badmod", "goodmod" }, result.Packages.Select(p => p.ModId).ToArray());
        Assert.Null(registry.FindByName(ContentType.Building, "badmod:b1"));
        Assert.Null(registry.FindByName(ContentType.Building, "badmod:b2"));
        Assert.Equal("goodmod", ((JsonContentItem)registry.Get(ContentType.Building, "goodmod:tower-good")).SourceModId);
    }

    [Fact] // [F-165] DSL 未知 op：装载管线产出 Dsl 类错误（含行号），item 剔除
    public void UnknownDslOp_ItemRejectedWithLine()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("alpha", "mod.json", Manifest("alpha"));
        string doc = BuildingDoc("tower-x").Replace("\"traits\": {}", """
            "traits": {},
              "on_hit": [ { "op": "teleport_enemy" } ]
            """);
        sandbox.WriteMod("alpha", "content/buildings.json", doc);
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        ModLoadError error = Assert.Single(result.Errors);
        Assert.Equal(ModErrorKind.Dsl, error.Kind);
        Assert.True(error.Line > 0);
        Assert.Equal("/items/0/on_hit/0", error.Path);
    }

    [Fact] // [F-165] 重复 mod_id：后到包报 Duplicate 并剔除
    public void DuplicateModId_SecondPackageRejected()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("solo", "mod.json", Manifest("solo"));
        sandbox.WriteRootFile("solo.json", """
            {
              "mod_id": "solo",
              "name_zh": "同名", "name_en": "Clash", "version": "1.0.0",
              "content_type": "Building",
              "items": []
            }
            """);
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);

        Assert.Contains(result.Errors, e => e.Kind == ModErrorKind.Duplicate && e.Message.Contains("solo"));
        Assert.Equal(new[] { "solo" }, result.Packages.Select(p => p.ModId).ToArray());
    }

    // ---- 边界：卸载/干净重启 ----------------------------------------------

    [Fact] // [F-165] 卸载干净：空 mods 目录 → 零模组零错误；本体内容不受影响；新注册表实例无残留
    public void EmptyModsDir_CleanRestart_NoModItemsNoErrors()
    {
        using var sandbox = new ModSandbox();
        string firstParty = sandbox.WriteFirstParty("era1.json", BuildingDoc("era1:tower-wood", dps: "10.0"));
        Directory.CreateDirectory(sandbox.ModsDir); // 空目录=已卸载全部模组
        var loader = new ModLoader();

        ModLoadResult result = loader.Compose(new[] { firstParty }, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.True(result.Ok);
        Assert.Empty(result.Packages);
        Assert.Empty(result.Reports);
        Assert.False(((JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood")).Patched);
        Assert.Null(((JsonContentItem)registry.Get(ContentType.Building, "era1:tower-wood")).SourceModId);
        Assert.Null(registry.FindByName(ContentType.Building, "tower-alpha")); // 无任何模组残留
    }

    [Fact] // [F-165] mods 根目录不存在 = 同空目录（未安装过模组的全新装机）
    public void MissingModsDir_TreatedAsNoMods()
    {
        using var sandbox = new ModSandbox();

        ModLoadResult result = new ModLoader().Compose(NoFirstParty, Path.Combine(sandbox.Root, "no-such-dir"));

        Assert.True(result.Ok);
        Assert.Empty(result.Packages);
        Assert.Empty(result.Items);
    }

    [Fact] // [F-156] Apply 到重复注册表抛 InvalidOperationException（overlay 应在装载器收口——契约防线）
    public void Apply_Twice_ThrowsOnDuplicateRegister()
    {
        using var sandbox = new ModSandbox();
        sandbox.WriteMod("alpha", "mod.json", Manifest("alpha"));
        sandbox.WriteMod("alpha", "content/buildings.json", BuildingDoc("tower-a"));
        var loader = new ModLoader();
        ModLoadResult result = loader.Compose(NoFirstParty, sandbox.ModsDir);
        var registry = new ContentRegistry();
        loader.Apply(registry, result);

        Assert.Throws<InvalidOperationException>(() => loader.Apply(registry, result));
    }
}

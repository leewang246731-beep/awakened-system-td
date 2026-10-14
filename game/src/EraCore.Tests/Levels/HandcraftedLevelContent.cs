using System.Text.Json;
using EraCore.Combat;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Levels;

/// <summary>
/// Shared content fixture of the handcrafted-level suite (T-085): the era
/// tables the level rows reference (towers/enemies/heroes of both eras, the
/// SP-8 objective pools, the era warehouse levels and the T-079 E2 schedule
/// rows), all loaded repo-relative from game/content (RL9). Pure loader —
/// every number stays in the content tables (CS-2.3).
/// </summary>
internal static class HandcraftedLevelContent
{
    private static DirectoryInfo FindContentDir(string relativeUnderContent)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", relativeUnderContent);
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException($"未找到 game/content/{relativeUnderContent}（测试须在仓库内运行）");
    }

    private static string ReadText(params string[] relativeUnderContent)
    {
        string path = Path.Combine([FindContentDir(relativeUnderContent[0]).FullName, .. relativeUnderContent[1..]]);
        Assert.True(File.Exists(path), $"内容文件缺失：{string.Join('/', relativeUnderContent)}");
        return File.ReadAllText(path);
    }

    /// <summary>Tower specs of one era keyed by id (towers.json, K-2 pipeline).</summary>
    public static IReadOnlyDictionary<string, TowerSpec> Towers(string eraDir) =>
        TowerSpec.LoadCollection(ReadText(eraDir, "towers.json")).ToDictionary(t => t.Id);

    /// <summary>Enemy specs of one era keyed by id (enemies.json; ids from the document, specs via the production parser).</summary>
    public static IReadOnlyDictionary<string, EnemySpec> Enemies(string eraDir)
    {
        string json = ReadText(eraDir, "enemies.json");
        var catalog = EnemySpecCatalog.Parse(json);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToDictionary(id => id, id => catalog.Get(id));
    }

    /// <summary>Hero catalog of one era (heroes.json; era1 carries the cross-era heritage row too).</summary>
    public static HeroSpecCatalog Heroes(string eraDir) => HeroSpecCatalog.Parse(ReadText(eraDir, "heroes.json"));

    /// <summary>Warehouse level of one era (era.json 仓库等级， v3.2 §5).</summary>
    public static int WarehouseLevel(string eraDir)
    {
        using var doc = JsonDocument.Parse(ReadText(eraDir, "era.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("warehouse_level").GetInt32();
    }

    /// <summary>Resource ids of one era row (era.json resources — includes the legacy-refresh rows).</summary>
    public static IReadOnlyList<string> EraResources(string eraDir)
    {
        using var doc = JsonDocument.Parse(ReadText(eraDir, "era.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray().Single()
            .GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()!).ToList();
    }

    /// <summary>Whether a recipe id exists in one era's recipes.json (era2 carries the table).</summary>
    public static bool RecipeExists(string eraDir, string recipeId)
    {
        string path = Path.Combine(FindContentDir(eraDir).FullName, "recipes.json");
        if (!File.Exists(path))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Any(r => r.GetProperty("id").GetString() == recipeId);
    }

    /// <summary>
    /// Rebuilds the converter recipe payload JSON of one recipes.json row
    /// (inputs amount → items；output amount=1，v3.2 §5 一 craft 产 1 件) — the
    /// same shape the E2 fixture passes to the build-converter command.
    /// </summary>
    public static string ConverterRecipePayload(string eraDir, string recipeId)
    {
        using var doc = JsonDocument.Parse(ReadText(eraDir, "recipes.json"));
        JsonElement row = doc.RootElement.GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == recipeId);
        var inputs = row.GetProperty("inputs").EnumerateArray()
            .Select(i => $"{{\"resource\":\"{i.GetProperty("resource").GetString()}\",\"items\":{i.GetProperty("amount").GetInt32()}}}");
        return "{\"inputs\":[" + string.Join(",", inputs) + "],\"output\":\""
            + row.GetProperty("output").GetProperty("resource").GetString()
            + "\",\"craft_ticks\":" + row.GetProperty("craft_ticks").GetInt32() + "}";
    }

    /// <summary>SP-8 objective pools (campaign/objectives.json): (strategy ids, challenge ids).</summary>
    public static (IReadOnlyList<string> Strategy, IReadOnlyList<string> Challenge) ObjectivePools()
    {
        using var doc = JsonDocument.Parse(ReadText("campaign", "objectives.json"));
        var strategy = doc.RootElement.GetProperty("strategy_pool").EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()!).ToList();
        var challenge = doc.RootElement.GetProperty("challenge_pool").EnumerateArray()
            .Select(o => o.GetProperty("id").GetString()!).ToList();
        return (strategy, challenge);
    }

    /// <summary>Hard-flag lookup of the SP-8 challenge pool (SP-8b C1~C4 ★ hard rows).</summary>
    public static IReadOnlyDictionary<string, bool> ChallengeHardFlags()
    {
        using var doc = JsonDocument.Parse(ReadText("campaign", "objectives.json"));
        return doc.RootElement.GetProperty("challenge_pool").EnumerateArray()
            .ToDictionary(o => o.GetProperty("id").GetString()!, o => o.GetProperty("hard").GetBoolean());
    }

    /// <summary>
    /// The frozen T-079 E2 schedule rows (era2/levels.json) keyed by slot.
    /// JsonElements outlive the method: the document is NOT disposed here
    /// (small fixed file, GC-owned — the Era2ContentTests annotation
    /// convention; disposing would cut the returned elements off).
    /// </summary>
    public static IReadOnlyDictionary<int, JsonElement> Era2ScheduleRows()
    {
        var doc = JsonDocument.Parse(ReadText("era2", "levels.json"));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("slot").GetInt32());
    }

    /// <summary>The era directory segment of a level row ("era1"/"era2" from its era id).</summary>
    public static string EraDir(HandcraftedLevel level) => level.Era.StartsWith("era2:", StringComparison.Ordinal)
        ? "era2"
        : "era1";
}

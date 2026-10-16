using System.Text.Json;
using EraCore.Balance;
using Xunit;

namespace EraCore.Tests.Levels;

/// <summary>
/// Schema violation of one handcrafted level row (T-085, F-055): the loader
/// throws this with a field-pathed message — content defects surface as load
/// failures, never as silently-zeroed fields (same discipline as
/// <c>EnemySpecCatalog.Parse</c>).
/// </summary>
public sealed class HandcraftedLevelSchemaException : Exception
{
    public HandcraftedLevelSchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>One grid cell of a handcrafted map (K-4 cell domain).</summary>
public sealed record CellPoint(int X, int Y);

/// <summary>
/// Fixed map parameters of one handcrafted level (T-085): the seed pins the
/// terrain draw (forest layout — passable), water stays 0 and rock stays 0
/// for E1/E2 rows so every scripted build coordinate is buildable for ANY
/// seed draw (the E2E fixture determinism precedent). T-116 (F-025): E3 rows
/// may declare rock_ratio &gt; 0 — the 矿格 (rock slot) is the coal-deposit
/// proxy of the 资源地理稀缺 design (富煤矿只在特定关, T-115 §四); the fixed
/// seed keeps such a map deterministic, so the scripted coordinates stay
/// buildable for THE authored seed (the level's own buildability proof runs
/// against the real GridWorld draw — Era3HandcraftedLevelSetTests).
/// 手工关=确定性 seed+布局参数，非随机重掷.
/// </summary>
public sealed record HandcraftedMap(
    long Seed,
    int Width,
    int Height,
    double ForestRatio,
    double RockRatio,
    double WaterRatio,
    CellPoint BaseCell,
    IReadOnlyList<CellPoint> EntryCells,
    CellPoint WarehouseCell,
    CellPoint? HeroCell,
    IReadOnlyList<CellPoint> DigSites);

/// <summary>One starting-grant line (送资源): resource id + amount.</summary>
public sealed record StockLine(string Resource, double Amount);

/// <summary>
/// Wave table of one handcrafted level: the §6 参数表 knobs (基数/起/间隔/
/// 成长 + 批次 + 波谷) plus the regular roster and the independent elite
/// line (F-031). Every knob feeds <see cref="WaveFormula.Count"/>/
/// <see cref="Elite.Count"/> and the WaveSpawner unchanged.
/// </summary>
public sealed record HandcraftedWaveTable(
    int BaseCount,
    int BeginWave,
    int Spacing,
    double Scaling,
    int BatchesPerWave,
    double ValleySeconds,
    IReadOnlyList<string> RegularRoster,
    bool EliteLineEnabled,
    string? EliteEnemyId);

/// <summary>
/// One teaching command of the level's 命令流脚本: anchored to a WAVE (the
/// step executes in the valley before that wave; wave-1 steps run inside the
/// first-wave grace). Verbs are the E1 thin five plus the E2 upgrade and
/// hero orders (SimE1Fixture/SimE2Fixture milestone precedents). Entity
/// ordinals follow the Simulation convention: warehouse=1, every later build
/// numbered 2..N in script issue order.
/// </summary>
public sealed record ScriptStep(
    int Wave,
    string Verb,
    CellPoint? Cell,
    string? Tower,
    string? Resource,
    int? Seats,
    long? FromEntity,
    long? ToEntity,
    long? Entity,
    int? ToTier,
    string? CarrierTier,
    string? Order,
    string? ConverterRecipe);

/// <summary>三重目标实例 of one level: survival is structural (守至胜波), the strategy and challenge pools are SP-8 id references.</summary>
public sealed record LevelObjectives(IReadOnlyList<string> Strategy, IReadOnlyList<string> Challenge);

/// <summary>One 演出素材接入点 (cinematic hook trigger, §10.4): beat + optional wave.</summary>
public sealed record CinematicHook(string Id, string Beat, int? Wave);

/// <summary>Per-level unlock schedule row (mirrors era2/levels.json for E2 rows; empty for E1 rows).</summary>
public sealed record LevelUnlocks(IReadOnlyList<string> Actions, IReadOnlyList<string> Features);

/// <summary>
/// One handcrafted level row of <c>game/content/levels/{era}.json</c>
/// (T-085, F-055): threat/win-waves follow the frozen §7 formulas, the
/// grace multiplier the §6 bands (teaching ×3, otherwise ×1.5~2), and
/// <see cref="BudgetSeconds"/> is the design budget the SP-8b C1
/// (ClearTimeSeconds ≤80%×预算) threshold multiplies.
/// </summary>
public sealed record HandcraftedLevel(
    string Id,
    string Era,
    int Slot,
    string NameZh,
    string NameEn,
    bool TeachingLevel,
    bool AwakeningCinematic,
    double Threat,
    int WinWaves,
    double FirstWaveGraceMultiplier,
    int BudgetSeconds,
    int? AdjacentEnemyCamps,
    string? HeroId,
    string? Focus,
    string? ScheduleTier,
    HandcraftedMap Map,
    IReadOnlyList<StockLine> InitialStock,
    HandcraftedWaveTable Waves,
    IReadOnlyList<ScriptStep> CommandScript,
    LevelObjectives Objectives,
    IReadOnlyList<CinematicHook> CinematicHooks,
    LevelUnlocks Unlocks);

/// <summary>
/// Parsed content of one handcrafted-level file: the era it belongs to plus
/// its level rows (E1 batch = 4 rows, E2 batch = 3 rows, T-085 首批).
/// </summary>
public sealed record HandcraftedLevelSet(string Era, IReadOnlyList<HandcraftedLevel> Items)
{
    /// <summary>Level row by id; null when absent.</summary>
    public HandcraftedLevel? TryGet(string id) => Items.FirstOrDefault(l => l.Id == id);
}

/// <summary>
/// Test-side loader of the handcrafted level files (T-085): repo-relative
/// walk-up discovery (RL9), strict JsonDocument mapping into the records
/// above, and total validation — every structural rule of the T-085 schema
/// is enforced at load time (schema 装载即校验，违规即红). Formulas are
/// ALWAYS <see cref="Campaign"/> calls (CS-4.4: 禁固定值冒充计算).
/// </summary>
internal static class HandcraftedLevelData
{
    /// <summary>Closed verb vocabulary of the command script (E1 thin five + E2 upgrade/hero).</summary>
    public static readonly IReadOnlySet<string> ScriptVerbs = new HashSet<string>
    {
        "build-labor-camp", "build-harvester", "build-converter", "build-tower",
        "assign-carrier", "upgrade-tower", "hero-order",
    };

    /// <summary>
    /// Closed carrier_tier vocabulary of assign-carrier steps (T-116): the
    /// trip forms of the six-tier logistics ladder the verb accepts
    /// (TransportTiers — belt/teleporter are separate verbs, so they are not
    /// assignable here). E1/E2 rows used porter/cart; E3 introduces mine-rail
    /// (10 件/s, era.json logistics tier 2).
    /// </summary>
    public static readonly IReadOnlySet<string> CarrierTiers = new HashSet<string>
    {
        "porter", "cart", "mine-rail", "drone",
    };

    // ---- repo-relative discovery (RL9: walk up, no absolute paths) ------

    private static DirectoryInfo FindLevelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "levels");
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe);
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("未找到 game/content/levels（测试须在仓库内运行）");
    }

    /// <summary>Parses and validates one handcrafted level file by name under game/content/levels/.</summary>
    public static HandcraftedLevelSet Load(string fileName)
    {
        string path = Path.Combine(FindLevelsDir().FullName, fileName);
        Assert.True(File.Exists(path), $"手工关卡文件缺失：{fileName}");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses and validates a handcrafted level document from raw JSON (negative tests feed mutated strings here).</summary>
    public static HandcraftedLevelSet Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Require(root.ValueKind == JsonValueKind.Object, "根节点须为对象");

        string era = String(root, "era");
        var items = new List<HandcraftedLevel>();
        var seenIds = new HashSet<string>();
        foreach (var item in Array(root, "items").EnumerateArray())
        {
            var level = ParseLevel(item, era);
            if (!seenIds.Add(level.Id))
            {
                throw new HandcraftedLevelSchemaException($"{level.Id}: 关卡 id 重复（一键一义）");
            }

            items.Add(level);
        }

        return new HandcraftedLevelSet(era, items);
    }

    // ---- one level row ----------------------------------------------------

    private static HandcraftedLevel ParseLevel(JsonElement item, string era)
    {
        string id = String(item, "id");
        double threat = Number(item, "threat");
        if (threat is < 0.3 or > 1.2)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 威胁度 {threat} 须在 [0.3,1.2] 带（v3.2 §7 clamp）");
        }

        int declaredWin = Int(item, "win_waves");
        int formulaWin = Campaign.WinWaves(threat);
        if (declaredWin != formulaWin)
        {
            throw new HandcraftedLevelSchemaException(
                $"{id}: win_waves={declaredWin} 与 §7 公式 10+⌊威胁度×10⌋={formulaWin} 不符（CS-4.4）");
        }

        double grace = Number(item, "first_wave_grace_multiplier");
        if (grace <= 0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 首波宽限乘子须为正（得 {grace}）");
        }

        bool teaching = Bool(item, "teaching_level");
        if (teaching && Math.Abs(grace - 3.0) > 1e-9)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 教学关首波宽限须 ×3（§6；得 {grace}）");
        }

        if (!teaching && (grace < 1.5 - 1e-9 || grace > 2.0 + 1e-9))
        {
            throw new HandcraftedLevelSchemaException($"{id}: 非教学关首波宽限须在 ×1.5~2 带（§6；得 {grace}）");
        }

        int budget = Int(item, "budget_seconds");
        if (budget is < 900 or > 2400)
        {
            throw new HandcraftedLevelSchemaException(
                $"{id}: 关卡预算 {budget}s 须在手工关 15~40 分钟带 [900,2400]s（§3）");
        }

        var map = ParseMap(item, id, era);
        var stock = ParseStock(item, id);
        var waves = ParseWaves(item, id, threat);
        var script = ParseScript(item, id, map, era);
        var objectives = ParseObjectives(item, id);
        var hooks = ParseHooks(item);
        var unlocks = ParseUnlocks(item);
        string? heroId = OptionalString(item, "hero_id");
        if ((heroId is null) != (map.HeroCell is null))
        {
            throw new HandcraftedLevelSchemaException($"{id}: hero_id 与 map.hero_cell 须成对出现");
        }

        return new HandcraftedLevel(
            Id: id,
            Era: String(item, "era"),
            Slot: Int(item, "slot"),
            NameZh: String(item, "name_zh"),
            NameEn: String(item, "name_en"),
            TeachingLevel: teaching,
            AwakeningCinematic: Bool(item, "awakening_cinematic"),
            Threat: threat,
            WinWaves: declaredWin,
            FirstWaveGraceMultiplier: grace,
            BudgetSeconds: budget,
            AdjacentEnemyCamps: OptionalInt(item, "adjacent_enemy_camps"),
            HeroId: heroId,
            Focus: OptionalString(item, "focus"),
            ScheduleTier: OptionalString(item, "schedule_tier"),
            Map: map,
            InitialStock: stock,
            Waves: waves,
            CommandScript: script,
            Objectives: objectives,
            CinematicHooks: hooks,
            Unlocks: unlocks);
    }

    private static HandcraftedMap ParseMap(JsonElement item, string id, string era)
    {
        var map = Object(item, "map");
        int width = Int(map, "width");
        int height = Int(map, "height");
        if (width < 3 || height < 3)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 地图须 ≥3×3（得 {width}×{height}）");
        }

        double water = Number(map, "water_ratio");
        double rock = Number(map, "rock_ratio");
        if (water != 0.0)
        {
            throw new HandcraftedLevelSchemaException(
                $"{id}: 手工关固定布局要求 water_ratio=0（命令流坐标可建性；水比先例=E2E 夹具）");
        }

        bool era3 = era.StartsWith("era3:", StringComparison.Ordinal);
        if (rock != 0.0 && !era3)
        {
            throw new HandcraftedLevelSchemaException(
                $"{id}: E1/E2 手工关固定布局要求 rock_ratio=0（命令流对任意 seed 可建）；E3 起 rock_ratio>0=矿位（煤）分布声明（T-115 §四，F-025）");
        }

        if (rock < 0.0 || rock > 1.0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 岩比 {rock} 须在 [0,1]");
        }

        double forest = Number(map, "forest_ratio");
        if (forest < 0.0 || forest > 1.0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 森林比 {forest} 须在 [0,1]");
        }

        var baseCell = Cell(map, "base_cell");
        var warehouse = Cell(map, "warehouse_cell");
        Inside(id, width, height, baseCell, "base_cell");
        Inside(id, width, height, warehouse, "warehouse_cell");

        var entries = new List<CellPoint>();
        foreach (var entry in Array(map, "entry_cells").EnumerateArray())
        {
            var point = ParseCell(entry);
            Inside(id, width, height, point, "entry_cells");
            entries.Add(point);
        }

        if (entries.Count == 0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: entry_cells 不得为空");
        }

        if (entries.Any(e => e == baseCell))
        {
            throw new HandcraftedLevelSchemaException($"{id}: 出生点不得与核心位重合");
        }

        CellPoint? hero = null;
        if (map.TryGetProperty("hero_cell", out var heroEl) && heroEl.ValueKind != JsonValueKind.Null)
        {
            hero = ParseCell(heroEl);
            Inside(id, width, height, hero, "hero_cell");
        }

        var digs = new List<CellPoint>();
        if (map.TryGetProperty("dig_sites", out var digsEl) && digsEl.ValueKind != JsonValueKind.Null)
        {
            foreach (var dig in digsEl.EnumerateArray())
            {
                var point = ParseCell(dig);
                Inside(id, width, height, point, "dig_sites");
                digs.Add(point);
            }
        }

        return new HandcraftedMap(
            Seed: Int(map, "seed"),
            Width: width,
            Height: height,
            ForestRatio: forest,
            RockRatio: rock,
            WaterRatio: water,
            BaseCell: baseCell,
            EntryCells: entries,
            WarehouseCell: warehouse,
            HeroCell: hero,
            DigSites: digs);
    }

    private static IReadOnlyList<StockLine> ParseStock(JsonElement item, string id)
    {
        var stock = new List<StockLine>();
        foreach (var line in Array(item, "initial_stock").EnumerateArray())
        {
            double amount = Number(line, "amount");
            if (amount <= 0)
            {
                throw new HandcraftedLevelSchemaException($"{id}: 初始资源 amount 须为正（得 {amount}）");
            }

            stock.Add(new StockLine(String(line, "resource"), amount));
        }

        if (stock.Count == 0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: initial_stock 不得为空（建造货币行必须存在）");
        }

        return stock;
    }

    private static HandcraftedWaveTable ParseWaves(JsonElement item, string id, double threat)
    {
        var waves = Object(item, "waves");
        int baseCount = Int(waves, "base_count");
        int spacing = Int(waves, "spacing");
        double scaling = Number(waves, "scaling");
        double valley = Number(waves, "valley_seconds");
        if (baseCount < 1)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 波表基数须 ≥1（得 {baseCount}）");
        }

        if (spacing < 1)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 波表间隔须 ≥1（得 {spacing}）");
        }

        if (scaling < 0.1)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 波表成长除数须 ≥0.1（得 {scaling}）");
        }

        double intervalSeconds = Campaign.WaveIntervalSeconds(threat);
        if (valley < 0 || valley >= intervalSeconds)
        {
            throw new HandcraftedLevelSchemaException(
                $"{id}: 波谷 {valley}s 须非负且小于波间隔 {intervalSeconds}s（F-034）");
        }

        var roster = new List<string>();
        foreach (var enemy in Array(waves, "regular_roster").EnumerateArray())
        {
            roster.Add(enemy.GetString()!);
        }

        if (roster.Count == 0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: regular_roster 不得为空");
        }

        if (roster.Count != roster.Distinct().Count())
        {
            throw new HandcraftedLevelSchemaException($"{id}: regular_roster 内敌种重复");
        }

        bool eliteEnabled = Bool(waves, "elite_line_enabled");
        string? eliteId = OptionalString(waves, "elite_enemy_id");
        if (eliteEnabled && eliteId is null)
        {
            throw new HandcraftedLevelSchemaException($"{id}: elite_line_enabled 须携带 elite_enemy_id");
        }

        if (!eliteEnabled && eliteId is not null)
        {
            throw new HandcraftedLevelSchemaException($"{id}: elite_enemy_id 仅在 elite_line_enabled 时存在");
        }

        return new HandcraftedWaveTable(
            baseCount, Int(waves, "begin_wave"), spacing, scaling,
            Int(waves, "batches_per_wave"), valley, roster, eliteEnabled, eliteId);
    }

    private static IReadOnlyList<ScriptStep> ParseScript(JsonElement item, string id, HandcraftedMap map, string era)
    {
        var script = new List<ScriptStep>();
        var builtCells = new HashSet<CellPoint>();
        long nextEntity = 2; // warehouse occupies ordinal 1 (Simulation convention)
        var builtEntities = new HashSet<long> { 1L }; // 仓库=关卡自带建筑（SimE1Fixture to:1 里程碑链先例）
        var towerEntities = new HashSet<long>();
        foreach (var stepEl in Array(item, "command_script").EnumerateArray())
        {
            var step = ParseStep(stepEl, id);
            if (step.Wave < 1 || step.Wave > Int(item, "win_waves"))
            {
                throw new HandcraftedLevelSchemaException(
                    $"{id}: 命令波锚 {step.Wave} 须在 [1,{Int(item, "win_waves")}]（胜波窗口内）");
            }

            bool isBuild = step.Verb is "build-labor-camp" or "build-harvester"
                or "build-converter" or "build-tower";
            if (isBuild)
            {
                if (step.Cell is null)
                {
                    throw new HandcraftedLevelSchemaException($"{id}: {step.Verb} 缺 cell");
                }

                Inside(id, map.Width, map.Height, step.Cell, $"command_script.{step.Verb}");
                if (!builtCells.Add(step.Cell))
                {
                    throw new HandcraftedLevelSchemaException(
                        $"{id}: cell({step.Cell.X},{step.Cell.Y}) 被重复建造（一格一建）");
                }

                builtEntities.Add(nextEntity);
                if (step.Verb == "build-tower")
                {
                    if (string.IsNullOrWhiteSpace(step.Tower))
                    {
                        throw new HandcraftedLevelSchemaException($"{id}: build-tower 缺 tower id");
                    }

                    towerEntities.Add(nextEntity);
                }

                if (step.Verb == "build-harvester" && (string.IsNullOrWhiteSpace(step.Resource) || step.Seats is null))
                {
                    throw new HandcraftedLevelSchemaException($"{id}: build-harvester 缺 resource/seats");
                }

                if (step.Verb == "build-converter" && string.IsNullOrWhiteSpace(step.ConverterRecipe)
                    && (era.StartsWith("era2:", StringComparison.Ordinal) || era.StartsWith("era3:", StringComparison.Ordinal)))
                {
                    throw new HandcraftedLevelSchemaException($"{id}: E2/E3 build-converter 须携带 converter_recipe 引用");
                }

                if (step.Verb != "build-converter" && step.ConverterRecipe is not null)
                {
                    throw new HandcraftedLevelSchemaException($"{id}: converter_recipe 仅属于 build-converter 步骤");
                }

                nextEntity++;
            }
            else if (step.Verb == "assign-carrier")
            {
                if (step.FromEntity is null || step.ToEntity is null)
                {
                    throw new HandcraftedLevelSchemaException($"{id}: assign-carrier 缺 from_entity/to_entity");
                }

                if (step.CarrierTier is not null && !CarrierTiers.Contains(step.CarrierTier))
                {
                    throw new HandcraftedLevelSchemaException(
                        $"{id}: 未登记运输档位 {step.CarrierTier}（封闭档位表：porter/cart/mine-rail/drone）");
                }

                RequireBuilt(id, builtEntities, step.FromEntity.Value, "assign-carrier.from_entity");
                RequireBuilt(id, builtEntities, step.ToEntity.Value, "assign-carrier.to_entity");
            }
            else if (step.Verb == "upgrade-tower")
            {
                if (step.Entity is null || step.ToTier is null)
                {
                    throw new HandcraftedLevelSchemaException($"{id}: upgrade-tower 缺 entity/to_tier");
                }

                RequireBuilt(id, builtEntities, step.Entity.Value, "upgrade-tower.entity");
                if (!towerEntities.Contains(step.Entity.Value))
                {
                    throw new HandcraftedLevelSchemaException(
                        $"{id}: upgrade-tower 目标实体 {step.Entity.Value} 不是塔");
                }

                if (step.ToTier.Value < 2)
                {
                    throw new HandcraftedLevelSchemaException($"{id}: upgrade-tower to_tier 须 ≥2");
                }
            }
            else if (step.Verb == "hero-order")
            {
                if (string.IsNullOrWhiteSpace(step.Order))
                {
                    throw new HandcraftedLevelSchemaException($"{id}: hero-order 缺 order");
                }

                if (step.Order == "move" && step.Cell is null)
                {
                    throw new HandcraftedLevelSchemaException($"{id}: hero-order move 缺 cell");
                }

                if (step.Cell is not null)
                {
                    Inside(id, map.Width, map.Height, step.Cell, "command_script.hero-order");
                }
            }

            script.Add(step);
        }

        if (script.Count == 0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: command_script 不得为空（教学引导建造序列）");
        }

        return script;
    }

    private static ScriptStep ParseStep(JsonElement el, string id)
    {
        string verb = String(el, "verb");
        if (!ScriptVerbs.Contains(verb))
        {
            throw new HandcraftedLevelSchemaException($"{id}: 未登记命令动词 {verb}（封闭动词表）");
        }

        return new ScriptStep(
            Wave: Int(el, "wave"),
            Verb: verb,
            Cell: el.TryGetProperty("cell", out var cell) && cell.ValueKind == JsonValueKind.Object
                ? ParseCell(cell)
                : null,
            Tower: OptionalString(el, "tower"),
            Resource: OptionalString(el, "resource"),
            Seats: OptionalInt(el, "seats"),
            FromEntity: OptionalLong(el, "from_entity"),
            ToEntity: OptionalLong(el, "to_entity"),
            Entity: OptionalLong(el, "entity"),
            ToTier: OptionalInt(el, "to_tier"),
            CarrierTier: OptionalString(el, "carrier_tier"),
            Order: OptionalString(el, "order"),
            ConverterRecipe: OptionalString(el, "converter_recipe"));
    }

    private static LevelObjectives ParseObjectives(JsonElement item, string id)
    {
        var objectives = Object(item, "objectives");
        var strategy = IdList(objectives, "strategy", id);
        var challenge = IdList(objectives, "challenge", id);
        if (strategy.Count == 0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 三重目标缺策略目标实例（SP-8a 池 ≥1）");
        }

        if (challenge.Count == 0)
        {
            throw new HandcraftedLevelSchemaException($"{id}: 三重目标缺挑战目标实例（SP-8b 池 ≥1）");
        }

        return new LevelObjectives(strategy, challenge);
    }

    private static IReadOnlyList<CinematicHook> ParseHooks(JsonElement item)
    {
        var hooks = new List<CinematicHook>();
        if (!item.TryGetProperty("cinematic_hooks", out var hooksEl) || hooksEl.ValueKind == JsonValueKind.Null)
        {
            return hooks;
        }

        foreach (var hook in hooksEl.EnumerateArray())
        {
            hooks.Add(new CinematicHook(
                String(hook, "id"),
                String(Object(hook, "trigger"), "beat"),
                hook.GetProperty("trigger").TryGetProperty("wave", out var wave) ? wave.GetInt32() : null));
        }

        return hooks;
    }

    private static LevelUnlocks ParseUnlocks(JsonElement item)
    {
        var unlocks = Object(item, "unlocks");
        var actions = new List<string>();
        foreach (var action in Array(unlocks, "actions").EnumerateArray())
        {
            actions.Add(action.GetString()!);
        }

        var features = new List<string>();
        foreach (var feature in Array(unlocks, "features").EnumerateArray())
        {
            features.Add(feature.GetString()!);
        }

        return new LevelUnlocks(actions, features);
    }

    private static List<string> IdList(JsonElement parent, string name, string id)
    {
        var ids = new List<string>();
        foreach (var value in Array(parent, name).EnumerateArray())
        {
            string objectiveId = value.GetString()!;
            if (string.IsNullOrWhiteSpace(objectiveId))
            {
                throw new HandcraftedLevelSchemaException($"{id}: objectives.{name} 含空 id");
            }

            ids.Add(objectiveId);
        }

        if (ids.Count != ids.Distinct().Count())
        {
            throw new HandcraftedLevelSchemaException($"{id}: objectives.{name} 内 id 重复");
        }

        return ids;
    }

    // ---- JSON helpers（与 Era2ContentTests 同约定，缺字段即异常） --------

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new HandcraftedLevelSchemaException(message);
        }
    }

    private static JsonElement Object(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new HandcraftedLevelSchemaException($"缺对象字段 {name}");
        }

        return value;
    }

    private static JsonElement Array(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new HandcraftedLevelSchemaException($"缺数组字段 {name}");
        }

        return value;
    }

    private static string String(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new HandcraftedLevelSchemaException($"缺非空字符串字段 {name}");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new HandcraftedLevelSchemaException($"缺数值字段 {name}");
        }

        return value.GetDouble();
    }

    private static int Int(JsonElement parent, string name)
    {
        double value = Number(parent, name);
        if (Math.Abs(value - Math.Floor(value)) > 1e-9)
        {
            throw new HandcraftedLevelSchemaException($"字段 {name} 须为整数（得 {value}）");
        }

        return (int)value;
    }

    private static int? OptionalInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)value.GetDouble()
            : null;

    private static long? OptionalLong(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (long)value.GetDouble()
            : null;

    private static bool Bool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.True
            && value.ValueKind != JsonValueKind.False)
        {
            throw new HandcraftedLevelSchemaException($"缺布尔字段 {name}");
        }

        return value.GetBoolean();
    }

    private static CellPoint Cell(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new HandcraftedLevelSchemaException($"缺坐标字段 {name}");
        }

        return ParseCell(value);
    }

    private static CellPoint ParseCell(JsonElement value) => new(
        Int(value, "x"),
        Int(value, "y"));

    private static void Inside(string id, int width, int height, CellPoint cell, string field)
    {
        if (cell.X < 0 || cell.X >= width || cell.Y < 0 || cell.Y >= height)
        {
            throw new HandcraftedLevelSchemaException(
                $"{id}: {field} 坐标 ({cell.X},{cell.Y}) 越界（地图 {width}×{height}）");
        }
    }

    private static void RequireBuilt(string id, HashSet<long> built, long entity, string field)
    {
        if (!built.Contains(entity))
        {
            throw new HandcraftedLevelSchemaException(
                $"{id}: {field} 引用未建造实体 {entity}（实体序号=仓库 1 起按建造序）");
        }
    }
}

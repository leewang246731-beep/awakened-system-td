using System.Text.Json;
using EraCore.Contracts.Foundation;
using EraCore.Units;
using Xunit;

namespace EraCore.Tests.Units;

// T-039 (C-10) 测试基建：K-1 七端口 Fake（IClock/IRng——CS-3.4 白名单）、
// 仓库相对寻址的内容装载（RL9：禁绝对路径，同 Era1ContentTests 模式）、
// 木箭塔开火循环（towers.json 常量驱动，CS-4.4）与单位状态哈希。

/// <summary>
/// Manually advanced fixed-step clock (K-1 port fake): the test loop owns
/// the tick counter, one tick = 1/30 s.
/// </summary>
internal sealed class FixedClock : IClock
{
    public FixedClock(long tick = 0)
    {
        Tick = tick;
    }

    public long Tick { get; private set; }

    public long NowMs => (Tick * 1000L) / 30L; // deterministic diagnostics value only

    public void Advance() => Tick++;

    public void Set(long tick) => Tick = tick;
}

/// <summary>
/// SplitMix64 seeded stream (K-1 port fake): same seed + same call sequence
/// ⇒ same draws, which the determinism tests rely on. NextDouble yields
/// [0,1) via the standard 53-bit reduction.
/// </summary>
internal sealed class SplitMixRng : IRng
{
    private ulong _state;

    public SplitMixRng(long seed)
    {
        Reseed(seed);
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentException($"minValue ({minValue}) must be below maxValue ({maxValue}).");
        }

        long span = maxValue - (long)minValue;
        return minValue + (int)(NextDouble() * span);
    }

    public double NextDouble()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }

    public void Reseed(long seed) => _state = (ulong)seed;
}

/// <summary>Repo-relative loaders for era1 content (RL9: walk up, no absolute paths).</summary>
internal static class UnitsContent
{
    private static DirectoryInfo FindContentDir(string era)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", era);
            if (Directory.Exists(probe))
            {
                return new DirectoryInfo(probe);
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"未找到 game/content/{era}（测试须在仓库内运行）");
    }

    private static string ReadContentFile(string era, string fileName)
    {
        string path = Path.Combine(FindContentDir(era).FullName, fileName);
        Assert.True(File.Exists(path), $"内容文件缺失：{era}/{fileName}");
        return File.ReadAllText(path);
    }

    /// <summary>Parses the real era1 enemies.json into the production catalog.</summary>
    public static EnemySpecCatalog LoadEnemyCatalog() =>
        EnemySpecCatalog.Parse(ReadContentFile("era1", "enemies.json"));

    /// <summary>Parses the real era2 enemies.json into the production catalog (SP-3b).</summary>
    public static EnemySpecCatalog LoadEra2EnemyCatalog() =>
        EnemySpecCatalog.Parse(ReadContentFile("era2", "enemies.json"));

    /// <summary>Parses the real era3 enemies.json into the production catalog (T-112 rows).</summary>
    public static EnemySpecCatalog LoadEra3EnemyCatalog() =>
        EnemySpecCatalog.Parse(ReadContentFile("era3", "enemies.json"));

    /// <summary>Parses the real heroes.json (SP-6 E1+E2 pair in one file) into the production catalog.</summary>
    public static HeroSpecCatalog LoadHeroCatalog() =>
        HeroSpecCatalog.Parse(ReadContentFile("era2", "heroes.json"));

    /// <summary>Raw JSON text of the era2 enemy table (anchor comparisons read fields the loader does not map).</summary>
    public static string ReadEra2EnemiesJson() => ReadContentFile("era2", "enemies.json");

    /// <summary>
    /// Wood arrow tower combat constants from towers.json (era1:tower-wood-arrow):
    /// damage_per_shot / reload_ticks / range_cells / hp — the hand-built
    /// firing loop below is driven by exactly these file values (CS-4.4:
    /// constants come from data JSON, not from the test body).
    /// </summary>
    public sealed record TowerStats(int DamagePerShot, int ReloadTicks, double RangeCells, int Hp, int CellSizePx);

    public static TowerStats LoadWoodArrowTower() => LoadTower("era1", "era1:tower-wood-arrow");

    /// <summary>
    /// Rockfall tower constants (era1:tower-rockfall) — the E2 boss-battle
    /// harness fires these because era2 towers.json is another task's
    /// deliverable (T-065); values still come from data JSON (CS-4.4).
    /// </summary>
    public static TowerStats LoadRockfallTower() => LoadTower("era1", "era1:tower-rockfall");

    /// <summary>
    /// E3 gatling constants (era3:tower-gatling, T-112 rows) — the T-114
    /// fortress-battle harness fires these (d=16/reload 2/range 8).
    /// </summary>
    public static TowerStats LoadGatlingTower() => LoadTower("era3", "era3:tower-gatling");

    /// <summary>
    /// E3 mortar constants (era3:tower-mortar, T-112 rows) — the T-114
    /// fortress-battle body-killer line (d=480/reload 30/range 11).
    /// </summary>
    public static TowerStats LoadMortarTower() => LoadTower("era3", "era3:tower-mortar");

    /// <summary>
    /// E3 ironclad-wall HP (era3:tower-ironclad-wall — support row, zero
    /// damage, hp 1600): the fortress battle's pure trample sink. Only the
    /// hp field exists for a non-firing row (reload/range are null in the
    /// file), so this loader reads just that.
    /// </summary>
    public static int LoadIroncladWallHp()
    {
        JsonElement item = FindTowerItem("era3", "era3:tower-ironclad-wall");
        return item.GetProperty("hp").GetInt32();
    }

    private static JsonElement FindTowerItem(string era, string towerId)
    {
        string path = Path.Combine(FindContentDir(era).FullName, "towers.json");
        Assert.True(File.Exists(path), $"内容文件缺失：{era}/towers.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonElement e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (e.GetProperty("id").GetString() == towerId)
            {
                return e.Clone();
            }
        }

        Assert.Fail($"towers.json 缺 {towerId}");
        throw new InvalidOperationException("unreachable");
    }

    private static TowerStats LoadTower(string era, string towerId)
    {
        JsonElement item = FindTowerItem(era, towerId);
        int cellSize = 32; // K-1 cell size (era3 towers.json carries no root override)
        return new TowerStats(
            item.GetProperty("damage_per_shot").GetInt32(),
            item.GetProperty("reload_ticks").GetInt32(),
            item.GetProperty("range_cells").GetDouble(),
            item.GetProperty("hp").GetInt32(),
            cellSize);
    }
}

/// <summary>
/// Hand-built wood-arrow tower for the E1 thin battle simulation (T-039 task
/// card: 木箭塔常量手搓开火循环). Fires at the first nearest living unit inside
/// range (strictly-closer comparison, list order tie-break — deterministic),
/// settles damage through <see cref="Unit.ApplyDamage"/>, and takes trample
/// pulses through <see cref="ITrampleTarget"/>.
/// </summary>
internal sealed class TestTower : ITrampleTarget
{
    private readonly int _damagePerShot;
    private readonly int _reloadTicks;
    private readonly float _rangePx;

    public TestTower(float x, float y, UnitsContent.TowerStats stats)
    {
        X = x;
        Y = y;
        _damagePerShot = stats.DamagePerShot;
        _reloadTicks = stats.ReloadTicks;
        _rangePx = (float)(stats.RangeCells * stats.CellSizePx);
        Hp = stats.Hp;
        Alive = true;
        NextFireTick = 0;
    }

    public float X { get; }

    public float Y { get; }

    public bool Alive { get; private set; }

    public int Hp { get; private set; }

    public long NextFireTick { get; internal set; }

    /// <summary>
    /// Whether this tower's rounds carry the armorPen flag (the E4 energy
    /// line's 穿甲标记 mechanic, SP-1a L28 — interrupts shield regeneration
    /// for 3 s). The T-114 two-case comparison flips exactly this bit.
    /// </summary>
    public bool PierceRounds { get; internal set; }

    public int ShotsFired { get; private set; }

    public int DamageDealt { get; private set; }

    public int TrampleHitsTaken { get; private set; }

    public long DestroyedTick { get; private set; } = -1;

    public void FireAt(IReadOnlyList<Unit> units, long tick, bool pierce = false)
    {
        if (!Alive || tick < NextFireTick)
        {
            return;
        }

        float rangeSquared = _rangePx * _rangePx;
        Unit? best = null;
        float bestDistance = rangeSquared;
        foreach (Unit unit in units)
        {
            if (!unit.Alive)
            {
                continue;
            }

            float dx = unit.X - X;
            float dy = unit.Y - Y;
            float distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                // strictly closer only — ties keep the earlier list entry
                best = unit;
                bestDistance = distance;
            }
        }

        if (best is null)
        {
            return;
        }

        DamageDealt += best.ApplyDamage(_damagePerShot, tick, pierce || PierceRounds);
        ShotsFired++;
        NextFireTick = tick + _reloadTicks;
    }

    public void TakeTrample(int damage)
    {
        if (!Alive)
        {
            return;
        }

        TrampleHitsTaken++;
        Hp = Math.Max(0, Hp - damage);
        if (Hp == 0)
        {
            Alive = false;
        }
    }

    /// <summary>Records the tick the tower fell on (called by the sim loop).</summary>
    public void MarkDestroyed(long tick)
    {
        if (!Alive && DestroyedTick < 0)
        {
            DestroyedTick = tick;
        }
    }
}

/// <summary>
/// FNV-1a 64 over ordered unit state (id, position bits, hp, flags) — the
/// observable the determinism tests compare across runs.
/// </summary>
internal static class UnitStateHash
{
    private const ulong Prime = 1099511628211UL;

    public static long Compute(IEnumerable<Unit> units)
    {
        ulong hash = 14695981039346656037UL;
        foreach (Unit unit in units.OrderBy(u => u.Id.Value))
        {
            hash = Mix(hash, (ulong)unit.Id.Value);
            hash = Mix(hash, unchecked((ulong)BitConverter.SingleToInt32Bits(unit.X)));
            hash = Mix(hash, unchecked((ulong)BitConverter.SingleToInt32Bits(unit.Y)));
            hash = Mix(hash, (ulong)unit.Hp);
            hash = Mix(hash, unit.Alive ? 1UL : 0UL);
            hash = Mix(hash, unit.ArrivedAtTarget ? 1UL : 0UL);
        }

        return unchecked((long)hash);
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        return hash * Prime;
    }
}

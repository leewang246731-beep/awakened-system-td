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
    private static string FindEra1Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "game", "content", "era1");
            if (Directory.Exists(probe))
            {
                return probe;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到 game/content/era1（测试须在仓库内运行）");
    }

    /// <summary>Parses the real enemies.json into the production catalog.</summary>
    public static EnemySpecCatalog LoadEnemyCatalog()
    {
        string path = Path.Combine(FindEra1Dir(), "enemies.json");
        Assert.True(File.Exists(path), "内容文件缺失：enemies.json");
        return EnemySpecCatalog.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Wood arrow tower combat constants from towers.json (era1:tower-wood-arrow):
    /// damage_per_shot / reload_ticks / range_cells / hp — the hand-built
    /// firing loop below is driven by exactly these file values (CS-4.4:
    /// constants come from data JSON, not from the test body).
    /// </summary>
    public sealed record TowerStats(int DamagePerShot, int ReloadTicks, double RangeCells, int Hp, int CellSizePx);

    public static TowerStats LoadWoodArrowTower()
    {
        string path = Path.Combine(FindEra1Dir(), "towers.json");
        Assert.True(File.Exists(path), "内容文件缺失：towers.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement item = default;
        foreach (JsonElement e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (e.GetProperty("id").GetString() == "era1:tower-wood-arrow")
            {
                item = e;
                break;
            }
        }

        Assert.True(item.ValueKind == JsonValueKind.Object, "towers.json 缺 era1:tower-wood-arrow");
        int cellSize = doc.RootElement.TryGetProperty("cell_size_px", out JsonElement cs)
            ? cs.GetInt32()
            : 32;
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

    public int ShotsFired { get; private set; }

    public int DamageDealt { get; private set; }

    public int TrampleHitsTaken { get; private set; }

    public long DestroyedTick { get; private set; } = -1;

    public void FireAt(IReadOnlyList<Unit> units, long tick)
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

        DamageDealt += best.ApplyDamage(_damagePerShot, tick);
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

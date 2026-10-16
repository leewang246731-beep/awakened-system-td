using EraCore.Units;

namespace EraCore.Endless;

/// <summary>
/// One era's contribution to an endless pool (T-118 E3 无尽可用): the era
/// index plus the era's parsed <see cref="EnemySpec"/> rows (caller owns the
/// IO — the host reads the era's enemies.json and hands the parsed catalog
/// rows over, EraCore stays IO-pure). Pure data record.
/// </summary>
/// <param name="EraIndex">Era the rows belong to (1..6, appendix A).</param>
/// <param name="Enemies">The era's enemy definitions (content set — for E3 the current four-level content rows of game/content/era3/enemies.json).</param>
public sealed record EndlessEraEnemySet(int EraIndex, IReadOnlyList<EnemySpec> Enemies);

/// <summary>
/// Era-content → 混编池 builder (T-118, F-006; SP-7 入口规则「混编敌人——跨
/// 时代敌人池」): turns parsed per-era <see cref="EnemySpec"/> sets into one
/// cross-era <see cref="EndlessEnemyPool"/> with clean role semantics —
/// <list type="bullet">
/// <item>Boss 行（<c>is_boss</c>）→ Boss 子池（boss cadence 抽取源）；</item>
/// <item>非 Boss 行按 (HP 降序, id 序) 排序，最重 <c>ElitesPerEra</c> 行 →
/// 精英线子池（精英线 1+⌊(w−8)/4⌋ 不封顶，必须有行）——最重行当精英线是
/// 工程规则（[NEW-原型校准]：内容侧无 elite 位标记，重甲厚血行是精英线
/// 的最小诚实代理；非平衡数值，RL7 宿主可换自定义池）；</item>
/// <item>其余非 Boss 行 → 常规线子池；</item>
/// <item>权重一律 1.0（时代内均匀——SP-7 只钉「按权重」不钉权重值；宿主经
/// <see cref="EndlessEnemyPool"/> 原始构造器可注入任意权重表）。</item>
/// </list>
/// Deterministic: no rng, no clock — same input sets ⇒ same pool (CS-1.4).
/// </summary>
public static class EndlessEraPools
{
    /// <summary>
    /// 每时代进精英线的最重行数（默认 1，工程结构值 [NEW-原型校准]——每代
    /// 一条重行扛精英线，常规线保持宽混编面）。
    /// </summary>
    public const int DefaultElitesPerEra = 1; // ci-allow: R7 T-118 工程结构值（每时代精英线行数，非平衡数值）

    /// <summary>
    /// Builds the cross-era pool from the era sets (混编跨时代): every era
    /// contributes regular, elite and (when present) boss rows; ids stay
    /// unique because a row enters exactly ONE role sub-pool. The result
    /// always carries ≥1 Regular and ≥1 Elite entry; Boss presence follows
    /// the sets (an endless config additionally requires ≥1 Boss row —
    /// <see cref="EndlessRunConfig"/> validates that).
    /// </summary>
    /// <exception cref="ArgumentNullException">eraSets is null or carries a null member/list.</exception>
    /// <exception cref="ArgumentException">
    /// Empty era-set list, an era index outside 1..6, a set with fewer than
    /// <c>elitesPerEra + 1</c> non-boss rows (the era could not keep a
    /// non-empty regular line), or a duplicate (era, enemy id) pair.
    /// </exception>
    public static EndlessEnemyPool Build(IReadOnlyList<EndlessEraEnemySet> eraSets, int elitesPerEra = DefaultElitesPerEra)
    {
        ArgumentNullException.ThrowIfNull(eraSets);
        if (eraSets.Count == 0)
        {
            throw new ArgumentException("The endless pool needs at least one era set.", nameof(eraSets));
        }

        if (elitesPerEra < 1)
        {
            throw new ArgumentException($"ElitesPerEra must be ≥ 1 (got {elitesPerEra}).", nameof(elitesPerEra));
        }

        var entries = new List<EndlessEnemyPoolEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (EndlessEraEnemySet set in eraSets)
        {
            ArgumentNullException.ThrowIfNull(set);
            ArgumentNullException.ThrowIfNull(set.Enemies);
            if (set.EraIndex < EndlessEnemyPool.MinEraIndex || set.EraIndex > EndlessEnemyPool.MaxEraIndex)
            {
                throw new ArgumentException(
                    $"Era index must lie in {EndlessEnemyPool.MinEraIndex}..{EndlessEnemyPool.MaxEraIndex} (got {set.EraIndex}).",
                    nameof(eraSets));
            }

            List<EnemySpec> nonBoss = set.Enemies.Where(enemy => !enemy.IsBoss).ToList();
            if (nonBoss.Count < elitesPerEra + 1)
            {
                throw new ArgumentException(
                    $"Era {set.EraIndex} carries {nonBoss.Count} non-boss rows — an era keeps a non-empty regular line only with ≥ {elitesPerEra + 1}.",
                    nameof(eraSets));
            }

            // 精英线=最重行（HP 降序，id 序破平）：确定性排序——同一内容集恒得同一池。
            IOrderedEnumerable<EnemySpec> heaviestFirst = nonBoss
                .OrderByDescending(enemy => enemy.Hp)
                .ThenBy(enemy => enemy.Id, StringComparer.Ordinal);
            HashSet<string> eliteIds = heaviestFirst.Take(elitesPerEra)
                .Select(enemy => enemy.Id)
                .ToHashSet(StringComparer.Ordinal);

            foreach (EnemySpec enemy in set.Enemies)
            {
                if (!seenIds.Add(enemy.Id))
                {
                    throw new ArgumentException($"Duplicate enemy id '{enemy.Id}' across the era sets.", nameof(eraSets));
                }

                EndlessEnemyRole role = enemy.IsBoss
                    ? EndlessEnemyRole.Boss
                    : eliteIds.Contains(enemy.Id)
                        ? EndlessEnemyRole.Elite
                        : EndlessEnemyRole.Regular;
                entries.Add(new EndlessEnemyPoolEntry(enemy.Id, set.EraIndex, role, Weight: 1.0));
            }
        }

        return new EndlessEnemyPool(entries);
    }
}

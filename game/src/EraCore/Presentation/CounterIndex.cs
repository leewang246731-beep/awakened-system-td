using System.Text.Json;
using EraCore.Combat;

namespace EraCore.Presentation;

/// <summary>
/// One counter row: an enemy that the indexed tower class counters
/// (T-093 三层信息静态面板的克制表数据源).
/// </summary>
/// <param name="EnemyId">Namespaced content id.</param>
/// <param name="NameZh">Chinese display name.</param>
/// <param name="Armor">Armor points (SP-3 甲列).</param>
public sealed record CounterRow(string EnemyId, string NameZh, int Armor);

/// <summary>
/// Tower-class → countered-enemies index built from the enemy table's
/// <c>counters</c> column (T-093, F-120 三层信息①: 塔详情卡的克制行).
/// The index INVERTS the per-enemy <c>counters</c> arrays of
/// enemies.json — the enemy table stays the single source (RL7), the
/// card asks "which enemies list my class". Pure presentation-side
/// parse; <c>EnemySpec</c> (Combat) intentionally stays untouched.
/// </summary>
public sealed class CounterIndex
{
    private const string ExpectedContentType = "Enemy";

    private readonly Dictionary<TowerClass, List<CounterRow>> _byClass = new();

    private CounterIndex()
    {
    }

    /// <summary>
    /// Parses one enemies.json document and builds the inverted index.
    /// The enemy table's <c>counters</c> column MIXES tower classes
    /// (physical/pierce/…) with strategy tokens (<c>focus_fire</c> 远程集
    /// 火， SP-3a 克制提示列) — only the class tokens enter the index;
    /// strategy tokens are skipped (they address the player's focus-fire
    /// play, not a tower class). Rows without a counters array are skipped.
    /// </summary>
    /// <param name="json">enemies.json text.</param>
    /// <param name="sourceName">Label for error messages.</param>
    /// <exception cref="ArgumentException">Structure or value violations.</exception>
    public static CounterIndex FromEnemiesJson(string json, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(json);
        EnemyCounterDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, UiContentJsonContext.Default.EnemyCounterDocument);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{sourceName}: invalid JSON ({ex.Message}).", nameof(json));
        }

        if (document is null)
        {
            throw new ArgumentException($"{sourceName}: deserialized to null.", nameof(json));
        }

        if (document.ContentType != ExpectedContentType)
        {
            throw new ArgumentException(
                $"{sourceName}: content_type must be '{ExpectedContentType}', got '{document.ContentType}'.");
        }

        var index = new CounterIndex();
        if (document.Items is not { Length: > 0 } items)
        {
            return index; // an empty catalog indexes nothing (legal: not every era file has rows)
        }

        foreach (EnemyCounterRowJson item in items)
        {
            if (item.Counters is not { Length: > 0 })
            {
                continue;
            }

            string id = item.Id ?? string.Empty;
            if (id.Trim().Length == 0)
            {
                throw new ArgumentException($"{sourceName}: items[].id must be non-blank when counters present.");
            }

            string name = item.NameZh ?? id;
            foreach (string className in item.Counters)
            {
                // JSON 用 snake_case（"pierce"），枚举名 PascalCase——大小写
                // 不敏感解析即六类冻结词表（tower_class 列，SP-1a）；解析失败
                // =战术词（focus_fire 等），跳过不入索引。
                if (!Enum.TryParse(className, ignoreCase: true, out TowerClass towerClass))
                {
                    continue;
                }

                if (!index._byClass.TryGetValue(towerClass, out List<CounterRow>? rows))
                {
                    rows = new List<CounterRow>();
                    index._byClass.Add(towerClass, rows);
                }

                rows.Add(new CounterRow(id, name, item.Armor.GetValueOrDefault(0)));
            }
        }

        return index;
    }

    /// <summary>
    /// The enemies the given tower class counters, in enemy-file order.
    /// </summary>
    public IReadOnlyList<CounterRow> CounteredBy(TowerClass towerClass) =>
        _byClass.TryGetValue(towerClass, out List<CounterRow>? rows)
            ? rows
            : Array.Empty<CounterRow>();
}

/// <summary>Root shape of the counter parse: content_type + items.</summary>
internal sealed record EnemyCounterDocument(string? ContentType, EnemyCounterRowJson[]? Items);

/// <summary>Raw enemy row shape for the counter parse (annotation keys ignored).</summary>
internal sealed record EnemyCounterRowJson(string? Id, string? NameZh, int? Armor, string[]? Counters);

using System.Text.Json;

namespace EraCore.Cinematics;

/// <summary>
/// The beat an 演出素材接入点 fires on (T-086, §10.4 hook grammar — the
/// <c>trigger.beat</c> values of <c>game/content/levels/*.json</c>
/// <c>cinematic_hooks</c> rows): E2 觉醒=level_start, E1 啊哈=wave_start(第
/// 2~3 波), 首关胜利交底=victory.
/// </summary>
public enum CinematicBeat
{
    /// <summary>Level opening (E2 L2-1 觉醒演出位).</summary>
    LevelStart = 0,

    /// <summary>A wave boundary (E1 L1-1 第 2~3 波啊哈位).</summary>
    WaveStart = 1,

    /// <summary>Level victory (首关觉醒交底位).</summary>
    Victory = 2,
}

/// <summary>One 演出素材接入点 row of a level file: hook id + beat + optional wave anchor.</summary>
public sealed record CinematicHookRow(string HookId, CinematicBeat Beat, int? Wave);

/// <summary>
/// Binds level hook rows to cinematic scripts and routes runtime beats
/// (T-086, C-24): the session-owned single-fire gate between the game loop
/// (LevelSession event positions — wave starts, victory, level start, fed by
/// the host that owns the session) and the cinematic director. Cross-run
/// "already seen" state is NOT here — that lives in
/// <see cref="CinematicProgressStore"/> (存档); this router only guarantees
/// one fire per script per session, so a beat observed tick-by-tick never
/// double-starts a performance.
/// </summary>
public sealed class CinematicHookRouter
{
    private readonly List<(CinematicHookRow Row, CinematicScript Script)> _bindings = new();
    private readonly HashSet<string> _firedScripts = new(StringComparer.Ordinal);

    /// <summary>
    /// Wires the scripts onto the level's hook rows. Every script that
    /// declares a hook must find its row — a script pointing at a hook the
    /// level never declares is a wiring bug and fails fast here.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// A script references an unknown hook id, or two scripts claim the same
    /// hook row.
    /// </exception>
    public CinematicHookRouter(
        IReadOnlyList<CinematicHookRow> levelHooks,
        IReadOnlyList<CinematicScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(levelHooks);
        ArgumentNullException.ThrowIfNull(scripts);

        Dictionary<string, CinematicHookRow> rowsById = new(StringComparer.Ordinal);
        foreach (CinematicHookRow row in levelHooks)
        {
            if (!rowsById.TryAdd(row.HookId, row))
            {
                throw new ArgumentException($"level hook id 重复（{row.HookId}）", nameof(levelHooks));
            }
        }

        foreach (CinematicScript script in scripts)
        {
            if (string.IsNullOrEmpty(script.HookId))
            {
                continue; // manual-only script (回看/手动演出) — no beat binding
            }

            if (!rowsById.TryGetValue(script.HookId, out CinematicHookRow? row))
            {
                throw new ArgumentException(
                    $"脚本 {script.Id} 引用了关卡未声明的钩子 {script.HookId}（先在 levels/*.json 落钩子行）",
                    nameof(scripts));
            }

            if (_bindings.Any(binding => binding.Row.HookId == row.HookId))
            {
                throw new ArgumentException(
                    $"钩子 {row.HookId} 被多个脚本认领（一个接入点一场演出）", nameof(scripts));
            }

            _bindings.Add((row, script));
        }
    }

    /// <summary>Bound (row, script) pairs, in declaration order (observability).</summary>
    public IReadOnlyList<(CinematicHookRow Row, CinematicScript Script)> Bindings => _bindings;

    /// <summary>Scripts bound to the given beat that have not fired this session.</summary>
    public IReadOnlyList<string> OnLevelStart() => Match(row => row.Beat == CinematicBeat.LevelStart);

    /// <summary>Scripts bound to a wave_start beat matching the wave anchor (exact wave).</summary>
    public IReadOnlyList<string> OnWaveStart(int wave) =>
        Match(row => row.Beat == CinematicBeat.WaveStart && row.Wave == wave);

    /// <summary>Scripts bound to the victory beat.</summary>
    public IReadOnlyList<string> OnVictory() => Match(row => row.Beat == CinematicBeat.Victory);

    /// <summary>
    /// Marks a script as fired WITHOUT routing it (the replay path 可回看:
    /// explicit launch bypasses the router, this keeps the session bookkeeping
    /// consistent so the hooked beat later in the same session stays silent).
    /// </summary>
    public void MarkFired(string scriptId)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
        {
            throw new ArgumentException("scriptId must not be blank.", nameof(scriptId));
        }

        _firedScripts.Add(scriptId);
    }

    private IReadOnlyList<string> Match(Func<CinematicHookRow, bool> predicate)
    {
        var due = new List<string>();
        foreach ((CinematicHookRow row, CinematicScript script) in _bindings)
        {
            if (predicate(row) && _firedScripts.Add(script.Id))
            {
                due.Add(script.Id);
            }
        }

        return due;
    }

    /// <summary>
    /// Parses the <c>cinematic_hooks</c> arrays of a level file
    /// (<c>items[*].cinematic_hooks[*]</c>). Beat strings form the closed
    /// set of <see cref="CinematicBeat"/>; a wave_start row must carry
    /// <c>wave ≥ 1</c>; rows must not repeat ids.
    /// </summary>
    /// <param name="levelJson">Raw level file JSON.</param>
    /// <param name="sourceName">Source label for error messages.</param>
    /// <exception cref="CinematicSchemaException">Any schema violation.</exception>
    public static IReadOnlyList<CinematicHookRow> ParseLevelHooks(string levelJson, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(levelJson))
        {
            throw new CinematicSchemaException($"{sourceName}: 关卡文件为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(levelJson);
        }
        catch (JsonException ex)
        {
            throw new CinematicSchemaException($"{sourceName}: JSON 解析失败（{ex.Message}）");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out JsonElement itemsEl)
                || itemsEl.ValueKind != JsonValueKind.Array)
            {
                throw new CinematicSchemaException($"{sourceName}: 缺 items 数组（关卡文件结构）");
            }

            var rows = new List<CinematicHookRow>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in itemsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("cinematic_hooks", out JsonElement hooksEl)
                    || hooksEl.ValueKind != JsonValueKind.Array
                    || hooksEl.GetArrayLength() == 0)
                {
                    continue;
                }

                foreach (JsonElement hook in hooksEl.EnumerateArray())
                {
                    if (hook.ValueKind != JsonValueKind.Object
                        || !hook.TryGetProperty("id", out JsonElement idEl)
                        || idEl.ValueKind != JsonValueKind.String)
                    {
                        throw new CinematicSchemaException($"{sourceName}: cinematic_hooks 行缺字符串 id");
                    }

                    string id = idEl.GetString()!;
                    if (seenIds.Contains(id))
                    {
                        throw new CinematicSchemaException($"{sourceName}: cinematic_hooks id 重复（{id}）");
                    }

                    seenIds.Add(id);
                    if (hook.TryGetProperty("trigger", out JsonElement triggerEl)
                        && triggerEl.ValueKind == JsonValueKind.Object
                        && triggerEl.TryGetProperty("beat", out JsonElement beatEl)
                        && beatEl.ValueKind == JsonValueKind.String)
                    {
                        string beat = beatEl.GetString()!;
                        int? wave = triggerEl.TryGetProperty("wave", out JsonElement waveEl)
                            && waveEl.ValueKind == JsonValueKind.Number
                            && waveEl.TryGetInt32(out int waveValue)
                            ? waveValue
                            : null;
                        CinematicBeat parsedBeat = ParseBeat(beat, sourceName, id);
                        if (parsedBeat == CinematicBeat.WaveStart)
                        {
                            if (wave is null)
                            {
                                throw new CinematicSchemaException(
                                    $"{sourceName}: 钩子 {id} 的 wave_start 必须带 wave（第几波）");
                            }

                            if (wave < 1)
                            {
                                throw new CinematicSchemaException(
                                    $"{sourceName}: 钩子 {id} 的 wave 须 ≥1（得到 {wave}）");
                            }
                        }

                        rows.Add(new CinematicHookRow(id, parsedBeat, wave));
                    }
                    else
                    {
                        throw new CinematicSchemaException($"{sourceName}: 钩子 {id} 缺 trigger.beat");
                    }
                }
            }

            return rows;
        }
    }

    private static CinematicBeat ParseBeat(string beat, string source, string hookId) => beat switch
    {
        "level_start" => CinematicBeat.LevelStart,
        "wave_start" => CinematicBeat.WaveStart,
        "victory" => CinematicBeat.Victory,
        _ => throw new CinematicSchemaException(
            $"{source}: 钩子 {hookId} 未登记 beat（{beat}，封闭表 level_start/wave_start/victory）"),
    };
}

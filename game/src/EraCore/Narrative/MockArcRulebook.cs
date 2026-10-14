namespace EraCore.Narrative;

/// <summary>The verdict of one 打脸/扮猪 judgment (F-100/F-101).</summary>
/// <param name="HiddenHit">True when the hidden evaluation fired (低配通关).</param>
/// <param name="HiddenChestTier">The declared hidden chest tier (valid only on hit).</param>
/// <param name="HiddenItemIds">The declared hidden reward items (valid only on hit).</param>
/// <param name="Lines">The 打脸/翻盘 performance lines to play on hit (empty on miss).</param>
/// <param name="ReasonZh">Human-readable verdict reason (settlement surface).</param>
public sealed record MockArcVerdict(
    bool HiddenHit,
    string? HiddenChestTier,
    IReadOnlyList<string> HiddenItemIds,
    IReadOnlyList<DialogueLine> Lines,
    string ReasonZh)
{
    /// <summary>Miss verdict (normal clear or defeat).</summary>
    public static MockArcVerdict Miss(string reasonZh) =>
        new(false, null, Array.Empty<string>(), Array.Empty<DialogueLine>(), reasonZh);
}

/// <summary>
/// The configured 打脸/扮猪 rulebook (T-089, F-100/F-101): every knob —
/// suppression multiplier, taunt anchors, low-config threshold, hidden
/// reward — comes from the story chapter rows (RL7: 数据化非硬编码); this
/// class only reads the row and judges. 纯函数：the verdict depends solely
/// on the declared row and the observed end facts (先定后演 — the
/// settlement performance, T-087's hidden tier, replays this verdict and
/// never re-rolls it).
/// </summary>
public static class MockArcRulebook
{
    /// <summary>
    /// Applies the 强制劣势开局 suppression to one initial-stock amount:
    /// <c>base × multiplier</c>. Round-half-up to the resource economy's
    /// whole-point convention so a suppressed start stays deterministic
    /// across replays.
    /// </summary>
    /// <exception cref="ArgumentException">The row carries no suppression (face_slap rows).</exception>
    public static double SuppressedStock(double baseAmount, MockArcRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Suppression is not { } suppression)
        {
            throw new ArgumentException(
                $"关卡 {row.LevelId} 无资源压制参数（suppression 仅扮猪吃虎关携带）", nameof(row));
        }

        return Math.Floor(baseAmount * suppression.Multiplier + 0.5d);
    }

    /// <summary>The taunts anchored to one wave (打脸拍：NPC 嘲讽→低配通关→震惊演出).</summary>
    public static IReadOnlyList<MockArcTaunt> TauntsDue(MockArcRow row, int wave)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Taunts.Where(taunt => taunt.Wave == wave).ToArray();
    }

    /// <summary>
    /// The hidden-evaluation judgment (低配通关判定): victory AND at most
    /// <c>max_towers</c> weapon towers standing at the victory moment. On a
    /// hit the verdict carries the declared chest tier, items and
    /// performance lines (额外奖励+打脸演出 — the data source T-087's
    /// settlement hidden tier consumes).
    /// </summary>
    /// <param name="row">The configured row of this level.</param>
    /// <param name="victory">Whether the level was won.</param>
    /// <param name="towersStanding">Weapon towers standing at the victory moment.</param>
    public static MockArcVerdict Judge(MockArcRow row, bool victory, int towersStanding)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!victory)
        {
            return MockArcVerdict.Miss("未通关——隐藏评价不判定");
        }

        if (towersStanding > row.LowConfig.MaxTowers)
        {
            return MockArcVerdict.Miss(
                $"塔数 {towersStanding} 超过低配阈值 {row.LowConfig.MaxTowers}——常规评价");
        }

        return new MockArcVerdict(
            true,
            row.HiddenReward.ChestTier,
            row.HiddenReward.ItemIds,
            row.HiddenReward.Lines,
            $"低配通关（{towersStanding}≤{row.LowConfig.MaxTowers} 塔）——隐藏评价『{(row.Kind == MockArcKind.FaceSlap ? "打脸" : "扮猪吃虎")}』");
    }
}

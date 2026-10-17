namespace EraCore.Legacy;

/// <summary>
/// The wonder-driven FINALE-LEVEL gate (T-135, 附录 F.3 奇观链第六个:
/// 纪元之门=终章奇观, 通关之门). Reads the wonder table plus the built-wonder
/// state of the legacy ledger and exposes whether the game's final level
///（campaign:level-e6-final, 堕落守望者 Boss 关）is enterable — the
/// 跃迁硬条件语义 terminal form: the five earlier chain members gate an
/// era JUMP (Precheck of the N→N+1 transition), the sixth gates the end of
/// the game itself（建成后终局关可通）.
/// </summary>
/// <remarks>
/// Same discipline as the sibling wonder boards — the wonder table stays
/// progression-agnostic, the ledger stays wonder-agnostic, this board is
/// the only place they meet (单一通道:
/// <see cref="WonderThroughputBoard"/> E3 世界博览会 →
/// <see cref="WonderRadarSharingBoard"/> E4 互联网 →
/// <see cref="WonderEnergyOutputBoard"/> E5 戴森云 → this board 终章纪元之门
/// —— the six-wonder chain of boards gains its sixth member). The gate is
/// ALL finale-gate wonders built（AND 族——通关之门只有一个成员，规则仍
/// 泛化为族语义与跳代 Precheck 同构）; a bonus_value on the finale row is
/// a fraction flag (wonders.json <c>_units</c> row "bonus_value:finale_gate"),
/// not a multiplier — the board does not consume it.
/// </remarks>
public sealed class WonderFinaleGateBoard
{
    /// <summary>The closed bonus-kind key of the finale-gate channel (wonders.json bonus_kind 值域).</summary>
    public const string FinaleGateBonusKind = "finale_gate";

    private readonly IReadOnlyList<WonderSpec> _wonders;
    private readonly LegacyLedger _ledger;

    /// <param name="wonders">The wonder table (wonders.json).</param>
    /// <param name="ledger">The legacy ledger — the built-wonder state source.</param>
    /// <exception cref="ArgumentNullException">wonders or ledger is null.</exception>
    public WonderFinaleGateBoard(IReadOnlyList<WonderSpec> wonders, LegacyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(wonders);
        ArgumentNullException.ThrowIfNull(ledger);
        _wonders = wonders;
        _ledger = ledger;
    }

    /// <summary>
    /// Whether every declared finale-gate wonder is BUILT — i.e. the final
    /// level（e6-final）is enterable. False until 纪元之门 stands: the F.3
    /// 通关之门 semantics（建成后终局关可通——levels/e6.json e6-final row's
    /// <c>requires_wonder</c> data bit is the level-side twin of this
    /// predicate）. With the frozen content exactly one finale-gate wonder
    /// exists (campaign:wonder-era-gate, era_index 6).
    /// </summary>
    public bool FinaleLevelEnterable
    {
        get
        {
            foreach (WonderSpec wonder in _wonders)
            {
                if (wonder.BonusKind == FinaleGateBonusKind && !_ledger.IsWonderBuilt(wonder.Id))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The gate's own refusal reason for an UNBUILT finale wonder (null when
    /// <see cref="FinaleLevelEnterable"/>): the id the progression layer
    /// names to the player（『先建纪元之门』提示位）. First unbuilt row in
    /// table order — deterministic（表序遍历，无哈希序）.
    /// </summary>
    public string? BlockingWonderId
    {
        get
        {
            foreach (WonderSpec wonder in _wonders)
            {
                if (wonder.BonusKind == FinaleGateBonusKind && !_ledger.IsWonderBuilt(wonder.Id))
                {
                    return wonder.Id;
                }
            }

            return null;
        }
    }
}

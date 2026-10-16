using EraCore.Contracts.Foundation;

namespace EraCore.Legacy;

/// <summary>
/// The wonder-driven RADAR full-map sharing channel (T-124, F-060: 互联网
/// =E4 奇观, 雷达全图共享 — 附录 F.3). Reads the wonder table plus the
/// built-wonder state of the legacy ledger and answers the single question
/// every radar-coverage consumer asks: <b>does one radar station cover the
/// whole map?</b> The tier/tower table stays wonder-agnostic, the wonder
/// table stays tower-agnostic, this board is the only place they meet
/// (单一通道, same discipline as <see cref="WonderThroughputBoard"/> for the
/// throughput multiplier).
/// </summary>
/// <remarks>
/// F.3 lists exactly one radar wonder (互联网, E4, 雷达全图共享); the board
/// still consults EVERY built row carrying the radar bonus kind — one row
/// per era is already enforced by the wonders loader, so the rule
/// generalizes without inventing new stacking semantics. The content value
/// <c>bonus_value = 1.0</c> is the share FRACTION: full-map sharing is a
/// binary channel (a radar either shares the whole grid or keeps its own
/// aura circle), so a declared fraction below 1.0 has no designed meaning
/// and is rejected at construction (content shape guard, same standing as
/// the throughput board's positive-fraction check).
/// </remarks>
public sealed class WonderRadarSharingBoard
{
    /// <summary>The closed bonus-kind key of the radar-sharing channel (wonders.json bonus_kind 值域).</summary>
    public const string RadarBonusKind = "radar_shared";

    private readonly IReadOnlyList<WonderSpec> _wonders;
    private readonly LegacyLedger _ledger;

    /// <param name="wonders">The wonder table (wonders.json).</param>
    /// <param name="ledger">The legacy ledger — the built-wonder state source.</param>
    /// <exception cref="ArgumentNullException">wonders or ledger is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A radar wonder's bonus_value is not a finite fraction ≥ 1.0 — content
    /// shape violation (full-map sharing is binary; partial-share fractions
    /// are not a designed channel).
    /// </exception>
    public WonderRadarSharingBoard(IReadOnlyList<WonderSpec> wonders, LegacyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(wonders);
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (WonderSpec wonder in wonders.Where(w => w.BonusKind == RadarBonusKind))
        {
            if (!double.IsFinite(wonder.BonusValue) || wonder.BonusValue < 1.0)
            {
                throw new InvalidOperationException(
                    $"Wonder '{wonder.Id}' declares {RadarBonusKind}={wonder.BonusValue} — must be a finite fraction >= 1.0 " +
                    "(雷达全图共享 is binary: 1.0 = the whole map shares one radar grid).");
            }
        }

        _wonders = wonders;
        _ledger = ledger;
    }

    /// <summary>
    /// Whether every radar station's coverage is the ENTIRE map: true iff a
    /// built wonder carries the radar bonus kind with the full share
    /// fraction (互联网 建成 ⇒ true; 内容冻结值 1.0).
    /// </summary>
    public bool IsFullMapShared
    {
        get
        {
            foreach (WonderSpec wonder in _wonders)
            {
                if (wonder.BonusKind == RadarBonusKind && _ledger.IsWonderBuilt(wonder.Id))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The effective radar coverage radius (cells) of ONE station on a
    /// <paramref name="mapWidth"/>×<paramref name="mapHeight"/> grid: the
    /// station's own <paramref name="stationRadarRadiusCells"/> aura (e.g.
    /// the radar tower row's 12) — or, once the internet wonder stands, the
    /// radius that spans the whole map from that station (the Chebyshev
    /// distance to the farthest corner, 最小通道裁定：全图化=半径顶格).
    /// </summary>
    public double EffectiveRadarRadiusCells(
        double stationRadarRadiusCells, int mapWidth, int mapHeight)
    {
        if (stationRadarRadiusCells <= 0 || !double.IsFinite(stationRadarRadiusCells))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stationRadarRadiusCells), stationRadarRadiusCells,
                "A radar station radius must be a positive finite number of cells.");
        }

        if (mapWidth < 1 || mapHeight < 1)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(mapWidth)}x{nameof(mapHeight)}", $"{mapWidth}x{mapHeight}",
                "A radar grid must be at least 1x1 cells.");
        }

        if (!IsFullMapShared)
        {
            return stationRadarRadiusCells;
        }

        // 全图共享: the coverage circle must reach the farthest corner of the
        // grid from the WORST-case station position — the far corner distance
        // upper bound over any station cell (Chebyshev corner span, converted
        // to the Euclidean radius that covers it).
        return Math.Sqrt((mapWidth - 1) * (mapWidth - 1) + (mapHeight - 1) * (mapHeight - 1));
    }

    /// <summary>
    /// Whether one radar station at <paramref name="station"/> covers
    /// <paramref name="cell"/> on the grid: shared ⇒ every in-bounds cell of
    /// the map; otherwise the Euclidean-distance aura circle (the same
    /// cells/px range convention the tower rows declare, K-4 cell domain).
    /// </summary>
    public bool IsCellCovered(
        CellCoord station, CellCoord cell, double stationRadarRadiusCells, int mapWidth, int mapHeight)
    {
        if (cell.X < 0 || cell.X >= mapWidth || cell.Y < 0 || cell.Y >= mapHeight)
        {
            return false; // 越界格不在任何雷达覆盖语义内
        }

        if (IsFullMapShared)
        {
            return true; // 雷达全图共享（互联网建成）——全图任意格被任意站覆盖
        }

        double radius = EffectiveRadarRadiusCells(stationRadarRadiusCells, mapWidth, mapHeight);
        int dx = cell.X - station.X;
        int dy = cell.Y - station.Y;
        return (dx * dx) + (dy * dy) <= (radius * radius) + 1e-9;
    }
}

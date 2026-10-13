using EraCore.Agent.Actions;
using EraCore.Contracts.Foundation;

namespace EraCore.Presentation;

// T-056 (C-23 建造交互薄版, F-036/F-119)：拖放建造的纯逻辑状态机。表现层
// （Godot 侧 BuildInteractionUi）只做事件翻译：鼠标按下→BeginDrag、移动→
// UpdateDrag（矩形从锚格到当前格，单格=1×1 矩形，与批量同一交互）、ESC→
// Cancel、松开→Commit。格子合法性直接复用 BuildPlan 的校验输出
// （BuildPlanAction.Draft 的逐步 Buildable/Rejection——绿/红格与拒绝码和
// 军师草案同源），批量格数同限（cells ≤ 4+2×E，F-036 玩家与系统同限）
// 经 <see cref="PlayerBuildLane.MaxBuildCells"/> 读 K-3 冻结表，本类不自持
// 数值。执行不经本类：Commit 产出 (towerId, cells)，由宿主交给
// PlayerBuildLane——同一咽喉（ActionRegistry→SimCommand）。

/// <summary>Interaction state of one drag session (idle / dragging / all-red invalid).</summary>
public enum BuildDragState
{
    /// <summary>No tower armed, no preview on the grid.</summary>
    Idle = 0,

    /// <summary>Tower armed, pointer defines a selection rectangle with at least one buildable cell.</summary>
    Dragging = 1,

    /// <summary>Tower armed but every selected cell is rejected — the release commits nothing.</summary>
    Invalid = 2,
}

/// <summary>One preview cell: green (<see cref="Buildable"/>) or red with the shared rejection code.</summary>
/// <param name="Cell">Selected cell.</param>
/// <param name="Buildable">True when the cell passes the BuildPlan draft checks now.</param>
/// <param name="Rejection">Shared rejection code when not buildable; null otherwise.</param>
public sealed record BuildDragPreviewCell(CellCoord Cell, bool Buildable, string? Rejection);

/// <summary>
/// The whole preview frame the UI paints: state, per-cell legality (BuildPlan
/// draft output), the batch-limit cap flag and the cost readout.
/// </summary>
/// <param name="State">Current state machine state.</param>
/// <param name="TowerId">Armed tower id (empty at idle).</param>
/// <param name="Cells">Selected cells in row-major rectangle order, already capped.</param>
/// <param name="CappedByLimit">True when the raw rectangle exceeded cells≤4+2×E and was truncated (front row-major cells kept).</param>
/// <param name="UnitCost">Per-tower cost the draft quoted (0 while unknown).</param>
/// <param name="TotalCost">Cost-currency total of the buildable cells.</param>
/// <param name="Affordable">True when the cost pool covers <see cref="TotalCost"/>.</param>
public sealed record BuildDragPreview(
    BuildDragState State,
    string TowerId,
    IReadOnlyList<BuildDragPreviewCell> Cells,
    bool CappedByLimit,
    double UnitCost,
    double TotalCost,
    bool Affordable);

/// <summary>
/// What a release hands the execution lane: the armed tower and the capped
/// cell list (the lane builds the action JSON and walks the shared
/// registry throat — nothing is executed from inside the controller).
/// </summary>
public sealed record BuildDragCommit(string TowerId, IReadOnlyList<CellCoord> Cells);

/// <summary>
/// Drag-drop build controller (T-056): idle/dragging/invalid state machine,
/// rectangle batch selection capped at the shared BuildPlan limit, and the
/// per-cell legality cache that keeps one drag session from re-drafting
/// every cell on every pointer move. Pure logic — no clock, no rng, no
/// engine types (CS-1.4/CS-1.1).
/// </summary>
public sealed class BuildDragController
{
    private readonly IActionWorldView _view;
    private readonly Func<int> _eraOrdinal;
    private readonly BuildPlanAction _draftAction = new();

    // Per-session legality cache: cell → draft verdict. Cleared on every
    // BeginDrag/Cancel/Commit (a new session re-reads the world through the
    // view, so a placement made between sessions can never paint a stale
    // green cell).
    private readonly Dictionary<CellCoord, BuildDragPreviewCell> _cellCache = new();

    private string? _towerId;
    private CellCoord _anchor = new(0, 0);
    private CellCoord _corner = new(0, 0);
    private bool _cappedByLimit;
    private IReadOnlyList<BuildDragPreviewCell> _cells = Array.Empty<BuildDragPreviewCell>();
    private double _unitCost;

    /// <param name="view">World view the BuildPlan drafts validate against (the same view the lane uses).</param>
    /// <param name="eraOrdinalProvider">Era ordinal for the shared cells≤4+2×E cap.</param>
    public BuildDragController(IActionWorldView view, Func<int> eraOrdinalProvider)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(eraOrdinalProvider);
        _view = view;
        _eraOrdinal = eraOrdinalProvider;
    }

    /// <summary>Current state machine state.</summary>
    public BuildDragState State { get; private set; } = BuildDragState.Idle;

    /// <summary>Cache hits this session — cells answered without a fresh draft.</summary>
    public long CacheHits { get; private set; }

    /// <summary>Drafts issued this session — the cache's miss counter.</summary>
    public int ViewDrafts { get; private set; }

    /// <summary>Arms a tower and anchors the selection rectangle at the pressed cell (Idle→Dragging).</summary>
    public void BeginDrag(string towerId, CellCoord anchorCell)
    {
        ArgumentNullException.ThrowIfNull(towerId);
        ArgumentNullException.ThrowIfNull(anchorCell);
        _towerId = towerId;
        _anchor = anchorCell;
        _corner = anchorCell;
        _cellCache.Clear();
        CacheHits = 0;
        ViewDrafts = 0;
        _unitCost = 0;
        Refresh();
    }

    /// <summary>Extends the selection rectangle to the pointer cell (single cell until the pointer leaves the anchor).</summary>
    public void UpdateDrag(CellCoord cornerCell)
    {
        ArgumentNullException.ThrowIfNull(cornerCell);
        if (State == BuildDragState.Idle)
        {
            return; // nothing armed — pointer motion is inert
        }

        _corner = cornerCell;
        Refresh();
    }

    /// <summary>ESC / right-cancel: drops the armed tower and the preview (any state→Idle).</summary>
    public void Cancel()
    {
        Reset();
    }

    /// <summary>
    /// Release: hands the capped buildable selection to the execution lane.
    /// Returns null when idle, when the tower is unknown to the catalog, or
    /// when every selected cell is red (the UI gate — anything actually sent
    /// is still validated by the registry, the single authority).
    /// </summary>
    public BuildDragCommit? Commit()
    {
        if (State != BuildDragState.Dragging || _towerId is null)
        {
            Reset();
            return null;
        }

        var cells = new List<CellCoord>(_cells.Count);
        foreach (BuildDragPreviewCell cell in _cells)
        {
            if (cell.Buildable)
            {
                cells.Add(cell.Cell);
            }
        }

        if (cells.Count == 0)
        {
            Reset();
            return null;
        }

        var commit = new BuildDragCommit(_towerId, cells);
        Reset();
        return commit;
    }

    /// <summary>The current preview frame (recomputed from the session cache; cheap enough per pointer move).</summary>
    public BuildDragPreview Preview()
    {
        double total = 0;
        bool anyBuildable = false;
        foreach (BuildDragPreviewCell cell in _cells)
        {
            if (cell.Buildable)
            {
                anyBuildable = true;
                total += _unitCost;
            }
        }

        double available = _view.ResourceAmount(_view.CostCurrency);
        return new BuildDragPreview(
            State,
            _towerId ?? string.Empty,
            _cells,
            _cappedByLimit,
            _unitCost,
            total,
            available >= total && anyBuildable);
    }

    private void Refresh()
    {
        State = BuildDragState.Dragging;
        var selection = RectangleCells(_anchor, _corner);
        int cap = PlayerBuildLane.MaxBuildCells(_eraOrdinal());
        _cappedByLimit = selection.Count > cap;
        if (_cappedByLimit)
        {
            selection.RemoveRange(cap, selection.Count - cap);
        }

        var previewCells = new List<BuildDragPreviewCell>(selection.Count);
        bool anyBuildable = false;
        foreach (CellCoord cell in selection)
        {
            if (_cellCache.TryGetValue(cell, out BuildDragPreviewCell? cached))
            {
                CacheHits++;
                previewCells.Add(cached);
            }
            else
            {
                BuildDragPreviewCell fresh = DraftCell(cell);
                _cellCache.Add(cell, fresh);
                ViewDrafts++;
                previewCells.Add(fresh);
            }

            if (previewCells[^1].Buildable)
            {
                anyBuildable = true;
            }
        }

        _cells = previewCells;
        if (_towerId is null || !_view.TryGetTowerCost(_towerId, out _) || !anyBuildable)
        {
            State = BuildDragState.Invalid;
        }
    }

    private BuildDragPreviewCell DraftCell(CellCoord cell)
    {
        // Single-cell BuildPlan draft — the SAME validation the registry will
        // run at commit time; the preview merely shows its output early.
        BuildPlanDraft? draft = _draftAction.Draft(_view, _towerId!, new[] { cell }, confirmOverwrite: false);
        if (draft is null)
        {
            return new BuildDragPreviewCell(cell, Buildable: false, "tower-unknown");
        }

        _unitCost = draft.UnitCost;
        BuildPlanStep step = draft.Steps[0];
        return new BuildDragPreviewCell(cell, step.Buildable, step.Rejection);
    }

    private void Reset()
    {
        _towerId = null;
        _cells = Array.Empty<BuildDragPreviewCell>();
        _cellCache.Clear();
        _cappedByLimit = false;
        _unitCost = 0;
        State = BuildDragState.Idle;
    }

    /// <summary>Row-major rectangle between the two corners (inclusive; a 1×1 rectangle is a single cell).</summary>
    private static List<CellCoord> RectangleCells(CellCoord anchor, CellCoord corner)
    {
        int minX = Math.Min(anchor.X, corner.X);
        int maxX = Math.Max(anchor.X, corner.X);
        int minY = Math.Min(anchor.Y, corner.Y);
        int maxY = Math.Max(anchor.Y, corner.Y);
        var cells = new List<CellCoord>((maxX - minX + 1) * (maxY - minY + 1));
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                cells.Add(new CellCoord(x, y));
            }
        }

        return cells;
    }
}

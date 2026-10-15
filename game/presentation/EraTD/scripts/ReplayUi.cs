using System.Text;
using EraCore.Agent.Pipeline;
using EraCore.Contracts.Journal;
using EraCore.IO;
using Godot;

namespace EraTD;

// T-111 (C-30/C-22, F-121/F-174) 战报回放 UI：JSONL 离线加载（存档目录
// 浏览——reports/ 下 *.jsonl 文件列表）+ 时间轴回放（复用 T-053
// JournalReplayer 决策轨迹与 T-111 ReplayTimeline 四色事件流：军令/审批/
// 执行/汇报，可跳转到任意 tick）+ 回放摘要 md 导出（WarReportMarkdown）。
// 分层纪律（CS-1.6）：本节点零回放业务规则——时间轴投影/seek 切割/报告
// 生成全部在 EraCore 纯逻辑（已测）；这里只做文件列举、节点镜像与输入
// 接线。F3 开关；headless 下自带冒烟并打印 REPLAY_UI_OK（smoke 判据，
// 失败 Quit(1) 早于 SMOKE_OK）。

/// <summary>
/// The T-111 replay control: offline archive browser (reports/*.jsonl) +
/// four-lane timeline with arbitrary-tick seek + markdown export. Loads run
/// through <see cref="JsonlJournalWriter.ReadAll"/> (share-read: a live
/// run's journal loads while its writer holds the file).
/// </summary>
public sealed partial class ReplayUi : Control
{
    /// <summary>Archive sub-directory of the D-03 data root (SimHost journal convention).</summary>
    internal const string ReportsDir = "reports";

    /// <summary>Render cap for the event lane (recent-tail window; the seek domain stays full).</summary>
    private const int MaxRenderedEntries = 300;

    private SimHost _host = null!;
    private IFileStore _files = null!;

    private ItemList _fileList = null!;
    private Button _loadButton = null!;
    private Button _refreshButton = null!;
    private Label _loadedLabel = null!;
    private HSlider _tickSlider = null!;
    private Label _tickLabel = null!;
    private Button _latestButton = null!;
    private VBoxContainer _eventBox = null!;
    private Button _exportButton = null!;
    private Label _statusText = null!;

    private ReplayTimelineIndex _timeline =
        ReplayTimeline.Build(Array.Empty<JournalEvent>());
    private IReadOnlyList<DecisionTrace> _traces = [];
    private string? _loadedFileName;

    /// <summary>Scene entry (Main.cs single-line wiring, same lane as the other HUDs).</summary>
    internal static ReplayUi Instantiate(Main main, SimHost host, IFileStore? files = null)
    {
        var ui = new ReplayUi
        {
            _host = host,
            _files = files ?? CreateDefaultFiles(),
        };
        main.AddChild(ui); // _Ready builds the panel (and smokes headless)
        return ui;
    }

    /// <summary>
    /// File root: live runs read/write the real D-03 dev root (where the run
    /// journal lands); headless smoke runs get a throwaway root under the
    /// same <c>.tmp-tests</c> convention so CI archives stay deterministic.
    /// </summary>
    private static IFileStore CreateDefaultFiles()
    {
        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            string root = System.IO.Path.Combine(
                DevFileStore.DefaultDevRoot, ".tmp-tests", "replay-smoke");
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }

            return new DevFileStore(root);
        }

        return new DevFileStore();
    }

    public override void _Ready()
    {
        Visible = false; // F3 toggles (headless smoke drives internals directly)
        ZIndex = 26;
        BuildUi();
        RefreshArchive();

        if (OS.HasFeature("headless") || DisplayServer.GetName() == "headless")
        {
            RunHeadlessSmoke();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Keycode: Key.F3 })
        {
            TogglePanel();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>F3 toggle（同一入口，冒烟不经过输入层）。</summary>
    internal void TogglePanel()
    {
        Visible = !Visible;
        if (Visible)
        {
            RefreshArchive();
        }
    }

    // ---- construction -----------------------------------------------------

    private void BuildUi()
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", HoloSkin.BuildHoloBox());

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);

        var titleRow = new HBoxContainer();
        var title = Dim("◈ 战报回放 · F3 开关");
        title.AddThemeFontSizeOverride("font_size", 12);
        var close = new Button { Text = "关闭", FocusMode = FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        titleRow.AddChild(title);
        titleRow.AddChild(close);

        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 8);

        // 左列：存档目录浏览（jsonl 文件列表）
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 4);
        var listCaption = Dim($"战报文件（{ReportsDir}/）");
        left.AddChild(listCaption);
        _fileList = new ItemList { CustomMinimumSize = new Vector2(170, 150) };
        _fileList.ItemSelected += _ => OnLoadPressed(); // 单击即选中，配合「载入」
        left.AddChild(_fileList);
        var listRow = new HBoxContainer();
        _refreshButton = new Button { Text = "刷新", FocusMode = FocusModeEnum.None };
        _refreshButton.Pressed += () => _ = RefreshArchive();
        _loadButton = new Button { Text = "载入所选", FocusMode = FocusModeEnum.None };
        _loadButton.Pressed += OnLoadPressed;
        listRow.AddChild(_refreshButton);
        listRow.AddChild(_loadButton);
        left.AddChild(listRow);
        _loadedLabel = Dim("未载入");
        left.AddChild(_loadedLabel);

        // 右列：时间轴（滑杆任意 tick 跳转 + 四色事件流）与导出
        var right = new VBoxContainer();
        right.AddThemeConstantOverride("separation", 4);
        var sliderRow = new HBoxContainer();
        _tickSlider = new HSlider
        {
            CustomMinimumSize = new Vector2(220, 16),
            MinValue = 0,
            MaxValue = 0,
            Step = 1d,
            FocusMode = FocusModeEnum.None,
        };
        _tickSlider.ValueChanged += value => SeekTo((long)value);
        _tickLabel = Dim("tick -");
        _tickLabel.CustomMinimumSize = new Vector2(84, 0);
        _latestButton = new Button { Text = "跳到最新", FocusMode = FocusModeEnum.None };
        _latestButton.Pressed += () => SeekTo(_timeline.MaxTick);
        sliderRow.AddChild(_tickSlider);
        sliderRow.AddChild(_tickLabel);
        sliderRow.AddChild(_latestButton);

        var legend = Dim("四色：军令(青) 审批(金) 执行(绿) 汇报(白)");
        legend.AddThemeFontSizeOverride("font_size", 9);
        _eventBox = new VBoxContainer();
        _eventBox.AddThemeConstantOverride("separation", 1);
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(330, 150),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(_eventBox);

        var exportRow = new HBoxContainer();
        _exportButton = new Button { Text = "导出回放摘要(.md)", FocusMode = FocusModeEnum.None };
        _exportButton.Pressed += OnExportPressed;
        _statusText = Dim("选择左侧战报文件载入。");
        _statusText.CustomMinimumSize = new Vector2(160, 0);
        _statusText.AddThemeFontSizeOverride("font_size", 10);
        exportRow.AddChild(_exportButton);
        exportRow.AddChild(_statusText);

        right.AddChild(sliderRow);
        right.AddChild(legend);
        right.AddChild(scroll);
        right.AddChild(exportRow);

        body.AddChild(left);
        body.AddChild(right);
        box.AddChild(titleRow);
        box.AddChild(body);
        panel.AddChild(box);
        AddChild(panel);
        panel.Position = new Vector2(90, 60);
    }

    private static Label Dim(string text)
    {
        var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", HoloSkin.TextDimCyan);
        return label;
    }

    // ---- archive browsing --------------------------------------------------

    /// <summary>
    /// Rescans the reports directory (offline archive list). Deterministic
    /// order (ordinal by file name) so the smoke count is stable.
    /// </summary>
    /// <returns>The file names listed.</returns>
    internal IReadOnlyList<string> RefreshArchive()
    {
        string dir = System.IO.Path.Combine(_files.Root, ReportsDir);
        var names = new List<string>();
        if (System.IO.Directory.Exists(dir))
        {
            names.AddRange(
                System.IO.Directory.EnumerateFiles(dir, "*.jsonl")
                    .Select(System.IO.Path.GetFileName)
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .Order(StringComparer.Ordinal));
        }

        _fileList.Clear();
        foreach (string name in names)
        {
            _fileList.AddItem(name);
        }

        return names;
    }

    private void OnLoadPressed()
    {
        string[] names = RefreshArchive().ToArray();
        int[] selected = _fileList.GetSelectedItems();
        if (selected.Length == 0 || selected[0] >= names.Length)
        {
            _statusText.Text = "先在左侧选择一个 .jsonl 战报。";
            return;
        }

        LoadArchiveFile(names[selected[0]]);
    }

    /// <summary>
    /// Loads one archive file offline: journal read (share-read) → decision
    /// traces (<see cref="JournalReplayer.Replay"/>) + timeline
    /// (<see cref="ReplayTimeline.Build"/>) → cursor at the latest tick.
    /// </summary>
    /// <returns>True when the file replayed (integrity + at least one order).</returns>
    internal bool LoadArchiveFile(string fileName)
    {
        string path = System.IO.Path.Combine(_files.Root, ReportsDir, fileName);
        if (!System.IO.File.Exists(path))
        {
            _statusText.Text = $"文件不存在：{fileName}";
            return false;
        }

        IReadOnlyList<JournalEvent> events;
        IReadOnlyList<DecisionTrace> traces;
        try
        {
            events = JsonlJournalWriter.ReadAll(path);
            traces = JournalReplayer.Replay(path);
        }
        catch (Exception ex)
        {
            // 战报不可回放=战报损坏——如实可见，不静默吞（K-8 口径）
            _statusText.Text = $"回放失败：{ex.GetType().Name}";
            return false;
        }

        _traces = traces;
        _timeline = ReplayTimeline.Build(events);
        _loadedFileName = fileName;
        _loadedLabel.Text = $"已载入：{fileName}（{traces.Count} 军令 / {_timeline.Entries.Count} 事件）";
        _tickSlider.MinValue = _timeline.MinTick;
        _tickSlider.MaxValue = Math.Max(_timeline.MinTick, _timeline.MaxTick);
        SeekTo(_timeline.MaxTick);
        _statusText.Text = "已按时间轴载入——拖动滑杆跳转任意 tick。";
        return traces.Count > 0;
    }

    // ---- timeline mirror ---------------------------------------------------

    /// <summary>
    /// Seek to an arbitrary tick (slider path and smoke path share this):
    /// re-renders the four-lane event prefix
    /// (<see cref="ReplayTimeline.EntriesUpTo"/> — 纯切割在 EraCore，已测).
    /// </summary>
    internal void SeekTo(long tick)
    {
        IReadOnlyList<ReplayTimelineEntry> prefix =
            ReplayTimeline.EntriesUpTo(_timeline, tick);

        foreach (Node child in _eventBox.GetChildren())
        {
            _eventBox.RemoveChild(child);
            child.QueueFree();
        }

        // 渲染窗：最近 MaxRenderedEntries 条（seek 域不变——被裁掉的更早
        // 事件在导出报告里完整保留）
        IEnumerable<ReplayTimelineEntry> window = prefix.Count > MaxRenderedEntries
            ? prefix.Skip(prefix.Count - MaxRenderedEntries)
            : prefix;
        foreach (ReplayTimelineEntry entry in window)
        {
            var label = new Label
            {
                Text = $"t{entry.Tick} #{entry.Order} [{ReplayTimeline.StageLabelZh(entry.Stage)}] {entry.Summary}",
                MouseFilter = MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", 10);
            label.AddThemeColorOverride("font_color", LaneColor(entry.Stage));
            _eventBox.AddChild(label);
        }

        if (prefix.Count > MaxRenderedEntries)
        {
            var note = Dim($"（仅显示最近 {MaxRenderedEntries} 条 / 共 {prefix.Count} 条）");
            note.AddThemeFontSizeOverride("font_size", 9);
            _eventBox.AddChild(note);
        }

        _tickLabel.Text = $"tick {tick} / {_timeline.MaxTick}（{prefix.Count} 事件）";
        _tickSlider.SetValueNoSignal(Math.Clamp(tick, _timeline.MinTick, _timeline.MaxTick));
    }

    private static Color LaneColor(ReplayStage stage) => stage switch
    {
        ReplayStage.Order => HoloSkin.EdgeCyan,                       // 军令=青
        ReplayStage.Approval => new Color(1.00f, 0.84f, 0.40f),      // 审批=金
        ReplayStage.Execution => new Color(0.55f, 0.95f, 0.55f),     // 执行=绿
        _ => new Color(0.92f, 0.92f, 0.92f),                         // 汇报=白
    };

    /// <summary>Currently rendered lane rows (smoke probe: seek prefix evidence).</summary>
    internal IReadOnlyList<string> RenderedLaneRows()
    {
        var rows = new List<string>();
        foreach (Node child in _eventBox.GetChildren())
        {
            if (child is Label label && label.Text.StartsWith("t", StringComparison.Ordinal))
            {
                rows.Add(label.Text);
            }
        }

        return rows;
    }

    // ---- export ------------------------------------------------------------

    private void OnExportPressed()
    {
        if (ExportMarkdown() is null)
        {
            _statusText.Text = "先载入一局战报再导出。";
        }
    }

    /// <summary>
    /// Writes the replay-summary markdown next to the loaded journal
    /// (reports/&lt;stem&gt;.report.md, through the D-03 store). Returns the
    /// report text, or null when nothing is loaded.
    /// </summary>
    internal string? ExportMarkdown()
    {
        if (_loadedFileName is null)
        {
            return null;
        }

        string markdown = WarReportMarkdown.Build(_loadedFileName, _traces, _timeline);
        string relative = $"{ReportsDir}/" +
            System.IO.Path.GetFileNameWithoutExtension(_loadedFileName) + ".report.md";
        _files.WriteAllBytes(relative, Encoding.UTF8.GetBytes(markdown));
        _statusText.Text = $"已导出：{relative}（{_traces.Count} 军令）";
        return markdown;
    }

    /// <summary>The loaded timeline index (smoke probe).</summary>
    internal ReplayTimelineIndex Timeline => _timeline;

    /// <summary>The loaded decision traces (smoke probe).</summary>
    internal IReadOnlyList<DecisionTrace> Traces => _traces;

    // ---- headless smoke (acceptance) ---------------------------------------

    /// <summary>
    /// T-111 acceptance run (工具+E 级): 任意局战报可离线加载并按时间轴回放。
    /// 判据（smoke 段口径）：①离线加载——真实存档目录浏览（本进程真管线产出的
    /// 战报复制两份入一次性根 → files=2）+ 载入重建决策轨迹/时间轴（load=ok，
    /// 四 lane 齐备）；②任意 tick 跳转——MinTick-1 空、首执行 tick 前无执行、
    /// 最新 tick 全量（seek=ok，走真实滑杆同一切割入口）；③导出——md 报告落
    /// reports/ 且含汇总段+逐军令节（export=md）。打印 REPLAY_UI_OK；失败
    /// Quit(1) 早于 SMOKE_OK。
    /// </summary>
    private void RunHeadlessSmoke()
    {
        var problems = new List<string>();

        // ①离线加载：真实战报（本进程管线落盘的 K-8 流）复制两份进一次性
        // 存档根——文件列举与计数确定性（files=2，不依赖开发机历史存档）
        byte[] liveBytes = ReadSharedAllBytes(_host.JournalPath);
        if (liveBytes.Length == 0)
        {
            problems.Add("本进程战报为空（前置 PANEL/CINE 段应已产事件）");
        }

        _files.WriteAllBytes($"{ReportsDir}/run-a.jsonl", liveBytes);
        _files.WriteAllBytes($"{ReportsDir}/run-b.jsonl", liveBytes);
        IReadOnlyList<string> archive = RefreshArchive();
        if (archive.Count != 2 || !archive.Contains("run-a.jsonl"))
        {
            problems.Add($"存档目录浏览应列 2 件 jsonl，实得 {archive.Count}");
        }

        bool loaded = LoadArchiveFile("run-a.jsonl");
        if (!loaded || _traces.Count < 1 || _timeline.Entries.Count < 6)
        {
            problems.Add($"离线加载未重建决策轨迹/时间轴：loaded={loaded} orders={_traces.Count} entries={_timeline.Entries.Count}");
        }

        foreach (ReplayStage stage in Enum.GetValues<ReplayStage>())
        {
            if (!_timeline.Entries.Any(entry => entry.Stage == stage))
            {
                problems.Add($"四色事件流缺 {stage} lane");
            }
        }

        // ②任意 tick 跳转（真实滑杆同一切割入口）
        SeekTo(_timeline.MinTick - 1);
        bool beforeStartEmpty = RenderedLaneRows().Count == 0;
        long firstExecutionTick = _timeline.Entries
            .First(entry => entry.Stage == ReplayStage.Execution).Tick;
        SeekTo(firstExecutionTick - 1);
        IReadOnlyList<string> beforeExecution = RenderedLaneRows();
        bool noExecutionYet = beforeExecution.All(row =>
            !row.Contains("[执行]", StringComparison.Ordinal));
        SeekTo(_timeline.MaxTick);
        IReadOnlyList<string> allRows = RenderedLaneRows();
        bool allRendered = allRows.Count == Math.Min(_timeline.Entries.Count, MaxRenderedEntries)
            && allRows.Any(row => row.Contains("[执行]", StringComparison.Ordinal))
            && allRows.Any(row => row.Contains("[军令]", StringComparison.Ordinal));
        if (!beforeStartEmpty || !noExecutionYet || !allRendered)
        {
            problems.Add(
                $"seek 切割未按预期：beforeStart={beforeStartEmpty} noExec={noExecutionYet} all={allRendered}");
        }

        // ③导出：md 报告落存档根且含汇总段+逐军令节
        string? markdown = ExportMarkdown();
        bool exportOk = markdown is not null
            && markdown.Contains("# 战报回放摘要：run-a.jsonl", StringComparison.Ordinal)
            && markdown.Contains("## 一局汇总", StringComparison.Ordinal)
            && markdown.Contains("## 军令 #", StringComparison.Ordinal)
            && _files.Exists($"{ReportsDir}/run-a.report.md");
        if (!exportOk)
        {
            problems.Add("md 导出未按预期（汇总/逐军令节/落盘）");
        }

        if (problems.Count == 0)
        {
            GD.Print(
                $"REPLAY_UI_OK files={archive.Count} load=ok seek=ok export=md " +
                $"orders={_traces.Count} entries={_timeline.Entries.Count} stages=4 " +
                $"tick_axis={_timeline.MinTick}-{_timeline.MaxTick} offline=no-sim");
        }
        else
        {
            GD.PushError("REPLAY_UI_FAIL " + string.Join("; ", problems));
            GD.Print("REPLAY_UI_FAIL " + string.Join("; ", problems));
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// Reads a file the live journal writer still holds (share ReadWrite —
    /// the same sharing contract as <see cref="JsonlJournalWriter.ReadAll"/>).
    /// </summary>
    private static byte[] ReadSharedAllBytes(string path)
    {
        using var stream = new System.IO.FileStream(
            path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

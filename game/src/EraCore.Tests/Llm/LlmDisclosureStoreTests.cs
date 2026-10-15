using EraCore.IO;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

// [F-147] [C-31] T-109 首启数据流声明状态机（LlmDisclosureStore 持久化 +
// LlmDisclosureGate 纯门）：缺省未启（弹窗到期）、确认跨运行记忆、坏文件
// 回「未启」（披露的诚实失败方向=宁可再弹、永不静默视为已确认）、门矩阵
// 八组合=系统开∧云档可用∧未确认（关闭 AI ⇒ 永不弹；Web ⇒ 无可启用 AI 不
// 弹）。沙盒同 PanelSettingsStoreTests（D 盘开发根下 .tmp-tests）。
public class LlmDisclosureStoreTests : IDisposable
{
    // Scratch sandbox under the pinned dev root (D-drive only; data/ is gitignored runtime data).
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public LlmDisclosureStoreTests()
    {
        _store = new DevFileStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- legal

    [Fact] // legal — 缺文件：出厂缺省=未启（首启声明到期须弹，不得静默跳过）
    public void Load_missing_file_yields_the_unacknowledged_factory_default()
    {
        LlmDisclosureSettings loaded = LlmDisclosureStore.Load(_store);

        Assert.False(loaded.Acknowledged);
        Assert.False(LlmDisclosureSettings.Default.Acknowledged);
        Assert.False(_store.Exists(LlmDisclosureStore.RelativePath));
    }

    [Fact] // legal — 往返：确认 → 保存 → 重载仍是已确认（跨运行永不再弹的记忆源）
    public void Save_then_load_roundtrips_the_acknowledged_state()
    {
        LlmDisclosureStore.Save(_store, new LlmDisclosureSettings { Acknowledged = true });

        Assert.True(LlmDisclosureStore.Load(_store).Acknowledged);
    }

    [Fact] // legal — 线上格式：紧凑 JSON，成员名与记录参数对齐（可人工检视）
    public void Save_writes_compact_json_under_the_settings_relative_path()
    {
        LlmDisclosureStore.Save(_store, new LlmDisclosureSettings { Acknowledged = true });

        Assert.True(_store.Exists(LlmDisclosureStore.RelativePath));
        string json = System.Text.Encoding.UTF8.GetString(
            _store.ReadAllBytes(LlmDisclosureStore.RelativePath));
        Assert.Equal("""{"Acknowledged":true}""", json);
    }

    [Theory] // legal — 门矩阵八组合：弹窗到期 ⟺ 系统开 ∧ 云档可用 ∧ 未确认
    [InlineData(true, true, true, false)]   // 未启+系统开+云可用 → 弹
    [InlineData(true, true, false, true)]   // 已确认 → 永不再弹
    [InlineData(false, true, true, false)]  // 系统关（AI 关）→ 永不弹
    [InlineData(false, true, false, false)] // 系统关+已确认 → 不弹
    [InlineData(true, false, true, false)]  // Web（云档平台禁用）→ 无可启用的 AI 不弹
    [InlineData(true, false, false, false)] // Web+已确认 → 不弹
    [InlineData(false, false, true, false)] // 系统关+Web → 不弹
    [InlineData(false, false, false, false)] // 三条件全反 → 不弹
    public void Gate_matrix_prompts_only_when_enabled_available_and_unacknowledged(
        bool aiSystemEnabled, bool cloudTierAvailable, bool acknowledged, bool expected)
    {
        Assert.Equal(
            expected,
            LlmDisclosureGate.PromptRequired(aiSystemEnabled, cloudTierAvailable, acknowledged));
    }

    [Fact] // legal — 端到端状态机：未启弹 → 确认落盘 → 门即关闭（设置页 Open 判据同源）
    public void Acknowledge_round_trip_closes_the_gate()
    {
        Assert.True(LlmDisclosureGate.PromptRequired(
            aiSystemEnabled: true, cloudTierAvailable: true,
            acknowledged: LlmDisclosureStore.Load(_store).Acknowledged));

        LlmDisclosureStore.Save(_store, new LlmDisclosureSettings { Acknowledged = true });

        Assert.False(LlmDisclosureGate.PromptRequired(
            aiSystemEnabled: true, cloudTierAvailable: true,
            acknowledged: LlmDisclosureStore.Load(_store).Acknowledged));
    }

    // ------------------------------------------------------------ boundary

    [Fact] // boundary — 坏 JSON：回「未启」，不抛（披露状态损坏不阻断启动）
    public void Load_corrupt_file_falls_back_to_unacknowledged()
    {
        _store.WriteAllBytes(LlmDisclosureStore.RelativePath, new byte[] { 0x7B, 0x00, 0xFF });

        Assert.False(LlmDisclosureStore.Load(_store).Acknowledged);
    }

    [Fact] // boundary — 缺 required 成员：解码抛 JsonException → 回「未启」
    // （损坏文件永不把声明静默视为已确认——诚实失败方向）
    public void Load_file_missing_the_required_member_falls_back_unacknowledged()
    {
        _store.WriteAllBytes(
            LlmDisclosureStore.RelativePath, System.Text.Encoding.UTF8.GetBytes("{}"));

        Assert.False(LlmDisclosureStore.Load(_store).Acknowledged);
    }

    [Fact] // boundary — null 文档：回「未启」（与坏 JSON 同道）
    public void Load_null_document_falls_back_to_unacknowledged()
    {
        _store.WriteAllBytes(
            LlmDisclosureStore.RelativePath, System.Text.Encoding.UTF8.GetBytes("null"));

        Assert.False(LlmDisclosureStore.Load(_store).Acknowledged);
    }

    // ------------------------------------------------------------ illegal

    [Fact] // illegal — null store / null settings 拒绝
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => LlmDisclosureStore.Load(null!));
        Assert.Throws<ArgumentNullException>(
            () => LlmDisclosureStore.Save(_store, null!));
        Assert.Throws<ArgumentNullException>(
            () => LlmDisclosureStore.Save(null!, LlmDisclosureSettings.Default));
    }
}

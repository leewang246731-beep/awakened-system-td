using EraCore.Cinematics;
using EraCore.IO;
using Xunit;

namespace EraCore.Tests.Cinematics;

// [C-24] [F-113] [F-114] T-086 演出完成标记入存档：不重复播放（IsCompleted
// 门）+可回看（Mark/Forget 往返）、缺文件/坏 JSON/缺 required 成员回出厂
// 缺省（进度标记永不阻断启动）、紧凑线上格式、幂等标记、空 id 拒绝。
// 沙盒同 PanelSettingsStoreTests（D 盘开发根下 .tmp-tests，gitignore 运行时数据）。
public class CinematicProgressStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public CinematicProgressStoreTests()
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

    [Fact] // legal — 缺文件：出厂缺省（什么都没看过）
    public void Load_missing_file_yields_the_factory_default()
    {
        CinematicProgress progress = CinematicProgressStore.Load(_store);

        Assert.Empty(progress.CompletedIds);
        Assert.Empty(CinematicProgress.Default.CompletedIds);
        Assert.False(CinematicProgressStore.IsCompleted(progress, "cinematic:e1-aha"));
        Assert.False(_store.Exists(CinematicProgressStore.RelativePath));
    }

    [Fact] // legal — 往返：标记两条→保存→重载仍在（跨运行不重复播放的存档面）
    public void Save_then_load_roundtrips_completed_ids()
    {
        CinematicProgress marked = CinematicProgressStore.MarkCompleted(
            CinematicProgressStore.MarkCompleted(CinematicProgress.Default, "cinematic:e1-aha"),
            "cinematic:e2-awaken");
        CinematicProgressStore.Save(_store, marked);

        CinematicProgress reloaded = CinematicProgressStore.Load(_store);

        Assert.Equal(new[] { "cinematic:e1-aha", "cinematic:e2-awaken" }, reloaded.CompletedIds);
        Assert.True(CinematicProgressStore.IsCompleted(reloaded, "cinematic:e1-aha"));
        Assert.True(CinematicProgressStore.IsCompleted(reloaded, "cinematic:e2-awaken"));
    }

    [Fact] // legal — 可回看：Forget 摘除标记后钩子位重新武装
    public void Forget_re_arms_the_hook_for_a_rewatch()
    {
        CinematicProgress marked = CinematicProgressStore.MarkCompleted(
            CinematicProgress.Default, "cinematic:e1-aha");

        CinematicProgress forgot = CinematicProgressStore.Forget(marked, "cinematic:e1-aha");

        Assert.False(CinematicProgressStore.IsCompleted(forgot, "cinematic:e1-aha"));
        Assert.Equal(forgot, CinematicProgressStore.Forget(forgot, "cinematic:e1-aha")); // 幂等
        Assert.True(CinematicProgressStore.IsCompleted(
            CinematicProgressStore.MarkCompleted(forgot, "cinematic:e1-aha"), "cinematic:e1-aha")); // 重标复原
    }

    [Fact] // legal — 标记幂等且保序（首完成位不变）
    public void MarkCompleted_is_idempotent_and_keeps_first_completion_order()
    {
        CinematicProgress once = CinematicProgressStore.MarkCompleted(
            CinematicProgress.Default, "cinematic:e1-aha");
        CinematicProgress twice = CinematicProgressStore.MarkCompleted(once, "cinematic:e1-aha");
        CinematicProgress mixed = CinematicProgressStore.MarkCompleted(twice, "cinematic:e2-awaken");

        Assert.Equal(once, twice);
        Assert.Equal(new[] { "cinematic:e1-aha", "cinematic:e2-awaken" }, mixed.CompletedIds);
    }

    [Fact] // legal — 线上格式：紧凑 JSON（可人工检视）
    public void Save_writes_compact_json_under_the_cinematics_relative_path()
    {
        CinematicProgressStore.Save(
            _store, CinematicProgressStore.MarkCompleted(CinematicProgress.Default, "cinematic:e1-aha"));

        Assert.True(_store.Exists(CinematicProgressStore.RelativePath));
        string json = System.Text.Encoding.UTF8.GetString(
            _store.ReadAllBytes(CinematicProgressStore.RelativePath));
        Assert.Equal("""{"CompletedIds":["cinematic:e1-aha"]}""", json);
    }

    // -------------------------------------------------------------- boundary

    [Fact] // boundary — 坏 JSON：回出厂缺省，不抛（进度损坏=重播一场，不阻断启动）
    public void Load_corrupt_file_falls_back_to_the_factory_default()
    {
        _store.WriteAllBytes(CinematicProgressStore.RelativePath, new byte[] { 0x7B, 0x00, 0xFF });

        Assert.Equal(CinematicProgress.Default, CinematicProgressStore.Load(_store));
    }

    [Fact] // boundary — 缺 required 成员的 JSON：回出厂缺省（源生成解码拒绝半截文件）
    public void Load_member_incomplete_file_falls_back_to_the_factory_default()
    {
        _store.WriteAllBytes(
            CinematicProgressStore.RelativePath, System.Text.Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(CinematicProgress.Default, CinematicProgressStore.Load(_store));
    }

    // -------------------------------------------------------------- illegal

    [Theory] // illegal — 空/空白脚本 id：逐 API 拒绝
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_script_ids_are_rejected(string? scriptId)
    {
        Assert.Throws<ArgumentException>(
            () => CinematicProgressStore.IsCompleted(CinematicProgress.Default, scriptId!));
        Assert.Throws<ArgumentException>(
            () => CinematicProgressStore.MarkCompleted(CinematicProgress.Default, scriptId!));
        Assert.Throws<ArgumentException>(
            () => CinematicProgressStore.Forget(CinematicProgress.Default, scriptId!));
    }

    [Fact] // illegal — null 入参：逐 API 拒绝
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => CinematicProgressStore.Load(null!));
        Assert.Throws<ArgumentNullException>(
            () => CinematicProgressStore.Save(_store, null!));
        Assert.Throws<ArgumentNullException>(
            () => CinematicProgressStore.IsCompleted(null!, "cinematic:e1-aha"));
        Assert.Throws<ArgumentNullException>(
            () => CinematicProgressStore.MarkCompleted(null!, "cinematic:e1-aha"));
        Assert.Throws<ArgumentNullException>(
            () => CinematicProgressStore.Forget(null!, "cinematic:e1-aha"));
    }
}

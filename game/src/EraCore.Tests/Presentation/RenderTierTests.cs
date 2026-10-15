using EraCore.IO;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-21] [F-159] T-102 渲染三档纯逻辑：能力矩阵冻结表（三档差异可测）、
// 自动探测选择器（Web feature + 首次 60 帧帧率自适应）与偏好持久化
// （PanelSettingsStore 先例）。三行必备：legal=表值/选择/往返，
// boundary=阈值线与损坏文件，illegal=未知枚举/负数/null 拒绝。
public class RenderTierTests : IDisposable
{
    // Scratch sandbox under the pinned dev root (D-drive only; data/ is gitignored runtime data).
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public RenderTierTests()
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

    // ------------------------------------------------- matrix (三档差异可测)

    [Fact] // legal — L 档矩阵行：关静态烘焙 + 特效减半 + 无扫描线/后处理 + 33.4ms 预算
    public void Low_tier_row_matches_the_degradation_targets()
    {
        RenderTierCapabilities low = RenderTierMatrix.Capabilities(RenderTier.Low);

        Assert.False(low.StaticChunkBake);       // 关静态烘焙改按需直绘
        Assert.Equal(32, low.DirtyChunkCells);   // 粒度维持 G.1 条 8 的 32
        Assert.Equal(0.5d, low.EffectDensity);   // 特效减半
        Assert.False(low.ScanlineOverlay);
        Assert.False(low.PostProcessing);        // 无后处理
        Assert.Equal(33.4d, low.TargetFrameBudgetMs); // 30 fps 集显/Web 稳定目标
    }

    [Fact] // legal — M 档矩阵行=现默认（烘焙 32 + 全特效 + 全开 + 16.7ms）
    public void Medium_tier_row_matches_the_current_default()
    {
        RenderTierCapabilities medium = RenderTierMatrix.Capabilities(RenderTier.Medium);

        Assert.True(medium.StaticChunkBake);
        Assert.Equal(DirtyRegionSet.DefaultChunkCells, medium.DirtyChunkCells);
        Assert.Equal(1.0d, medium.EffectDensity);
        Assert.True(medium.ScanlineOverlay);
        Assert.True(medium.PostProcessing);
        Assert.Equal(16.7d, medium.TargetFrameBudgetMs);
    }

    [Fact] // legal — H 档矩阵行：全开 + 16×16 更细脏粒度
    public void High_tier_row_tightens_the_dirty_granularity()
    {
        RenderTierCapabilities high = RenderTierMatrix.Capabilities(RenderTier.High);

        Assert.True(high.StaticChunkBake);
        Assert.Equal(16, high.DirtyChunkCells); // 更细脏粒度（H 独有差异）
        Assert.Equal(1.0d, high.EffectDensity);
        Assert.True(high.ScanlineOverlay);
        Assert.True(high.PostProcessing);
    }

    [Fact] // legal — 三档能力矩阵逐列成梯：L≤M≤H 的差异方向与任务卡口径一致
    public void Matrix_ladder_is_monotone_and_pairwise_distinct()
    {
        RenderTierCapabilities[] rows =
        [
            RenderTierMatrix.Capabilities(RenderTier.Low),
            RenderTierMatrix.Capabilities(RenderTier.Medium),
            RenderTierMatrix.Capabilities(RenderTier.High),
        ];

        // 三行两两不同（决策差异的根基：同表驱动的三档不可能退化为同一行）
        for (int i = 0; i < rows.Length; i++)
        {
            for (int j = i + 1; j < rows.Length; j++)
            {
                Assert.NotEqual(rows[i], rows[j]);
            }
        }

        Assert.True(rows[0].EffectDensity <= rows[1].EffectDensity);
        Assert.True(rows[1].EffectDensity <= rows[2].EffectDensity);
        Assert.True(rows[0].DirtyChunkCells >= rows[2].DirtyChunkCells); // H 更细
        Assert.False(rows[0].StaticChunkBake);
        Assert.True(rows[1].StaticChunkBake && rows[2].StaticChunkBake);
        Assert.Equal(new[] { "L", "M", "H" },
            RenderTierMatrix.AllTiers.Select(RenderTierMatrix.TierId));
    }

    // ---------------------------------------------------------- selector

    [Theory] // legal — 手动覆盖永远赢（探测事实不改变显式选择）
    [InlineData(RenderTierPreference.Low, false, 0d, RenderTier.Low)]
    [InlineData(RenderTierPreference.Low, true, 59d, RenderTier.Low)]
    [InlineData(RenderTierPreference.Medium, true, 20d, RenderTier.Medium)]
    [InlineData(RenderTierPreference.High, false, 10d, RenderTier.High)]
    public void Manual_preference_overrides_any_probe_facts(
        RenderTierPreference preference, bool isWeb, double fps, RenderTier expected)
    {
        Assert.Equal(expected, RenderTierSelector.Select(preference, new RenderProbeFacts(isWeb, fps)));
    }

    [Theory] // legal — 自动探测：Web→L；帧率 <45→L；≥55→H；其间→M
    [InlineData(true, 0d, RenderTier.Low)]      // Web 优先于帧率（R-E2 单线程弱 GPU）
    [InlineData(true, 60d, RenderTier.Low)]     // Web 上即使帧率高也自动落 L
    [InlineData(false, 0d, RenderTier.Medium)]  // 无数据（headless/探测未完）保持现默认
    [InlineData(false, 44.9d, RenderTier.Low)]
    [InlineData(false, 50d, RenderTier.Medium)]
    [InlineData(false, 55d, RenderTier.High)]   // boundary 值在 legal 表内一并锁死
    [InlineData(false, 59.9d, RenderTier.High)]
    public void Auto_preference_resolves_from_probe_facts(
        bool isWeb, double fps, RenderTier expected)
    {
        Assert.Equal(expected,
            RenderTierSelector.Select(RenderTierPreference.Auto, new RenderProbeFacts(isWeb, fps)));
    }

    [Fact] // boundary — 阈值线两侧：45 之下刚降档、45 整保持 M（<45 才降）
    public void Fps_thresholds_are_strictly_below_low_and_at_or_above_high()
    {
        Assert.Equal(RenderTier.Medium,
            RenderTierSelector.Select(RenderTierPreference.Auto, new RenderProbeFacts(false, 45d)));
        Assert.Equal(RenderTier.Low,
            RenderTierSelector.Select(RenderTierPreference.Auto, new RenderProbeFacts(false, 44.9999d)));
        Assert.Equal(RenderTier.High,
            RenderTierSelector.Select(RenderTierPreference.Auto, new RenderProbeFacts(false, RenderTierSelector.HighAtOrAboveFps)));
        Assert.Equal(RenderTier.Medium,
            RenderTierSelector.Select(RenderTierPreference.Auto, new RenderProbeFacts(false, RenderTierSelector.HighAtOrAboveFps - 0.0001d)));
    }

    [Fact] // illegal — 未知偏好枚举与 null facts 拒绝
    public void Selector_rejects_unknown_preference_and_null_facts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RenderTierSelector.Select((RenderTierPreference)99, new RenderProbeFacts(false, 60d)));
        Assert.Throws<ArgumentNullException>(
            () => RenderTierSelector.Select(RenderTierPreference.Auto, null!));
    }

    // -------------------------------------------------------- persistence

    [Fact] // legal — 缺文件：出厂缺省=自动探测
    public void Load_missing_file_yields_the_auto_factory_default()
    {
        RenderTierSettings loaded = RenderTierStore.Load(_store);

        Assert.Equal(RenderTierPreference.Auto, loaded.Preference);
        Assert.Equal(RenderTierPreference.Auto, RenderTierSettings.Default.Preference);
    }

    [Fact] // legal — 往返：低档偏好保存后重载仍是低档（跨运行记忆）
    public void Save_then_load_roundtrips_a_manual_low_preference()
    {
        RenderTierStore.Save(_store, new RenderTierSettings { Preference = RenderTierPreference.Low });

        Assert.Equal(RenderTierPreference.Low, RenderTierStore.Load(_store).Preference);
    }

    [Fact] // legal — 线上格式：紧凑 JSON、枚举走可读字符串（人工可检视）
    public void Save_writes_compact_json_with_a_string_preference()
    {
        RenderTierStore.Save(_store, new RenderTierSettings { Preference = RenderTierPreference.High });

        Assert.True(_store.Exists(RenderTierStore.RelativePath));
        string json = System.Text.Encoding.UTF8.GetString(
            _store.ReadAllBytes(RenderTierStore.RelativePath));
        Assert.Equal("""{"Preference":"High"}""", json);
    }

    [Theory] // boundary — 坏 JSON/空文档/未知偏好词 → 回出厂自动档（永不阻断启动）
    [InlineData("null")]
    [InlineData("{}")]              // missing required member
    [InlineData("""{"Preference":"Ultra"}""")] // unknown token
    [InlineData("""{"Preference":99}""")]      // out-of-range enum number
    public void Load_broken_file_falls_back_to_the_factory_default(string json)
    {
        _store.WriteAllBytes(
            RenderTierStore.RelativePath, System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal(RenderTierPreference.Auto, RenderTierStore.Load(_store).Preference);
    }

    [Fact] // boundary — 坏字节同样回缺省（与坏 JSON 同道）
    public void Load_corrupt_bytes_fall_back_to_the_factory_default()
    {
        _store.WriteAllBytes(RenderTierStore.RelativePath, [0x7B, 0x00, 0xFF]);

        Assert.Equal(RenderTierPreference.Auto, RenderTierStore.Load(_store).Preference);
    }

    [Fact] // illegal — null store / null settings 拒绝
    public void Store_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => RenderTierStore.Load(null!));
        Assert.Throws<ArgumentNullException>(() => RenderTierStore.Save(_store, null!));
        Assert.Throws<ArgumentNullException>(
            () => RenderTierStore.Save(null!, RenderTierSettings.Default));
    }
}

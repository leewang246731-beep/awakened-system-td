using EraCore.Llm.L2;
using Xunit;

namespace EraCore.Tests.Llm.L2;

// [F-078] T-137 下载器决策面（断点续传/校验门/进度投影）——纯逻辑单元级，
// 网络零参与（真下 1.9 GB 是验收红线，引擎级 mock 测试在 L2DownloadEngineTests）。
public class L2DownloadPlanTests
{
    private static L2Artifact Artifact(long size, string? sha = "ab") => new(
        "model", "https://example.invalid/model.gguf", "model.gguf", "models", size, sha);

    // ------------------------------------------------ 决策表全枚举

    [Fact] // 终稿在位+尺寸符+已验 → AlreadyComplete（零 wire 零磁盘写）
    public void Final_file_with_matching_size_and_verified_digest_is_already_complete()
    {
        var directive = L2DownloadPlan.Decide(Artifact(100), finalSize: 100, partSize: -1, finalDigestVerified: true);

        Assert.Equal(L2DownloadAction.AlreadyComplete, directive.Action);
        Assert.Equal(0, directive.ResumeOffset);
    }

    [Fact] // 终稿在位+尺寸符+未验 → VerifyOnly（离线 hash 门，零 wire）
    public void Final_file_unverified_downgrades_to_verify_only()
    {
        var directive = L2DownloadPlan.Decide(Artifact(100), finalSize: 100, partSize: -1, finalDigestVerified: false);

        Assert.Equal(L2DownloadAction.VerifyOnly, directive.Action);
    }

    [Fact] // 无摘要工件（runtime zip）：尺寸符即完整（GitHub Releases 无服务端摘要，尺寸门独扛——REV 登记）
    public void Digestless_artifact_is_complete_on_size_match_alone()
    {
        var directive = L2DownloadPlan.Decide(Artifact(100, sha: null), finalSize: 100, partSize: -1, finalDigestVerified: false);

        Assert.Equal(L2DownloadAction.AlreadyComplete, directive.Action);
    }

    [Theory] // 断点续传核心：.part 任意 (0,expected) 尺寸 → 从其长度续传
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    public void Partial_file_resumes_from_its_own_length(long partSize)
    {
        var directive = L2DownloadPlan.Decide(Artifact(100), finalSize: -1, partSize: partSize, finalDigestVerified: false);

        Assert.Equal(L2DownloadAction.ResumeFromPartial, directive.Action);
        Assert.Equal(partSize, directive.ResumeOffset);
    }

    [Fact] // .part 尺寸==期望 → 先验后晋（VerifyOnly over part）
    public void Complete_part_is_verified_before_promotion()
    {
        var directive = L2DownloadPlan.Decide(Artifact(100), finalSize: -1, partSize: 100, finalDigestVerified: false);

        Assert.Equal(L2DownloadAction.VerifyOnly, directive.Action);
    }

    [Theory] // 脏盘全清：无文件/.part 超尺寸/终稿超尺寸（外来文件）→ FreshStart
    [InlineData(-1, -1)]        // nothing on disk
    [InlineData(-1, 101)]       // oversized part
    [InlineData(101, -1)]       // oversized foreign final
    [InlineData(99, 0)]         // zero-length part (no usable resume)
    public void Unusable_disk_states_restart_clean(long finalSize, long partSize)
    {
        var directive = L2DownloadPlan.Decide(Artifact(100), finalSize: finalSize, partSize: partSize, finalDigestVerified: false);

        Assert.Equal(L2DownloadAction.FreshStart, directive.Action);
    }

    [Fact] // 终稿尺寸不符但更小：不算终稿（续传 .part 优先）
    public void Undersized_final_with_usable_part_still_resumes_part()
    {
        var directive = L2DownloadPlan.Decide(Artifact(100), finalSize: 40, partSize: 60, finalDigestVerified: false);

        Assert.Equal(L2DownloadAction.ResumeFromPartial, directive.Action);
        Assert.Equal(60, directive.ResumeOffset);
    }

    // ------------------------------------------------ 进度投影

    [Fact] // 进度投影：字节数→整百分比（钳制域）
    public void Progress_projection_clamps_and_computes_whole_percents()
    {
        L2DownloadProgress progress = L2DownloadPlan.Progress(Artifact(1000), downloadedBytes: 250);

        Assert.Equal(25, progress.Percent);
        Assert.Equal("25%", progress.PercentText);
        Assert.Equal(250, progress.DownloadedBytes);
        Assert.Equal(1000, progress.TotalBytes);

        Assert.Equal(0, L2DownloadPlan.PercentOf(-5, 1000));   // negative clamps to 0
        Assert.Equal(100, L2DownloadPlan.PercentOf(5000, 1000)); // over-run clamps to 100
        Assert.Equal(0, L2DownloadPlan.PercentOf(0, 0));         // degenerate total
    }

    // ------------------------------------------------ 事实清单（冻结锚）

}

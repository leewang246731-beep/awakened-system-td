using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

// [F-147] [C-31] T-109 披露文案存在性（LlmDisclosureCatalog）：首启声明四要素
// （数据去向=你配置的 base_url/不收集遥测/随时可关/中英）+ AI 生成素材标注
// （含 manifest 链接）逐要素断言，且 manifest 台账真实随仓库在盘（F-153
// 声明/对比材料随仓库——game/assets/expand-v1 聚合台账为锚）。
public class LlmDisclosureCatalogTests
{
    // ---------------------------------------------------------------- legal

    [Fact] // legal — 首启声明四要素齐备（中英）：去向/遥测/可关/标题
    public void Dialog_copy_carries_all_four_notice_elements_in_both_languages()
    {
        string flowZh = LlmDisclosureCatalog.DialogFlowZh("https://open.bigmodel.cn/api/paas/v4");
        string flowEn = LlmDisclosureCatalog.DialogFlowEn("https://open.bigmodel.cn/api/paas/v4");

        // ① 数据去向 = 玩家配置的 base_url（唯一去向，中英同插值）
        Assert.Contains("你配置的服务", flowZh);
        Assert.Contains("https://open.bigmodel.cn/api/paas/v4", flowZh);
        Assert.Contains("the service you configured", flowEn);
        Assert.Contains("https://open.bigmodel.cn/api/paas/v4", flowEn);

        // ② 不收集遥测
        Assert.Contains("不收集遥测", LlmDisclosureCatalog.DialogTelemetryZh);
        Assert.Contains("no telemetry", LlmDisclosureCatalog.DialogTelemetryEn);

        // ③ 随时可关（关=零网络）
        Assert.Contains("随时", LlmDisclosureCatalog.DialogOptionalZh);
        Assert.Contains("关闭", LlmDisclosureCatalog.DialogOptionalZh);
        Assert.Contains("switched off anytime", LlmDisclosureCatalog.DialogOptionalEn);

        // ④ 标题/按钮中英非空
        Assert.NotEmpty(LlmDisclosureCatalog.DialogTitleZh);
        Assert.NotEmpty(LlmDisclosureCatalog.DialogTitleEn);
        Assert.Contains("不再提示", LlmDisclosureCatalog.DialogConfirmZh);
        Assert.Contains("下次", LlmDisclosureCatalog.DialogDeclineZh); // 暂不=不落盘下次仍弹
    }

    [Fact] // legal — AI 生成素材标注：措辞（部分+AI 生成）+ manifest 链接路径
    public void Ai_asset_label_states_partial_ai_generation_with_the_manifest_path()
    {
        Assert.Contains("部分", LlmDisclosureCatalog.AiAssetLabelZh);
        Assert.Contains("AI 生成", LlmDisclosureCatalog.AiAssetLabelZh);
        Assert.Contains(LlmDisclosureCatalog.ManifestDirPath, LlmDisclosureCatalog.AiAssetLabelZh);
        Assert.Contains("AI-generated", LlmDisclosureCatalog.AiAssetLabelEn);
        Assert.Contains(LlmDisclosureCatalog.ManifestDirPath, LlmDisclosureCatalog.AiAssetLabelEn);
    }

    [Fact] // legal — 声明/对比材料随仓库：manifest 目录与聚合台账真实在盘
    // （仓库 walk-up 探测，同 UiContentFilesTests 口径；缺失即红=披露失锚）
    public void Generation_manifests_really_ship_in_the_repository()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string probe = Path.Combine(
                dir.FullName, "game", "assets", "expand-v1", "aggregation-manifest.json");
            if (File.Exists(probe))
            {
                // 聚合台账在盘即止（逐批 manifest 由装配脚本+资产校验门禁另行保证）
                Assert.True(new FileInfo(probe).Length > 0);
                return;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException(
            "未找到 game/assets/expand-v1/aggregation-manifest.json（披露链接失锚）");
    }

    [Fact] // legal — 披露文案永不含密钥素材字样（RL5：Key 不进任何玩家文案）
    public void Disclosure_copy_never_suggests_key_material_leaves_the_machine()
    {
        string all = string.Join("\n",
            LlmDisclosureCatalog.DialogFlowZh("https://example.test/v1"),
            LlmDisclosureCatalog.DialogFlowEn("https://example.test/v1"),
            LlmDisclosureCatalog.DialogTelemetryZh,
            LlmDisclosureCatalog.DialogTelemetryEn,
            LlmDisclosureCatalog.DialogOptionalZh,
            LlmDisclosureCatalog.DialogOptionalEn,
            LlmDisclosureCatalog.AiAssetLabelZh,
            LlmDisclosureCatalog.AiAssetLabelEn);

        Assert.Contains("仅存本机", LlmDisclosureCatalog.DialogTelemetryZh); // 正向事实在册
        Assert.DoesNotContain("上传 Key", all);
        Assert.DoesNotContain("upload your key", all);
    }

    // ------------------------------------------------------------ boundary

    [Theory] // boundary — base_url 空白三态（空/空白/null）→ 同一空位插入
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public void Blank_base_url_uses_the_shared_blank_insert(string? baseUrl)
    {
        Assert.Contains(
            LlmDisclosureCatalog.DialogFlowBlankZh,
            LlmDisclosureCatalog.DialogFlowZh(baseUrl!));
        Assert.Contains(
            LlmDisclosureCatalog.DialogFlowBlankZh,
            LlmDisclosureCatalog.DialogFlowEn(baseUrl!));
    }

    [Fact] // boundary — base_url 带空白前后缀 → 修剪后插入（不双份空白）
    public void Base_url_is_trimmed_into_the_flow_line()
    {
        string zh = LlmDisclosureCatalog.DialogFlowZh("  https://relay.example/v1  ");

        Assert.Contains("（https://relay.example/v1）", zh);
        Assert.DoesNotContain("  https", zh);
    }
}

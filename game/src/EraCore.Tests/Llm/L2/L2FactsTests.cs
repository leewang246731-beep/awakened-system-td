using EraCore.Llm.L2;
using Xunit;

namespace EraCore.Tests.Llm.L2;

// [F-078] [F-163] T-137 事实清单冻结（L2Facts）：模型/运行工具体积、URL、
// sha256、预算与玩家可见文案单源——下载器与 UI 全部从这里取数（禁第二处
// 措辞/常量）。纯事实锚定测试（47-t129 §1.2 实测 + HF LFS oid 对账）。
public class L2FactsTests
{

    [Fact] // [F-163] 模型事实=47-t129 §1.2 实测（1.93 GB 不进主包的裁决依据）
    public void Model_facts_stay_frozen_to_the_spike_measurements()
    {
        Assert.Equal(1929903264L, L2Facts.Model.SizeBytes);
        Assert.NotNull(L2Facts.Model.Sha256Hex);
        Assert.Equal(64, L2Facts.Model.Sha256Hex!.Length); // lowercase hex sha256
        Assert.StartsWith("https://huggingface.co/", L2Facts.Model.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal("models", L2Facts.Model.RelativeDir);
    }

    [Fact] // 运行时事实=b11714 win-cpu-x64（GitHub Content-Length 实测对账）
    public void Runtime_facts_stay_frozen()
    {
        Assert.Equal(19662606L, L2Facts.Runtime.SizeBytes);
        Assert.Null(L2Facts.Runtime.Sha256Hex); // no server-side digest — size gate only
        Assert.StartsWith("https://github.com/", L2Facts.Runtime.DownloadUrl, StringComparison.Ordinal);
    }

    [Fact] // 波间预算=25s（spike 热整答上界 22.7s+余量）；启动预算 60s
    public void Budgets_stay_frozen()
    {
        Assert.Equal(TimeSpan.FromSeconds(25), L2Facts.WaveGapFillBudget);
        Assert.Equal(TimeSpan.FromSeconds(60), L2Facts.RuntimeStartBudget);
        Assert.Equal(25000, WaveGapFillQueue.BudgetMs);
    }

    [Fact] // 玩家可见体积文案单源：从事实推导（禁第二处措辞）
    public void Player_facing_size_copy_derives_from_the_manifest()
    {
        Assert.Equal("1.8 GB", L2Facts.ModelSizeZh);
        Assert.Equal(2, L2Facts.All.Count);
        Assert.Same(L2Facts.Model, L2Facts.All[0]);
    }
}

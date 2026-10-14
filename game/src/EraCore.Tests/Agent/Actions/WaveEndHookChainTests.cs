using EraCore.Agent.Actions;
using EraCore.Tests.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-075] T-070 WaveEnd 钩子链（守成完成度把关 ≤3 轮，F.4）：逐钩顺序检查、
// 首个 Block 拦下"守住"汇报并回喂原因、每波拦截计数 ≤3 轮后强制放行
// （RoundCapped 标记透出，汇报必出、循环有界）、换波自动清零计数。

/// <summary>[F-075] WaveEnd completeness chain: held-report gating with the ≤3-round cap.</summary>
public class WaveEndHookChainTests
{
    private sealed record StubHook(string Id, Func<WaveEndContext, WaveEndHookVerdict> Behavior) : IWaveEndHook
    {
        public WaveEndHookVerdict Review(WaveEndContext context) => Behavior(context);
    }

    private static WaveEndContext HeldReport(int wave) =>
        new(wave, ClaimsHeld: true, ReportJson: ActionJsonSamples.ReportToPlayer(), WorldView: null);

    [Fact] // legal — 全过：所有钩子 Pass → 汇报放行，未消耗轮次
    public void Passing_hooks_let_the_report_ship()
    {
        var chain = new WaveEndHookChain(new IWaveEndHook[] { new StubHook("a", _ => WaveEndHookVerdict.Pass()) });
        WaveEndReviewResult result = chain.Review(HeldReport(wave: 3));
        Assert.True(result.Ship);
        Assert.False(result.RoundCapped);
        Assert.Null(result.BlockedBy);
        Assert.Equal(0, result.Round);
    }

    [Fact] // illegal — 漏怪未补：Block 拦下汇报并回喂原因（轮次 1 起）
    public void Blocking_hooks_hold_the_report_and_feed_back()
    {
        var chain = new WaveEndHookChain(new IWaveEndHook[]
        {
            new StubHook("leak-check", _ => WaveEndHookVerdict.Block("2 条漏怪路径未补")),
        });

        WaveEndReviewResult result = chain.Review(HeldReport(wave: 5));
        Assert.False(result.Ship);
        Assert.Equal("leak-check", result.BlockedBy);
        Assert.Equal("2 条漏怪路径未补", result.Feedback);
        Assert.Equal(1, result.Round);
    }

    [Fact] // legal — 补防后复评通过：同一波第 2 轮 Pass 放行（轮次簿记保留）
    public void Patched_defense_ships_on_the_next_round()
    {
        int reviews = 0;
        var chain = new WaveEndHookChain(new IWaveEndHook[]
        {
            new StubHook("leak-check", _ => ++reviews == 1
                ? WaveEndHookVerdict.Block("先补防线")
                : WaveEndHookVerdict.Pass()),
        });

        Assert.False(chain.Review(HeldReport(wave: 5)).Ship);
        WaveEndReviewResult second = chain.Review(HeldReport(wave: 5));
        Assert.True(second.Ship);
        Assert.False(second.RoundCapped);
        Assert.Equal(1, second.Round);
    }

    [Fact] // boundary — ≤3 轮上限：同波第 4 次评审强制放行（RoundCapped=true，循环有界）
    public void Third_consecutive_block_opens_the_gate_on_the_fourth_review()
    {
        var chain = new WaveEndHookChain(new IWaveEndHook[]
        {
            new StubHook("leak-check", _ => WaveEndHookVerdict.Block("仍未补")),
        });

        Assert.False(chain.Review(HeldReport(wave: 7)).Ship); // 轮 1
        Assert.False(chain.Review(HeldReport(wave: 7)).Ship); // 轮 2
        Assert.False(chain.Review(HeldReport(wave: 7)).Ship); // 轮 3（=MaxReviewRounds）
        WaveEndReviewResult fourth = chain.Review(HeldReport(wave: 7));
        Assert.True(fourth.Ship);
        Assert.True(fourth.RoundCapped);
        Assert.Equal(3, fourth.Round);
    }

    [Fact] // legal — 换波清零：新一轮的拦截计数从头起（每波独立 ≤3 轮）
    public void Wave_change_resets_the_round_counter()
    {
        var chain = new WaveEndHookChain(new IWaveEndHook[]
        {
            new StubHook("leak-check", _ => WaveEndHookVerdict.Block("拦")),
        });

        Assert.False(chain.Review(HeldReport(wave: 1)).Ship);
        Assert.False(chain.Review(HeldReport(wave: 1)).Ship);
        Assert.False(chain.Review(HeldReport(wave: 1)).Ship);
        Assert.True(chain.Review(HeldReport(wave: 1)).Ship); // 第 4 次封顶放行

        WaveEndReviewResult nextWave = chain.Review(HeldReport(wave: 2));
        Assert.False(nextWave.Ship); // 新波重新计数：又被拦（轮 1）
        Assert.Equal(1, nextWave.Round);
    }

    [Fact] // boundary — 常量=3（F.4 守成检查 ≤3 轮冻结口径）
    public void Max_review_rounds_is_three()
    {
        Assert.Equal(3, WaveEndHookChain.MaxReviewRounds);
    }

    [Fact] // illegal — 空/单参防御：null 上下文抛 ArgumentNull（守卫测试同律）
    public void Null_context_is_refused()
    {
        var chain = new WaveEndHookChain(Array.Empty<IWaveEndHook>());
        Assert.Throws<ArgumentNullException>(() => chain.Review(null!));
    }
}

using EraCore.Progression;
using Xunit;

namespace EraCore.Tests.Progression;

// [F-054][F-002] CampaignHash 确定性种子派生（T-073）：固定 FNV-1a 向量 +
// 同串同值 + 空串合法。纯辅助类测试。[C-19]
public class CampaignHashTests
{
    // FNV-1a 64 参照向量（标准空串偏移与 "a" 台阶值，先于本任务冻结的实现
    // 无关性——若实现漂移，此向量红）。
    private const long EmptyHash = unchecked((long)14695981039346656037UL);

    private const long AHash = unchecked((long)12638187200555641996UL); // FNV-1a("a")

    [Fact] // legal — 标准向量：空串=偏移基、"a"=偏移异或 97 后乘一次素数
    public void Known_fnv_vectors_hold()
    {
        Assert.Equal(EmptyHash, CampaignHash.Fnv64(string.Empty));
        Assert.Equal(AHash, CampaignHash.Fnv64("a"));
    }

    [Fact] // legal — 同串同值：种子派生的确定性根基（SP-8c seed=关卡 id）
    public void Same_text_hashes_identically()
    {
        Assert.Equal(CampaignHash.Fnv64("campaign:level-e1-01"), CampaignHash.Fnv64("campaign:level-e1-01"));
        Assert.NotEqual(CampaignHash.Fnv64("campaign:level-e1-01"), CampaignHash.Fnv64("campaign:level-e1-02"));
    }

    [Fact] // illegal — null 直接拒绝（ArgumentNullException）
    public void Null_text_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => CampaignHash.Fnv64(null!));
    }
}

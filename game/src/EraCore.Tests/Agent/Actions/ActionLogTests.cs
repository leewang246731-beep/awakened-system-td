using EraCore.Agent.Actions;
using Xunit;

namespace EraCore.Tests.Agent.Actions;

// [F-062] C-03 注册表日志与密钥预扫（拒绝记日志、永不携带密钥模式——RL5）
public class ActionLogTests
{
    [Theory] // legal：普通载荷原样保留（截断到摘要上限）
    [InlineData("{\"action\":\"LookAround\"}")]
    [InlineData("{\"action\":\"SpawnScout\",\"lane\":\"lane-north\",\"duration\":60.0}")]
    public void Digest_keeps_benign_payloads(string payload)
    {
        string digest = ActionLog.Digest(payload);
        Assert.Contains("action", digest);
        Assert.True(digest.Length <= ActionLog.PayloadDigestMaxLength, $"摘要超限：{digest.Length}");
    }

    [Fact]
    public void Digest_truncates_long_payloads_to_the_cap()
    {
        // boundary：超长载荷（非令牌形态，不触发脱敏）截到 160 字符（换行折叠为空格）
        string payload = "{\"action\":\"BuildPlan\",\"tower_id\":\"" + new string('塔', 500) + "\"}";
        Assert.Equal(ActionLog.PayloadDigestMaxLength, ActionLog.Digest(payload).Length);
    }

    [Fact]
    public void Scrub_redacts_service_key_tokens()
    {
        // illegal 样例（脱敏对象）：sk- 前缀长令牌不得进入日志
        string scrubbed = ActionLog.Scrub("token was sk-demo1234567 end");
        Assert.DoesNotContain("sk-demo1234567", scrubbed);
        Assert.Contains(ActionLog.RedactedToken, scrubbed);
    }

    [Theory] // illegal 样例（脱敏对象）：凭据字段名 + 值形态
    [InlineData("api_key=demo-secret-987654321")]
    [InlineData("\"secret\": \"demo-secret-987654321\"")]
    [InlineData("Authorization: demo-bearer-987654321")]
    [InlineData("password=demo-password-987654321")]
    public void Scrub_redacts_credential_field_assignments(string snippet)
    {
        string scrubbed = ActionLog.Scrub("payload=" + snippet);
        Assert.DoesNotContain("demo-secret-987654321", scrubbed);
        Assert.DoesNotContain("demo-bearer-987654321", scrubbed);
        Assert.DoesNotContain("demo-password-987654321", scrubbed);
        Assert.Contains(ActionLog.RedactedToken, scrubbed);
    }

    [Fact]
    public void Scrub_redacts_long_opaque_blobs()
    {
        // illegal 样例（脱敏对象）：无字段名的 32+ 位不透明串
        string blob = new string('a', 40);
        string scrubbed = ActionLog.Scrub("note=" + blob);
        Assert.DoesNotContain(blob, scrubbed);
        Assert.Contains(ActionLog.RedactedToken, scrubbed);
    }

    [Fact]
    public void DenyLine_carries_structured_fields_and_scrubbed_digest()
    {
        // legal：拒绝行结构化字段齐备（verdict/reason/action/payload）
        string line = ActionLog.DenyLine(
            "SellEverything",
            "action-out-of-list",
            "{\"action\":\"SellEverything\",\"api_key\":\"demo-secret-987654321\"}");
        Assert.Contains("verdict=denied", line);
        Assert.Contains("reason=action-out-of-list", line);
        Assert.Contains("action=SellEverything", line);
        Assert.Contains("payload=", line);
        Assert.DoesNotContain("demo-secret-987654321", line);
    }

    [Fact]
    public void DenyLine_tolerates_null_name_and_payload()
    {
        // boundary：空名/空载荷不抛异常（denied 行仍成形）
        string line = ActionLog.DenyLine(null, "payload-empty", null);
        Assert.Contains("verdict=denied", line);
        Assert.Contains("action=-", line);
    }

    [Fact]
    public void EraLockedLine_and_refusal_lines_carry_their_verdicts()
    {
        // legal：时代门/未实现档行的结构化 verdict
        Assert.Contains("verdict=era-locked", ActionLog.EraLockedLine("SpawnScout", EraOrdinals.E2, EraOrdinals.E1));
        Assert.Contains("verdict=refused", ActionLog.NotImplementedLine("SpawnScout", "not-implemented"));
        Assert.Contains("verdict=ok", ActionLog.OkLine("LookAround", EraOrdinals.E1));
    }

    [Fact]
    public void Scrub_throws_on_null_input()
    {
        // illegal：null 输入显式拒绝（防静默吞错）
        Assert.Throws<ArgumentNullException>(() => ActionLog.Scrub(null!));
        Assert.Throws<ArgumentNullException>(() => ActionLog.Digest(null!));
    }
}

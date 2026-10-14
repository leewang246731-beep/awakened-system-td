using System.Reflection;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 错误文案表完备性（数据化中英，方案 §11.7）：每个 ErrorKey 必须有中英
/// 双语文案，且文案永不携带密钥形态。
/// </summary>
// [F-089][F-162] C-16 T-072：错误码分类文案表
public class LlmErrorCatalogTests
{
    private static IReadOnlyList<string> AllKeys() => typeof(LlmErrorKeys)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToList();

    [Fact]
    public void Every_error_key_has_bilingual_copy()
    {
        // legal: 全键覆盖（新键缺文案即红）
        var keys = AllKeys();
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(
                LlmErrorCatalog.TryGetCopy(key, out var zh, out var en),
                $"缺少文案行：{key}");
            Assert.False(string.IsNullOrWhiteSpace(zh), $"{key} 中文文案为空");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} 英文文案为空");
        }
    }

    [Fact]
    public void Copy_rows_cover_exactly_the_declared_key_set()
    {
        // boundary: 文案表与键集一一对应（多余行=键集漂移信号）
        var declared = AllKeys().ToHashSet();
        var catalogued = LlmErrorCatalog.AllRows.Select(r => r.ErrorKey).ToHashSet();
        Assert.True(declared.SetEquals(catalogued), "LlmErrorKeys 与文案表键集不一致");
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("en")]
    public void No_copy_row_contains_key_shaped_material(string channel)
    {
        // illegal: 文案永不携带密钥形态（RL5）
        foreach (var row in LlmErrorCatalog.AllRows)
        {
            var text = channel == "zh" ? row.Zh : row.En;
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(text, "sk-[A-Za-z0-9]{8,}"),
                $"文案疑似携带密钥：{row.ErrorKey}");
        }
    }

    [Fact]
    public void Design_wording_is_preserved_for_the_pinned_error_classes()
    {
        // legal: §11.7 钉死话术——1315/1301/密钥无效/代理检查
        Assert.Contains("Coding Plan", LlmErrorCatalog.AllRows
            .First(r => r.ErrorKey == LlmErrorKeys.CodingPlan).Zh);
        Assert.Contains("1315", LlmErrorCatalog.AllRows
            .First(r => r.ErrorKey == LlmErrorKeys.CodingPlan).Zh);
        Assert.Contains("安全策略", LlmErrorCatalog.AllRows
            .First(r => r.ErrorKey == LlmErrorKeys.ContentBlocked).Zh);
        Assert.Contains("密钥无效", LlmErrorCatalog.AllRows
            .First(r => r.ErrorKey == LlmErrorKeys.Auth).Zh);
        Assert.Contains("代理", LlmErrorCatalog.AllRows
            .First(r => r.ErrorKey == LlmErrorKeys.Network).Zh);
    }

    [Fact]
    public void Unknown_key_lookup_fails_cleanly()
    {
        // illegal: 未知键返回 false 且文案为空（不抛异常）
        Assert.False(LlmErrorCatalog.TryGetCopy("no-such-key", out var zh, out var en));
        Assert.Equal(string.Empty, zh);
        Assert.Equal(string.Empty, en);
    }
}

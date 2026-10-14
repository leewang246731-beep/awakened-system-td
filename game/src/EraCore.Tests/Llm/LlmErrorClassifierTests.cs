using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 错误码分类表驱动测试（方案 §11.7）：401/403→密钥无效、429+1315→Coding
/// Plan、1301→内容拦截、Unsupported model→模型 ID 小写、5xx/3xx/网络兜底。
/// </summary>
// [F-089] C-16 T-072：错误码分类首版（文案表见 LlmErrorCatalogTests）
public class LlmErrorClassifierTests
{
    [Theory]
    [InlineData(401, null, LlmErrorKeys.Auth)]
    [InlineData(403, "{}", LlmErrorKeys.Auth)]
    [InlineData(429, "{\"error\":{\"message\":\"rate limited\"}}", LlmErrorKeys.RateLimit)]
    [InlineData(429, LlmFixtures.ZhipuCodingPlanBody, LlmErrorKeys.CodingPlan)]
    [InlineData(400, LlmFixtures.ZhipuCodingPlanBody, LlmErrorKeys.CodingPlan)]
    [InlineData(200, "{\"error\":{\"code\":\"1301\"}}", LlmErrorKeys.ContentBlocked)]
    [InlineData(400, LlmFixtures.ZhipuContentBlockedBody, LlmErrorKeys.ContentBlocked)]
    [InlineData(400, LlmFixtures.MimoUnsupportedModelBody, LlmErrorKeys.InvalidModel)]
    [InlineData(404, "{\"error\":{\"message\":\"model not found: X\"}}", LlmErrorKeys.InvalidModel)]
    [InlineData(400, "{\"error\":{\"message\":\"bad args\"}}", LlmErrorKeys.BadRequest)]
    [InlineData(500, null, LlmErrorKeys.ServerError)]
    [InlineData(503, "upstream down", LlmErrorKeys.ServerError)]
    [InlineData(302, "", LlmErrorKeys.RedirectBlocked)]
    [InlineData(200, "not json at all", LlmErrorKeys.BadResponse)]
    public void Status_and_provider_codes_classify_to_designated_keys(
        int status, string? body, string expected)
    {
        // legal/boundary/illegal 全表：供给商码优先于状态码，无法分类兜底 bad-response
        Assert.Equal(expected, LlmErrorClassifier.ClassifyHttp(status, body));
    }

    [Theory]
    [InlineData(LlmFixtures.ZhipuCodingPlanBody, "1315")]
    [InlineData("{\"error\":{\"code\":1315}}", "1315")]
    [InlineData("{\"code\":\"1301\"}", "1301")]
    [InlineData("garbage {\"code\":\"1315\"} tail", "1315")]
    [InlineData("{\"error\":{\"message\":\"no code here\"}}", null)]
    [InlineData("", null)]
    public void Provider_code_extraction_prefers_error_code_then_falls_back(
        string? body, string? expected)
    {
        // legal: error.code 字符串/数字 + 裸 code + 非 JSON 子串；boundary: 无码/空
        Assert.Equal(expected, LlmErrorClassifier.ExtractProviderErrorCode(body));
    }

    [Fact]
    public void Numeric_error_code_inside_nested_json_object_is_extracted()
    {
        // boundary: 数字型 code（部分中转站返回 number）
        Assert.Equal(
            "1315",
            LlmErrorClassifier.ExtractProviderErrorCode("{\"error\":{\"code\":1315,\"msg\":\"x\"}}"));
    }

    [Fact]
    public void Null_body_with_401_still_classifies_without_throwing()
    {
        // illegal: 空体 401（网关超时截断场景）
        Assert.Equal(LlmErrorKeys.Auth, LlmErrorClassifier.ClassifyHttp(401, null));
    }
}

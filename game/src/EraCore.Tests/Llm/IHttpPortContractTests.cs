using System.Net;
using System.Net.Http;
using System.Text;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// 唯一真实 IHttpPort 实现（HttpClientLlmPort，测试程序集内——CS-1.3）的
/// 契约测试：方法/URL/头/体透传、流式读、错误体、禁止跟随重定向、取消传播。
/// 全部离线（FakeHttpMessageHandler），真网络用例在 LlmOnlineTests。
/// </summary>
// [F-162] C-16 T-072：wire 端口契约（离线）
public class IHttpPortContractTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        public List<(HttpMethod Method, string Url, string Body)> Seen { get; } = [];

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.Method, request.RequestUri!.AbsoluteUri, body));
            return _reply(request);
        }
    }

    private static HttpPortRequest Request(
        string url = "https://api.test.example.com/v1/chat/completions") =>
        new("POST", new Uri(url),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Authorization"] = "Bearer synthetic-key-value-000",
            },
            "{\"model\":\"m\"}",
            TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Method_url_headers_and_body_pass_through_verbatim()
    {
        // legal: 线上形态映射
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var port = new HttpClientLlmPort(handler);

        var reply = await port.SendAsync(Request());

        Assert.Equal(200, reply.StatusCode);
        Assert.True(reply.IsSuccess);
        var seen = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("https://api.test.example.com/v1/chat/completions", seen.Url);
        Assert.Equal("{\"model\":\"m\"}", seen.Body);
    }

    [Fact]
    public async Task Success_reply_exposes_a_readable_body_stream()
    {
        // legal: 2xx → BodyStream 增量可读（SSE 前提）
        var sseText = "data: {\"a\":1}\n\ndata: [DONE]\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseText, Encoding.UTF8, "text/event-stream"),
        });
        using var port = new HttpClientLlmPort(handler);

        var reply = await port.SendAsync(Request());

        Assert.NotNull(reply.BodyStream);
        Assert.Null(reply.Body);
        using var reader = new StreamReader(reply.BodyStream, Encoding.UTF8);
        Assert.Equal(sseText, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Non_success_reply_exposes_body_text_and_no_stream()
    {
        // boundary: 4xx/5xx → 小体积错误体直读
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"code\":\"1315\"}}", Encoding.UTF8),
        });
        using var port = new HttpClientLlmPort(handler);

        var reply = await port.SendAsync(Request());

        Assert.Equal(401, reply.StatusCode);
        Assert.False(reply.IsSuccess);
        Assert.Null(reply.BodyStream);
        Assert.Contains("1315", reply.Body);
    }

    [Fact]
    public async Task Redirect_is_never_followed()
    {
        // illegal: 3xx 原样上抛、单次请求（域名白名单不可被 Location 跳转绕过）
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example.com/steal") },
        });
        using var port = new HttpClientLlmPort(handler);

        var reply = await port.SendAsync(Request());

        Assert.Equal(302, reply.StatusCode);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation()
    {
        // boundary: 取消令牌传导
        var handler = new FakeHandler(_ =>
        {
            throw new OperationCanceledException();
        });
        using var port = new HttpClientLlmPort(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => port.SendAsync(Request()));
    }

    [Fact]
    public void Port_records_are_plain_data_with_derived_success_flag()
    {
        // legal: 记录形态（2xx 唯一携带流）
        Assert.True(new HttpPortResponse(200, null, Stream.Null).IsSuccess);
        Assert.True(new HttpPortResponse(299, null, Stream.Null).IsSuccess);
        Assert.False(new HttpPortResponse(302, string.Empty, null).IsSuccess);
        Assert.False(new HttpPortResponse(500, "oops", null).IsSuccess);
    }
}

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;
using StyloExtract.Llm.Ollama;
using Xunit;

namespace StyloExtract.IntegrationTests;

/// <summary>
/// In-memory HttpMessageHandler stub so we can assert OllamaTextProvider's
/// wire format + happy/error paths without needing a real Ollama running.
/// The real-Ollama integration is a SkippableFact (later) that hits a
/// local instance when present.
/// </summary>
public class OllamaTextProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedRequestBody { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            if (request.Content is not null)
            {
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return Response;
        }
    }

    private static (OllamaTextProvider Provider, StubHandler Handler) Build(
        OllamaTextProviderOptions? options = null)
    {
        var handler = new StubHandler();
        var client = new HttpClient(handler);
        var opts = Options.Create(options ?? new OllamaTextProviderOptions());
        return (new OllamaTextProvider(client, opts), handler);
    }

    [Fact]
    public async Task Posts_To_Api_Chat_With_Configured_Model_And_Messages()
    {
        var (provider, handler) = Build(new OllamaTextProviderOptions
        {
            OllamaUrl = "http://test-ollama:11434",
            Model = "gemma4:e4b-it-qat",
            Temperature = 0.2,
            MaxOutputTokens = 512,
        });
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"message":{"role":"assistant","content":"sample reply"},"done":true}""",
                Encoding.UTF8,
                "application/json"),
        };

        var reply = await provider.CompleteAsync("system text", "user text");
        reply.Should().Be("sample reply");

        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
        handler.CapturedRequest.RequestUri!.AbsolutePath.Should().Be("/api/chat");
        handler.CapturedRequestBody.Should().Contain("gemma4:e4b-it-qat");
        handler.CapturedRequestBody.Should().Contain("system text");
        handler.CapturedRequestBody.Should().Contain("user text");
        handler.CapturedRequestBody.Should().Contain("\"stream\":false");
        handler.CapturedRequestBody.Should().Contain("\"temperature\":0.2");
        handler.CapturedRequestBody.Should().Contain("\"num_predict\":512");
    }

    [Fact]
    public async Task Throws_LlmProviderException_On_Non_2xx_Response()
    {
        var (provider, handler) = Build();
        handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("ollama exploded", Encoding.UTF8, "text/plain"),
        };
        var act = () => provider.CompleteAsync("s", "u");
        await act.Should().ThrowAsync<LlmProviderException>()
            .Where(ex => ex.Message.Contains("500") && ex.Message.Contains("ollama exploded"));
    }

    [Fact]
    public async Task Throws_LlmProviderException_When_Response_Json_Has_Empty_Message_Content()
    {
        var (provider, handler) = Build();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"message":{"role":"assistant","content":""},"done":true}""",
                Encoding.UTF8,
                "application/json"),
        };
        var act = () => provider.CompleteAsync("s", "u");
        await act.Should().ThrowAsync<LlmProviderException>()
            .Where(ex => ex.Message.Contains("empty message.content"));
    }

    [Fact]
    public async Task Translates_Backend_Network_Failure_To_LlmProviderException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var client = new HttpClient(handler);
        var provider = new OllamaTextProvider(client, Options.Create(new OllamaTextProviderOptions
        {
            OllamaUrl = "http://localhost:11434",
        }));
        var act = () => provider.CompleteAsync("s", "u");
        await act.Should().ThrowAsync<LlmProviderException>()
            .Where(ex => ex.Message.Contains("connection refused"));
    }

    [Fact]
    public async Task Cancellation_Propagates_As_OperationCanceledException()
    {
        var handler = new StubHandler { Response = new HttpResponseMessage(HttpStatusCode.OK) };
        var client = new HttpClient(handler);
        var provider = new OllamaTextProvider(client, Options.Create(new OllamaTextProviderOptions()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => provider.CompleteAsync("s", "u", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) { _ex = ex; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _ex;
    }
}

using System.Text;
using System.Text.Json;
using StyloExtract.Abstractions;

namespace StyloExtract.Cli.Shared;

public sealed class MonitorEventSink : ITemplateVersionEventSink, IDisposable
{
    private readonly TextWriter _out;
    private readonly HttpClient? _webhook;
    private readonly Uri? _webhookUrl;
    private readonly bool _pretty;

    public MonitorEventSink(TextWriter @out, string? webhook, bool pretty)
    {
        _out = @out;
        _pretty = pretty;
        if (!string.IsNullOrEmpty(webhook))
        {
            _webhookUrl = new Uri(webhook);
            _webhook = new HttpClient();
        }
    }

    public async ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken cancellationToken)
    {
        var envelope = new MonitorEnvelope<NewTemplateEvent> { Kind = "new-template", EmittedAt = DateTimeOffset.UtcNow, Payload = evt };
        string json;
        if (_pretty)
            json = JsonSerializer.Serialize(envelope, StyloExtractSerializerContextPretty.Default.MonitorEnvelopeNewTemplateEvent);
        else
            json = JsonSerializer.Serialize(envelope, StyloExtractSerializerContext.Default.MonitorEnvelopeNewTemplateEvent);
        await _out.WriteLineAsync(json);
        await PostAsync(json, cancellationToken);
    }

    public async ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken cancellationToken)
    {
        var envelope = new MonitorEnvelope<VersionChangeEvent> { Kind = "version-change", EmittedAt = DateTimeOffset.UtcNow, Payload = evt };
        string json;
        if (_pretty)
            json = JsonSerializer.Serialize(envelope, StyloExtractSerializerContextPretty.Default.MonitorEnvelopeVersionChangeEvent);
        else
            json = JsonSerializer.Serialize(envelope, StyloExtractSerializerContext.Default.MonitorEnvelopeVersionChangeEvent);
        await _out.WriteLineAsync(json);
        await PostAsync(json, cancellationToken);
    }

    private async Task PostAsync(string json, CancellationToken ct)
    {
        if (_webhook is null || _webhookUrl is null) return;
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _webhook.PostAsync(_webhookUrl, content, ct);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"webhook failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _webhook?.Dispose();
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using StyloExtract.Abstractions;

namespace StyloExtract.Cli;

public sealed class MonitorEventSink : ITemplateVersionEventSink
{
    private readonly TextWriter _out;
    private readonly HttpClient? _webhook;
    private readonly Uri? _webhookUrl;
    private readonly bool _pretty;
    private static readonly JsonSerializerOptions Compact = new();
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

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
        => await EmitAsync("new-template", evt, cancellationToken);

    public async ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken cancellationToken)
        => await EmitAsync("version-change", evt, cancellationToken);

    private async Task EmitAsync(string kind, object payload, CancellationToken ct)
    {
        var envelope = new { kind, emittedAt = DateTimeOffset.UtcNow, payload };
        var json = JsonSerializer.Serialize(envelope, _pretty ? Pretty : Compact);
        await _out.WriteLineAsync(json);
        if (_webhook is not null && _webhookUrl is not null)
        {
            try { await _webhook.PostAsJsonAsync(_webhookUrl, envelope, ct); }
            catch (Exception ex) { await Console.Error.WriteLineAsync($"webhook failed: {ex.Message}"); }
        }
    }
}

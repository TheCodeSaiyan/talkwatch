using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TalkWatch.Web.Services;

/// <summary>
/// Traces and metrics over OpenTelemetry, off unless OTEL_EXPORTER_OTLP_ENDPOINT is set; the other standard OTEL_
/// settings (protocol, headers, service name) apply as usual. Nothing that identifies a caller or opens a door leaves:
/// alert-link tokens are cut from request paths, requests to alert channels (a Telegram bot token is part of the URL)
/// are not traced, and database spans carry statements with placeholders, never values.
/// </summary>
public static class Telemetry
{
    public const string Name = "TalkWatch";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Polls = Meter.CreateCounter<long>("talkwatch.polls", description: "Polls of the console, by result.");
    public static readonly Counter<long> CallsStored = Meter.CreateCounter<long>("talkwatch.calls.stored", description: "Calls added or updated, by source and change.");
    public static readonly Counter<long> AlertDeliveries = Meter.CreateCounter<long>("talkwatch.alert.deliveries", description: "Alert deliveries attempted, by channel kind and result.");
    public static readonly Counter<long> AudioCopies = Meter.CreateCounter<long>("talkwatch.audio.copies", description: "Recordings and voicemail copied, by kind and result.");
    public static readonly Counter<long> RetentionRemoved = Meter.CreateCounter<long>("talkwatch.retention.removed", description: "Calls and audio files removed under the retention policy.");

    /// <summary>Marks a request not to be traced: set on every request to an alert channel.</summary>
    public static readonly HttpRequestOptionsKey<bool> NotTraced = new("talkwatch.not-traced");

    public static void AddTelemetry(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return;
        }

        var version = typeof(Telemetry).Assembly.GetName().Version?.ToString();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "talkwatch", serviceVersion: version))
            .WithTracing(t => t
                .AddSource(Name)
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = http => !http.Request.Path.StartsWithSegments("/healthz");

                    // The route template ('/a/{token}/ack') is safe; the path itself carries the token.
                    o.EnrichWithHttpRequest = (activity, request) =>
                    {
                        if (request.Path.StartsWithSegments("/a"))
                        {
                            activity.SetTag("url.path", "/a/{token}");
                        }
                    };
                })
                .AddHttpClientInstrumentation(o => o.FilterHttpRequestMessage = request =>
                    !(request.Options.TryGetValue(NotTraced, out var skip) && skip))
                .AddNpgsql()
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddMeter(Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());
    }
}

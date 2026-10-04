using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Run on their own: OpenTelemetry listens to sources and meters across the whole process, so another test's polls and
/// requests, running alongside, would be counted here too.
/// </summary>
[CollectionDefinition(nameof(TelemetryTests), DisableParallelization = true)]
public sealed class RunsAlone;

[Collection(nameof(TelemetryTests))]
public sealed partial class TelemetryTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    /// <summary>Takes what a channel is sent, as ntfy or Telegram would, and answers OK.</summary>
    private sealed class Receiver : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed record Harness(WebApplicationFactory<Program> App, List<Activity> Spans, List<Metric> Metrics, Receiver Receiver) : IAsyncDisposable
    {
        public void Flush()
        {
            App.Services.GetRequiredService<TracerProvider>().ForceFlush();
            App.Services.GetRequiredService<MeterProvider>().ForceFlush();
        }

        public async ValueTask DisposeAsync()
        {
            Receiver.Dispose();
            await App.DisposeAsync();
        }
    }

    private Harness Start(bool fakeChannels = true)
    {
        List<Activity> spans = [];
        List<Metric> metrics = [];
        var receiver = new Receiver();
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory),
            // Nothing listens there; the in-memory exporters are what the test reads.
            settings: new Dictionary<string, string> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:9", ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1000" },
            services: s =>
            {
                s.ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(spans));
                s.ConfigureOpenTelemetryMeterProvider(b => b.AddInMemoryExporter(metrics));
                if (fakeChannels)
                {
                    s.AddHttpClient(AlertDispatcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
                }
            });
        return new Harness(app, spans, metrics, receiver);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value)));
        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    [Fact]
    public async Task A_poll_is_traced_with_its_database_work_and_counted()
    {
        await using var h = Start();

        await h.App.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        h.Flush();

        Assert.Contains(h.Spans, s => s.Source.Name == Telemetry.Name && s.DisplayName == "poll");
        // Requests to the console are traced as well in a real install; here the fake console replaces the whole handler,
        // which by-passes .NET's request tracing, so they cannot be seen. The channel test below uses a real server.
        Assert.Contains(h.Spans, s => s.Source.Name == "Npgsql");
        var polls = Assert.Single(h.Metrics, m => m.Name == "talkwatch.polls");
        long ok = 0;
        foreach (ref readonly var point in polls.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag is { Key: "result", Value: "ok" })
                {
                    ok += point.GetSumLong();
                }
            }
        }

        Assert.Equal(1, ok);
        Assert.Contains(h.Metrics, m => m.Name == "talkwatch.calls.stored");
    }

    [Fact]
    public async Task An_alert_links_token_never_reaches_a_span()
    {
        await using var h = Start();
        var token = h.App.Services.GetRequiredService<AlertLinks>().Token(Guid.NewGuid());
        using var browser = TalkWatchApp.Browser(h.App);

        await browser.GetAsync(new Uri($"/a/{token}", UriKind.Relative), Ct);
        await browser.PostAsync(new Uri($"/a/{token}/ack", UriKind.Relative), null, Ct);
        h.Flush();

        var requests = h.Spans.Where(s => s.GetTagItem("url.path") is string path && path.StartsWith("/a", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, s => Assert.Equal("/a/{token}", s.GetTagItem("url.path")));
        Assert.DoesNotContain(h.Spans, s => s.DisplayName.Contains(token, StringComparison.Ordinal)
            || s.TagObjects.Any(t => t.Value?.ToString()?.Contains(token, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task Requests_to_alert_channels_are_sent_but_not_traced()
    {
        // A real server, so requests go through .NET's own handler, which is what the tracing hooks into.
        var hits = new List<string>();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/hook", (Microsoft.AspNetCore.Http.HttpContext c) => { hits.Add(c.Request.Path); return Microsoft.AspNetCore.Http.Results.Ok(); });
        server.MapGet("/control", () => Microsoft.AspNetCore.Http.Results.Ok());
        await server.StartAsync(Ct);
        var address = server.Urls.Single();

        await using var h = Start(fakeChannels: false);
        using var admin = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        await PostAsync(admin, "/admin/alerts", "/admin/alerts/channels",
            ("Name", "Hook"), ("Kind", "Webhook"), ("Target", $"{address}/hook?topic=secret-topic"), ("Secret", ""), ("Owner", ""));
        var channel = await Task.Run(async () =>
        {
            using var scope = h.App.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().AlertChannels.Select(c => c.Id).SingleAsync(Ct);
        });

        await PostAsync(admin, "/admin/alerts", $"/admin/alerts/channels/{channel}/test");
        // A request any other part of the app might make, to the same server: this one is traced.
        using (var client = h.App.Services.GetRequiredService<IHttpClientFactory>().CreateClient())
        {
            await client.GetAsync(new Uri($"{address}/control"), Ct);
        }

        h.Flush();

        var outgoing = h.Spans.Where(s => s.Kind == ActivityKind.Client && s.GetTagItem("url.full") is string url && url.StartsWith(address, StringComparison.Ordinal)).ToList();
        Assert.Equal(["/hook"], hits);
        Assert.Single(outgoing, s => ((string)s.GetTagItem("url.full")!).Contains("/control", StringComparison.Ordinal));
        Assert.DoesNotContain(outgoing, s => ((string)s.GetTagItem("url.full")!).Contains("/hook", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Spans, s => s.TagObjects.Any(t => t.Value?.ToString()?.Contains("secret-topic", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task Without_an_endpoint_nothing_is_set_up()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));

        Assert.Null(app.Services.GetService<TracerProvider>());
    }
}

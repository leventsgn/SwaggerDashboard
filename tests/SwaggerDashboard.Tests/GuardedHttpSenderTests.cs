using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Infrastructure.Http;
using SwaggerDashboard.Infrastructure.Security;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Exercises the outbound pipeline against real servers: what survives a redirect, and what
/// happens when a target stops behaving.
/// </summary>
public class GuardedHttpSenderTests : IAsyncLifetime
{
    private WebApplication _first = default!;
    private WebApplication _second = default!;

    private string _firstAddress = string.Empty;
    private string _secondAddress = string.Empty;

    /// <summary>Headers the second server saw, so a leaked credential is visible.</summary>
    private Dictionary<string, string> _secondHeaders = new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync()
    {
        _second = Build(app =>
        {
            app.MapGet("/echo", (HttpContext context) =>
            {
                _secondHeaders = context.Request.Headers
                    .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

                return Results.Text("ikinci sunucu");
            });
        });

        await _second.StartAsync();
        _secondAddress = _second.Urls.First().TrimEnd('/');

        _first = Build(app =>
        {
            app.MapGet("/cross", () => Results.Redirect($"{_secondAddress}/echo"));
            app.MapGet("/same", () => Results.Redirect("/landing"));
            app.MapGet("/landing", (HttpContext context) => Results.Text(
                context.Request.Headers.Authorization.ToString()));

            // Promises a long body and then closes the socket.
            app.MapGet("/truncated", async (HttpContext context) =>
            {
                context.Response.Headers.ContentLength = 100_000;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(new string('a', 128)));
                await context.Response.Body.FlushAsync();
                context.Abort();
            });

            // Sends headers and then holds the connection open without sending the body.
            app.MapGet("/stalled", async (HttpContext context) =>
            {
                context.Response.Headers.ContentLength = 100_000;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(new string('a', 128)));
                await context.Response.Body.FlushAsync();
                await Task.Delay(TimeSpan.FromMinutes(2), context.RequestAborted);
            });

            app.MapGet("/endless", async (HttpContext context) =>
            {
                var chunk = Encoding.UTF8.GetBytes(new string('x', 8192));

                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await context.Response.Body.WriteAsync(chunk);
                }
            });
        });

        await _first.StartAsync();
        _firstAddress = _first.Urls.First().TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        await _first.StopAsync();
        await _first.DisposeAsync();
        await _second.StopAsync();
        await _second.DisposeAsync();
    }

    private static WebApplication Build(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configure(app);

        return app;
    }

    private static GuardedHttpSender Sender(int timeoutSeconds = 30)
    {
        var configuration = new SwaggerDashboardOptions();
        configuration.Outbound.AllowAnyHost = true;
        configuration.Outbound.AllowInsecureHttp = true;
        configuration.Outbound.AllowPrivateNetworks = true;
        configuration.Outbound.TimeoutSeconds = timeoutSeconds;

        var options = new StaticOptions(configuration);
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<SwaggerDashboardOptions>>(options);
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<IOutboundUrlValidator, OutboundUrlValidator>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddHttpClient(OutboundHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(OutboundHttpClient.CreateHandler);

        var provider = services.BuildServiceProvider();

        return new GuardedHttpSender(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOutboundUrlValidator>(),
            options,
            NullLogger<GuardedHttpSender>.Instance);
    }

    private static HttpRequestMessage WithCredentials(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gizli-token");
        request.Headers.TryAddWithoutValidation("X-Company-Key", "gizli-anahtar");
        request.Options.Set(
            OutboundHttpClient.CredentialHeaderMarker, (IReadOnlyList<string>)new[] { "X-Company-Key" });

        return request;
    }

    [Fact]
    public async Task A_credential_is_not_carried_to_another_origin_by_a_redirect()
    {
        // The target names the redirect destination, so forwarding the credential hands the
        // user's token to whoever the target points at.
        var response = await Sender().SendAsync(
            new Uri($"{_firstAddress}/cross"), WithCredentials, 1024 * 1024, CancellationToken.None);

        Assert.True(response.IsCompleted, response.Error);
        Assert.Equal("ikinci sunucu", response.Content);

        Assert.False(_secondHeaders.ContainsKey("Authorization"));
        Assert.False(_secondHeaders.ContainsKey("X-Company-Key"));
    }

    [Fact]
    public async Task A_credential_still_follows_a_redirect_inside_the_same_origin()
    {
        // Dropping it here would break every API that redirects /a to /a/ behind auth.
        var response = await Sender().SendAsync(
            new Uri($"{_firstAddress}/same"), WithCredentials, 1024 * 1024, CancellationToken.None);

        Assert.True(response.IsCompleted, response.Error);
        Assert.Equal("Bearer gizli-token", response.Content);
    }

    [Fact]
    public async Task A_target_that_closes_the_connection_mid_body_is_reported_not_thrown()
    {
        // This used to escape as an exception, which tore down the circuit and left no log row.
        var response = await Sender().SendAsync(
            new Uri($"{_firstAddress}/truncated"),
            uri => new HttpRequestMessage(HttpMethod.Get, uri),
            1024 * 1024,
            CancellationToken.None);

        Assert.False(response.IsCompleted);
        Assert.Contains("gövdesi", response.Error);
    }

    [Fact]
    public async Task A_target_that_stops_sending_the_body_hits_the_timeout()
    {
        // The budget has to cover reading, not just the headers: a target that sends headers
        // and goes quiet otherwise holds the request open forever.
        var response = await Sender(timeoutSeconds: 3).SendAsync(
            new Uri($"{_firstAddress}/stalled"),
            uri => new HttpRequestMessage(HttpMethod.Get, uri),
            1024 * 1024,
            CancellationToken.None);

        Assert.False(response.IsCompleted);
        Assert.Contains("saniye", response.Error);
    }

    [Fact]
    public async Task An_endless_body_stops_at_the_cap_instead_of_being_read_to_the_end()
    {
        var response = await Sender(timeoutSeconds: 20).SendAsync(
            new Uri($"{_firstAddress}/endless"),
            uri => new HttpRequestMessage(HttpMethod.Get, uri),
            64 * 1024,
            CancellationToken.None);

        Assert.True(response.IsCompleted, response.Error);
        Assert.True(response.Truncated);
        Assert.True(response.RawContent.Length <= 64 * 1024);
    }

    private sealed class StaticOptions : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public StaticOptions(SwaggerDashboardOptions value) => CurrentValue = value;

        public SwaggerDashboardOptions CurrentValue { get; }

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}

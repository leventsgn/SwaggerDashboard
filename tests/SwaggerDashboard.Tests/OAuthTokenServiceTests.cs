using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Infrastructure.Http;
using SwaggerDashboard.Infrastructure.Security;
using SwaggerDashboard.Infrastructure.Services;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Drives the client credentials flow against a real token endpoint over a socket, so the
/// request the platform sends is the one an authorization server would actually receive.
/// </summary>
public class OAuthTokenServiceTests : IAsyncLifetime
{
    private WebApplication _server = default!;
    private string _tokenUrl = string.Empty;

    public int Calls { get; private set; }

    public string? LastAuthorization { get; private set; }

    public string? LastBody { get; private set; }

    /// <summary>Set by a test to change what the token endpoint answers.</summary>
    private Func<IResult> _respond = () => Results.Json(new { access_token = "token-1", expires_in = 3600 });

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.MapPost("/token", async (HttpContext context) =>
        {
            Calls++;
            LastAuthorization = context.Request.Headers.Authorization.ToString();

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            LastBody = await reader.ReadToEndAsync();

            return _respond();
        });

        await app.StartAsync();

        _server = app;
        _tokenUrl = app.Urls.First().TrimEnd('/') + "/token";
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    private static OAuthTokenService Service(IMemoryCache? cache = null)
    {
        var configuration = new SwaggerDashboardOptions();
        configuration.Outbound.AllowAnyHost = true;
        configuration.Outbound.AllowInsecureHttp = true;
        configuration.Outbound.AllowPrivateNetworks = true;

        var options = new StaticOptionsMonitor(configuration);
        var services = new ServiceCollection();
        services.AddHttpClient(OutboundHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(OutboundHttpClient.CreateHandler);
        services.AddSingleton<IOptionsMonitor<SwaggerDashboardOptions>>(options);
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<IOutboundUrlValidator, OutboundUrlValidator>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var provider = services.BuildServiceProvider();

        var sender = new GuardedHttpSender(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOutboundUrlValidator>(),
            options,
            NullLogger<GuardedHttpSender>.Instance);

        return new OAuthTokenService(
            sender,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OAuthTokenService>.Instance);
    }

    private ApiCredential Credential(string? scope = null) => new()
    {
        Kind = ApiAuthKind.OAuth2ClientCredentials,
        TokenUrl = _tokenUrl,
        UserName = "client-1",
        Secret = "gizli",
        Scope = scope,
    };

    [Fact]
    public async Task A_token_is_fetched_with_the_client_credentials_grant()
    {
        var result = await Service().GetTokenAsync("user:1", Credential());

        Assert.True(result.Success, result.Error);
        Assert.Equal("token-1", result.AccessToken);
        Assert.Contains("grant_type=client_credentials", LastBody);
    }

    [Fact]
    public async Task The_client_id_and_secret_go_in_the_authorization_header_not_the_body()
    {
        // RFC 6749 §2.3.1. Sending them in the body as well would put the secret on the wire
        // twice for no gain.
        await Service().GetTokenAsync("user:1", Credential());

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-1:gizli"));
        Assert.Equal(expected, LastAuthorization);
        Assert.DoesNotContain("gizli", LastBody);
    }

    [Fact]
    public async Task A_requested_scope_is_sent()
    {
        await Service().GetTokenAsync("user:1", Credential("read write"));

        Assert.Contains("scope=read+write", LastBody);
    }

    [Fact]
    public async Task A_token_is_reused_until_it_is_close_to_expiring()
    {
        // Fetching a token per call would triple the traffic to the authorization server.
        var service = Service();

        await service.GetTokenAsync("user:1", Credential());
        await service.GetTokenAsync("user:1", Credential());
        await service.GetTokenAsync("user:1", Credential());

        Assert.Equal(1, Calls);
    }

    [Fact]
    public async Task A_short_lived_token_is_not_cached_past_its_safety_margin()
    {
        // 10 seconds minus the 30 second margin is negative, so it must not be stored at all
        // rather than stored with a nonsense lifetime.
        _respond = () => Results.Json(new { access_token = "kısa", expires_in = 10 });
        var service = Service();

        await service.GetTokenAsync("user:1", Credential());
        await service.GetTokenAsync("user:1", Credential());

        Assert.Equal(2, Calls);
    }

    [Fact]
    public async Task Changing_the_client_secret_fetches_a_new_token()
    {
        // Otherwise a user who corrects a wrong secret keeps being served the token obtained
        // with the old one, and is told the new credentials work without them being tried.
        var service = Service();

        await service.GetTokenAsync("user:1", Credential());
        await service.GetTokenAsync("user:1", Credential() with { Secret = "yeni-gizli" });

        Assert.Equal(2, Calls);
    }

    [Fact]
    public async Task Another_user_does_not_share_the_cached_token()
    {
        var service = Service();

        await service.GetTokenAsync("user:1", Credential());
        await service.GetTokenAsync("user:2", Credential());

        Assert.Equal(2, Calls);
    }

    [Fact]
    public async Task A_rejected_grant_is_reported_with_the_servers_reason()
    {
        _respond = () => Results.Json(new { error = "invalid_client" }, statusCode: 401);

        var result = await Service().GetTokenAsync("user:1", Credential());

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.Contains("invalid_client", result.Error);
    }

    [Fact]
    public async Task A_response_without_an_access_token_is_refused()
    {
        _respond = () => Results.Json(new { not_a_token = "x" });

        var result = await Service().GetTokenAsync("user:1", Credential());

        Assert.False(result.Success);
        Assert.Contains("access_token", result.Error);
    }

    [Fact]
    public async Task A_token_url_that_is_missing_or_malformed_is_refused_before_any_call()
    {
        var service = Service();

        Assert.False((await service.GetTokenAsync("user:1", Credential() with { TokenUrl = null })).Success);
        Assert.False((await service.GetTokenAsync("user:1", Credential() with { TokenUrl = "adres değil" })).Success);
        Assert.False((await service.GetTokenAsync("user:1", Credential() with { UserName = null })).Success);

        Assert.Equal(0, Calls);
    }

    [Fact]
    public async Task A_token_url_the_outbound_policy_blocks_is_not_called()
    {
        // The token endpoint is a user supplied address like any other target, so it goes
        // through the same SSRF checks; a whitelisted platform must not be talked into
        // posting its client secret to an arbitrary host.
        var configuration = new SwaggerDashboardOptions();
        configuration.Outbound.AllowedHostSuffixes.Add("company.com");
        configuration.Outbound.AllowInsecureHttp = true;

        var options = new StaticOptionsMonitor(configuration);
        var services = new ServiceCollection();
        services.AddHttpClient(OutboundHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(OutboundHttpClient.CreateHandler);
        services.AddSingleton<IOptionsMonitor<SwaggerDashboardOptions>>(options);
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();

        // The socket level address re-check is built from this registration, so the handler
        // cannot be created without it.
        services.AddSingleton<IOutboundUrlValidator, OutboundUrlValidator>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IOutboundUrlValidator>();

        var service = new OAuthTokenService(
            new GuardedHttpSender(
                provider.GetRequiredService<IHttpClientFactory>(),
                validator,
                options,
                NullLogger<GuardedHttpSender>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OAuthTokenService>.Instance);

        var result = await service.GetTokenAsync("user:1", Credential());

        Assert.False(result.Success);
        Assert.Equal(0, Calls);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public StaticOptionsMonitor(SwaggerDashboardOptions value) => CurrentValue = value;

        public SwaggerDashboardOptions CurrentValue { get; }

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}

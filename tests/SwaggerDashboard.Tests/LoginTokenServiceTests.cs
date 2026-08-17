using System.Text;
using System.Text.Json;
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
/// Signs in against a real login endpoint over a socket, so the request the platform sends is
/// the one the target API would actually receive.
/// </summary>
public class LoginTokenServiceTests : IAsyncLifetime
{
    private WebApplication _server = default!;
    private string _loginUrl = string.Empty;

    private int Calls { get; set; }

    private string? LastBody { get; set; }

    private string? LastContentType { get; set; }

    /// <summary>Set by a test to change what the login endpoint answers.</summary>
    private Func<IResult> _respond = () => Results.Json(new { token = "token-1" });

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.MapPost("/login", async (HttpContext context) =>
        {
            Calls++;
            LastContentType = context.Request.ContentType;

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            LastBody = await reader.ReadToEndAsync();

            return _respond();
        });

        await app.StartAsync();

        _server = app;
        _loginUrl = app.Urls.First().TrimEnd('/') + "/login";
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    private static LoginTokenService Service(IMemoryCache? cache = null)
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

        return new LoginTokenService(
            sender,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<LoginTokenService>.Instance);
    }

    private ApiCredential Credential(
        string? userField = null, string? passwordField = null, string? tokenField = null) => new()
    {
        Kind = ApiAuthKind.LoginEndpoint,
        TokenUrl = _loginUrl,
        UserName = "levent",
        Secret = "gizli-parola",
        LoginUserField = userField,
        LoginPasswordField = passwordField,
        TokenField = tokenField,
    };

    [Fact]
    public async Task Signing_in_posts_json_and_returns_the_token()
    {
        var result = await Service().GetTokenAsync("user:1", Credential());

        Assert.True(result.Success, result.Error);
        Assert.Equal("token-1", result.AccessToken);
        Assert.Contains("application/json", LastContentType);

        var sent = JsonDocument.Parse(LastBody!).RootElement;
        Assert.Equal("levent", sent.GetProperty("username").GetString());
        Assert.Equal("gizli-parola", sent.GetProperty("password").GetString());
    }

    [Fact]
    public async Task The_request_field_names_can_be_changed_for_an_api_that_names_them_otherwise()
    {
        // A login endpoint is an ordinary endpoint of the API rather than a standard, so its
        // authors named these whatever they liked.
        var result = await Service().GetTokenAsync(
            "user:1", Credential(userField: "kullaniciAdi", passwordField: "sifre"));

        Assert.True(result.Success, result.Error);

        var sent = JsonDocument.Parse(LastBody!).RootElement;
        Assert.Equal("levent", sent.GetProperty("kullaniciAdi").GetString());
        Assert.Equal("gizli-parola", sent.GetProperty("sifre").GetString());
    }

    [Fact]
    public async Task A_token_inside_a_wrapper_is_found_without_being_told_where()
    {
        _respond = () => Results.Json(new { basarili = true, data = new { accessToken = "sarmalanmis" } });

        var result = await Service().GetTokenAsync("user:1", Credential());

        Assert.True(result.Success, result.Error);
        Assert.Equal("sarmalanmis", result.AccessToken);
    }

    [Fact]
    public async Task A_named_token_field_is_followed()
    {
        _respond = () => Results.Json(new { oturum = new { anahtar = "elle-verilen" } });

        var result = await Service().GetTokenAsync("user:1", Credential(tokenField: "oturum.anahtar"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("elle-verilen", result.AccessToken);
    }

    [Fact]
    public async Task The_token_is_reused_until_it_expires()
    {
        // Without this a sweep of a large API would sign in once per endpoint, which most
        // login endpoints treat as an attack.
        var cache = new MemoryCache(new MemoryCacheOptions());
        _respond = () => Results.Json(new { token = "token-1", expires_in = 3600 });

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await Service(cache).GetTokenAsync("user:1", Credential())).Success);
        }

        Assert.Equal(1, Calls);
    }

    [Fact]
    public async Task Correcting_the_password_signs_in_again_instead_of_reusing_the_old_token()
    {
        // The password takes part in the cache key as a hash. Without it a user who fixed a
        // typo would be told the corrected credentials work while nothing was retried.
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Service(cache).GetTokenAsync("user:1", Credential() with { Secret = "yanlis" });
        await Service(cache).GetTokenAsync("user:1", Credential() with { Secret = "dogru" });

        Assert.Equal(2, Calls);
    }

    [Fact]
    public async Task A_refused_sign_in_is_reported_with_what_the_target_said()
    {
        _respond = () => Results.Json(new { mesaj = "kullanıcı adı veya parola hatalı" }, statusCode: 401);

        var result = await Service().GetTokenAsync("user:1", Credential());

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
        Assert.Contains("parola", result.Error);
    }

    [Fact]
    public async Task A_response_with_no_token_says_which_box_to_fill_in()
    {
        // The user cannot guess the field name without seeing the answer, so the message
        // carries both the instruction and the body it could not read.
        _respond = () => Results.Json(new { kullanici = "levent", rol = "admin" });

        var result = await Service().GetTokenAsync("user:1", Credential());

        Assert.False(result.Success);
        Assert.Contains("Token alanı", result.Error);
        Assert.Contains("rol", result.Error);
    }

    [Fact]
    public async Task A_missing_address_is_refused_before_anything_is_sent()
    {
        var result = await Service().GetTokenAsync("user:1", Credential() with { TokenUrl = null });

        Assert.False(result.Success);
        Assert.Contains("Login adresi", result.Error);
        Assert.Equal(0, Calls);
    }

    [Fact]
    public async Task A_missing_user_name_is_refused_before_anything_is_sent()
    {
        var result = await Service().GetTokenAsync("user:1", Credential() with { UserName = null });

        Assert.False(result.Success);
        Assert.Contains("Kullanıcı adı", result.Error);
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

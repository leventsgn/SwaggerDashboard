using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure;
using SwaggerDashboard.Infrastructure.Identity;
using SwaggerDashboard.Infrastructure.Persistence;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Drives the whole stack against a real target API: first visit provisions from a pasted
/// swagger UI page, later visits read the stored dashboard, and calls go out through the
/// proxy with logging applied.
/// </summary>
public class EndToEndTests : IAsyncLifetime
{
    private TargetApiFixture _target = default!;
    private ServiceProvider _services = default!;
    private SqliteConnection _connection = default!;

    private static readonly ResolveContext Developer =
        new("1", "dev", Roles.Developer, IsAuthenticated: true);

    public async Task InitializeAsync()
    {
        _target = await TargetApiFixture.StartAsync();

        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The target runs on loopback over plain http, so the development style
                // policy is what makes this reachable at all.
                ["SwaggerDashboard:Outbound:AllowAnyHost"] = "true",
                ["SwaggerDashboard:Outbound:AllowInsecureHttp"] = "true",
                ["SwaggerDashboard:Outbound:AllowPrivateNetworks"] = "true",
                ["SwaggerDashboard:Outbound:TimeoutSeconds"] = "15",
                ["SwaggerDashboard:Logging:PersistBodies"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSwaggerDashboardInfrastructure(configuration);

        // Swap the SQL Server context registration for the in-memory SQLite one.
        foreach (var descriptor in services
                     .Where(d => d.ServiceType == typeof(DbContextOptions<SwaggerDashboardDbContext>))
                     .ToList())
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<SwaggerDashboardDbContext>(o => o.UseSqlite(_connection));

        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwaggerDashboardDbContext>();
        await db.Database.EnsureCreatedAsync();

        // The proxy authorises every call against the stored user rather than against the
        // caller's claims, so the fixture needs a real row behind the Developer context.
        db.Users.Add(new DashboardUser
        {
            Id = 1,
            UserName = "dev",
            Role = Roles.Developer,
            IsActive = true,
            PasswordHash = "unused",
            PasswordSalt = "unused",
            PasswordIterations = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        _connection.Dispose();
        await _target.DisposeAsync();
    }

    private IServiceScope Scope() => _services.CreateScope();

    [Fact]
    public async Task Pasting_the_swagger_ui_page_provisions_from_the_discovered_document()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();

        var result = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.WasProvisioned);
        Assert.Equal("Item API", result.Dashboard!.Title);
        Assert.Equal(5, result.Dashboard.Operations.Count);

        // The identity is the JSON document, not the HTML page that was pasted.
        Assert.EndsWith("/swagger/v1/swagger.json", result.Definition!.SwaggerUrlNormalized);

        // A relative servers entry resolves against the document's own origin.
        Assert.Equal($"http://{_target.Authority}/v1", result.Definition.BaseUrl);
    }

    [Fact]
    public async Task A_get_call_goes_out_through_the_proxy_and_is_logged()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();
        var logs = scope.ServiceProvider.GetRequiredService<IRequestLogService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        Assert.True(resolved.IsSuccess, resolved.Error);

        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition!.Id,
            OperationSlug = operation.Slug,
            PathParameters = { ["id"] = "42" },
            QueryParameters = { new("expand", "tags") },
            Headers = { ["X-Correlation-Id"] = "corr-1" },
            UserId = "1",
            ClientIp = "203.0.113.9",
        });

        Assert.True(response.Success, response.Error);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"id\":\"42\"", response.ResponseBody);
        Assert.Equal("/v1/items/42", _target.LastRequestPath);
        Assert.Equal("?expand=tags", _target.LastQueryString);
        Assert.Equal("corr-1", _target.LastRequestHeaders["X-Correlation-Id"]);

        var recorded = await logs.GetRecentAsync(resolved.Definition.Id, 10);
        var entry = Assert.Single(recorded);
        Assert.True(entry.IsSuccess);
        Assert.Equal("GET", entry.HttpMethod);
        Assert.Equal("203.0.113.9", entry.ClientIp);
        Assert.Equal("1", entry.UserId);
        Assert.NotNull(entry.ApiEndpointId);
    }

    [Fact]
    public async Task A_bearer_token_reaches_the_target_but_is_masked_in_the_log()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();
        var credentials = scope.ServiceProvider.GetRequiredService<IApiCredentialStore>();
        var logs = scope.ServiceProvider.GetRequiredService<IRequestLogService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        credentials.Set("1", resolved.Definition!.Id, new ApiCredential
        {
            Kind = ApiAuthKind.Bearer,
            Secret = "top-secret-token",
        });

        await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition.Id,
            OperationSlug = operation.Slug,
            PathParameters = { ["id"] = "7" },
            UserId = "1",
        });

        Assert.Equal("Bearer top-secret-token", _target.LastRequestHeaders["Authorization"]);

        var entry = (await logs.GetRecentAsync(resolved.Definition.Id, 1)).Single();
        Assert.DoesNotContain("top-secret-token", entry.RequestHeadersJson);
        Assert.Contains("Bearer ***", entry.RequestHeadersJson);
    }

    [Fact]
    public async Task An_api_key_in_the_query_string_reaches_the_target_but_is_masked_everywhere_else()
    {
        // The key is part of the address here, so masking headers alone left it in the log
        // table, on the admin log screen, in the response panel and in the generated snippet.
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();
        var credentials = scope.ServiceProvider.GetRequiredService<IApiCredentialStore>();
        var logs = scope.ServiceProvider.GetRequiredService<IRequestLogService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        credentials.Set("1", resolved.Definition!.Id, new ApiCredential
        {
            Kind = ApiAuthKind.ApiKey,
            ParameterName = "api_key",
            ParameterIn = "query",
            Secret = "cok-gizli-anahtar",
        });

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition.Id,
            OperationSlug = operation.Slug,
            PathParameters = { ["id"] = "42" },
            UserId = "1",
        });

        Assert.True(response.Success, response.Error);
        Assert.Contains("api_key=cok-gizli-anahtar", _target.LastQueryString);

        Assert.DoesNotContain("cok-gizli-anahtar", response.RequestUrl);
        Assert.Contains("api_key=***", Uri.UnescapeDataString(response.RequestUrl));

        var entry = (await logs.GetRecentAsync(resolved.Definition.Id, 1)).Single();
        Assert.DoesNotContain("cok-gizli-anahtar", entry.RequestUrl);
    }

    [Fact]
    public async Task An_api_key_header_is_masked_whatever_the_document_calls_it()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();
        var credentials = scope.ServiceProvider.GetRequiredService<IApiCredentialStore>();
        var logs = scope.ServiceProvider.GetRequiredService<IRequestLogService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        // A name that is not on any fixed mask list — the document decides it, not the platform.
        credentials.Set("1", resolved.Definition!.Id, new ApiCredential
        {
            Kind = ApiAuthKind.ApiKey,
            ParameterName = "X-Auth-Token",
            ParameterIn = "header",
            Secret = "baslik-gizli-anahtar",
        });

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition.Id,
            OperationSlug = operation.Slug,
            PathParameters = { ["id"] = "42" },
            UserId = "1",
        });

        Assert.Equal("baslik-gizli-anahtar", _target.LastRequestHeaders["X-Auth-Token"]);
        Assert.Equal("***", response.RequestHeaders["X-Auth-Token"]);

        var entry = (await logs.GetRecentAsync(resolved.Definition.Id, 1)).Single();
        Assert.DoesNotContain("baslik-gizli-anahtar", entry.RequestHeadersJson);
    }

    [Fact]
    public async Task A_post_sends_the_json_body_and_returns_the_target_status()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "createItem");

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition!.Id,
            OperationSlug = operation.Slug,
            Body = """{"name":"Widget","count":3}""",
            ContentType = "application/json",
            UserId = "1",
        });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("""{"name":"Widget","count":3}""", _target.LastRequestBody);
    }

    [Fact]
    public async Task A_target_error_is_reported_as_a_response_not_an_exception()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "boom");

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition!.Id,
            OperationSlug = operation.Slug,
            UserId = "1",
        });

        Assert.False(response.Success);
        Assert.Equal(500, response.StatusCode);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task An_html_response_comes_back_as_text_for_the_sandboxed_preview()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "page");

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition!.Id,
            OperationSlug = operation.Slug,
            UserId = "1",
        });

        Assert.Contains("text/html", response.ContentType);
        Assert.Contains("<h1>hello</h1>", response.ResponseBody);
        Assert.False(response.IsBinary);
    }

    [Fact]
    public async Task A_binary_response_is_offered_as_a_download_with_the_name_the_target_chose()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();
        var downloads = scope.ServiceProvider.GetRequiredService<IResponseDownloadStore>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "report");

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition!.Id,
            OperationSlug = operation.Slug,
            UserId = "1",
        });

        Assert.True(response.IsBinary);
        Assert.Null(response.ResponseBody);
        Assert.Equal("rapor.pdf", response.FileName);
        Assert.NotNull(response.DownloadToken);

        // Another session must not be able to redeem the token.
        Assert.Null(downloads.Take(response.DownloadToken!, "someone-else"));

        var download = downloads.Take(response.DownloadToken!, "1");
        Assert.NotNull(download);
        Assert.Equal("application/pdf", download!.ContentType);
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 1, 2, 3 }, download.Content);

        // The token is single use.
        Assert.Null(downloads.Take(response.DownloadToken!, "1"));
    }

    [Fact]
    public async Task Favourites_and_recents_follow_the_user()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();
        var userEndpoints = scope.ServiceProvider.GetRequiredService<IUserEndpointService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var apiId = resolved.Definition!.Id;
        var get = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");
        var post = resolved.Dashboard.Operations.Single(o => o.OperationId == "createItem");

        Assert.True(await userEndpoints.ToggleFavoriteAsync(apiId, get.Slug, "1"));
        Assert.Contains(get.Slug, await userEndpoints.GetFavoriteSlugsAsync(apiId, "1"));

        // Favourites are per user, not per API.
        Assert.Empty(await userEndpoints.GetFavoriteSlugsAsync(apiId, "2"));

        Assert.False(await userEndpoints.ToggleFavoriteAsync(apiId, get.Slug, "1"));
        Assert.Empty(await userEndpoints.GetFavoriteSlugsAsync(apiId, "1"));

        await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = apiId, OperationSlug = get.Slug, PathParameters = { ["id"] = "1" }, UserId = "1",
        });
        await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = apiId, OperationSlug = post.Slug, Body = """{"name":"x"}""", UserId = "1",
        });

        // Newest first, and each endpoint appears once however often it was called.
        var recents = await userEndpoints.GetRecentSlugsAsync(apiId, "1", 8);
        Assert.Equal(new[] { post.Slug, get.Slug }, recents);
        Assert.Empty(await userEndpoints.GetRecentSlugsAsync(apiId, "2", 8));
    }

    [Fact]
    public async Task The_proxy_refuses_an_endpoint_that_is_not_part_of_the_document()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);

        var response = await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = resolved.Definition!.Id,
            OperationSlug = "made-up-endpoint",
            UserId = "1",
        });

        Assert.False(response.Success);
        Assert.Contains("Endpoint bulunamadı", response.Error);
    }

    [Fact]
    public async Task Every_spelling_of_the_address_resolves_to_the_same_registration()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var db = scope.ServiceProvider.GetRequiredService<SwaggerDashboardDbContext>();

        var viaUiPage = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var viaShortForm = await definitions.ResolveAsync($"http://{_target.Authority}/swagger", Developer);
        var viaDocument = await definitions.ResolveAsync(
            $"http://{_target.Authority}/swagger/v1/swagger.json", Developer);

        Assert.True(viaUiPage.IsSuccess, viaUiPage.Error);
        Assert.True(viaShortForm.IsSuccess, viaShortForm.Error);
        Assert.True(viaDocument.IsSuccess, viaDocument.Error);

        Assert.Equal(viaUiPage.Definition!.Id, viaShortForm.Definition!.Id);
        Assert.Equal(viaUiPage.Definition.Id, viaDocument.Definition!.Id);
        Assert.Equal(1, await db.ApiDefinitions.CountAsync());

        // Each spelling is remembered, so none of them pays for discovery twice.
        Assert.True(await db.ApiUrlAliases.CountAsync() >= 2);
    }

    [Fact]
    public async Task An_arbitrary_path_still_resolves_through_the_root_probe()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();

        // Probing falls back to the well known document locations at the host root, so a
        // user who pastes any page of an API that exposes swagger normally still lands on
        // the right dashboard.
        var result = await definitions.ResolveAsync($"http://{_target.Authority}/nothing-here", Developer);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Item API", result.Dashboard!.Title);
    }

    [Fact]
    public async Task An_unreachable_host_reports_the_failure_instead_of_registering_anything()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var db = scope.ServiceProvider.GetRequiredService<SwaggerDashboardDbContext>();

        // Port 1 on loopback has nothing listening, so the connection is refused.
        var result = await definitions.ResolveAsync("http://127.0.0.1:1/swagger", Developer);

        Assert.Equal(ResolveStatus.ProvisioningFailed, result.Status);
        Assert.Equal(0, await db.ApiDefinitions.CountAsync());
    }

    /// <summary>Adds a user the proxy will authorise against, and returns its id as a string.</summary>
    private async Task<string> AddUserAsync(string userName, string role, bool isActive = true)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<SwaggerDashboardDbContext>();

        var user = new DashboardUser
        {
            UserName = userName,
            Role = role,
            IsActive = isActive,
            PasswordHash = "unused",
            PasswordSalt = "unused",
            PasswordIterations = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.Id.ToString();
    }

    private async Task<ProxyResponse> CallAsync(int apiDefinitionId, string slug, string? userId)
    {
        using var scope = Scope();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();

        return await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = apiDefinitionId,
            OperationSlug = slug,
            PathParameters = { ["id"] = "42" },
            UserId = userId,
        });
    }

    [Fact]
    public async Task A_read_only_user_cannot_run_an_endpoint_even_if_the_call_reaches_the_proxy()
    {
        // The screen hides the run button for this role, but hiding a control is not a
        // control: the check has to hold when the call arrives anyway.
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        var readOnly = await AddUserAsync("okur", Roles.ReadOnly);
        var response = await CallAsync(resolved.Definition!.Id, operation.Slug, readOnly);

        Assert.False(response.Success);
        Assert.Contains("yetkiniz yok", response.Error);
        Assert.Equal(0, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_stops_working_without_waiting_for_its_session_to_expire()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        var userId = await AddUserAsync("gidecek", Roles.Developer);
        Assert.True((await CallAsync(resolved.Definition!.Id, operation.Slug, userId)).Success);

        await users.SetActiveAsync(int.Parse(userId), false);

        var afterDisabling = await CallAsync(resolved.Definition.Id, operation.Slug, userId);
        Assert.False(afterDisabling.Success);
        Assert.Contains("etkin değil", afterDisabling.Error);
    }

    [Fact]
    public async Task A_demoted_user_loses_access_on_the_next_call()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        var userId = await AddUserAsync("düşecek", Roles.Tester);
        Assert.True((await CallAsync(resolved.Definition!.Id, operation.Slug, userId)).Success);

        await users.SetRoleAsync(int.Parse(userId), Roles.ReadOnly);

        Assert.False((await CallAsync(resolved.Definition.Id, operation.Slug, userId)).Success);
    }

    [Fact]
    public async Task A_call_without_a_signed_in_user_is_refused()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        Assert.False((await CallAsync(resolved.Definition!.Id, operation.Slug, null)).Success);
        Assert.False((await CallAsync(resolved.Definition.Id, operation.Slug, "9999")).Success);
    }

    [Fact]
    public async Task An_api_restricted_to_a_role_cannot_be_called_by_another_role()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "getItemById");

        await definitions.UpdateMetadataAsync(new UpdateApiRequest
        {
            Id = resolved.Definition!.Id,
            Name = resolved.Definition.Name,
            BaseUrl = resolved.Definition.BaseUrl,
            AllowedRoles = Roles.Admin,
            Actor = "dev",
        });

        var tester = await AddUserAsync("testçi", Roles.Tester);
        var response = await CallAsync(resolved.Definition.Id, operation.Slug, tester);

        Assert.False(response.Success);
        Assert.Contains("yetkiniz yok", response.Error);
    }

    [Fact]
    public async Task A_sweep_calls_every_endpoint_with_generated_data_and_reports_each_status()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var sweep = scope.ServiceProvider.GetRequiredService<IEndpointSweepService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var request = new SweepRequest { ApiDefinitionId = resolved.Definition!.Id, UserId = "1" };

        var results = new List<SweepResult>();
        await foreach (var result in sweep.RunAsync(request))
        {
            results.Add(result);
        }

        Assert.Equal(5, await sweep.CountAsync(request));
        Assert.Equal(5, results.Count);

        // Every endpoint ran without a single value being typed, including the one with a
        // required path parameter and the one with a required body.
        Assert.Equal(200, results.Single(r => r.Path == "/items/{id}").StatusCode);
        Assert.Equal(201, results.Single(r => r.Method == "POST").StatusCode);
        Assert.Equal(4, results.Count(r => r.Outcome == SweepOutcome.Success));

        // The deliberately broken endpoint is reported as failed rather than aborting the run.
        var failed = results.Single(r => r.Outcome == SweepOutcome.Failed);
        Assert.Equal("/boom", failed.Path);
        Assert.Equal(500, failed.StatusCode);

        // The generated body really reached the target, not an empty one.
        Assert.Contains("\"name\"", _target.LastRequestBody);
    }

    [Fact]
    public async Task A_read_only_sweep_leaves_the_writing_endpoints_alone()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var sweep = scope.ServiceProvider.GetRequiredService<IEndpointSweepService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);

        var results = new List<SweepResult>();
        await foreach (var result in sweep.RunAsync(new SweepRequest
        {
            ApiDefinitionId = resolved.Definition!.Id,
            UserId = "1",
            ReadOnlyMethodsOnly = true,
        }))
        {
            results.Add(result);
        }

        Assert.Equal(4, results.Count);
        Assert.DoesNotContain(results, r => r.Method == "POST");
    }

    [Fact]
    public async Task A_sweep_is_logged_but_does_not_count_as_recently_used()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var sweep = scope.ServiceProvider.GetRequiredService<IEndpointSweepService>();
        var proxy = scope.ServiceProvider.GetRequiredService<IApiProxyService>();
        var userEndpoints = scope.ServiceProvider.GetRequiredService<IUserEndpointService>();
        var logs = scope.ServiceProvider.GetRequiredService<IRequestLogService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        var definition = resolved.Definition!;

        await foreach (var _ in sweep.RunAsync(new SweepRequest { ApiDefinitionId = definition.Id, UserId = "1" }))
        {
        }

        // A sweep touches every endpoint, so counting it would make the shortcut list say the
        // user recently used all of them.
        Assert.Empty(await userEndpoints.GetRecentSlugsAsync(definition.Id, "1", 8));

        // It is still a real outbound call and stays in the audit trail.
        var recorded = await logs.GetRecentAsync(definition.Id, 20);
        Assert.Equal(5, recorded.Count);
        Assert.All(recorded, entry => Assert.True(entry.IsBulkRun));

        // A call the user makes by hand still shows up.
        var operation = resolved.Dashboard!.Operations.Single(o => o.OperationId == "page");
        await proxy.ExecuteAsync(new ProxyRequest
        {
            ApiDefinitionId = definition.Id,
            OperationSlug = operation.Slug,
            UserId = "1",
        });

        Assert.Equal(operation.Slug, Assert.Single(await userEndpoints.GetRecentSlugsAsync(definition.Id, "1", 8)));
    }

    [Fact]
    public async Task A_cancelled_sweep_stops_and_keeps_what_it_already_ran()
    {
        using var scope = Scope();
        var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();
        var sweep = scope.ServiceProvider.GetRequiredService<IEndpointSweepService>();

        var resolved = await definitions.ResolveAsync("http://" + _target.SwaggerRouteTail, Developer);
        using var cts = new CancellationTokenSource();

        var results = new List<SweepResult>();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var result in sweep.RunAsync(
                new SweepRequest { ApiDefinitionId = resolved.Definition!.Id, UserId = "1" }, cts.Token))
            {
                results.Add(result);
                cts.Cancel();
            }
        });

        // The rows produced before the stop are the user's; they are not rolled back.
        Assert.Single(results);
    }
}

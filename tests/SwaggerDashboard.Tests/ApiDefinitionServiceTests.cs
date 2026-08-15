using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Hashing;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Caching;
using SwaggerDashboard.Infrastructure.Persistence;
using SwaggerDashboard.Infrastructure.Services;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Covers the prefixed URL model end to end: resolve, provision on first visit, serve the
/// stored dashboard afterwards, and rebuild only when the document really changed.
/// </summary>
public class ApiDefinitionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SwaggerDashboardDbContext _db;
    private readonly RecordingDocumentService _documents;
    private readonly MemoryDashboardCache _cache;
    private readonly ApiDefinitionService _service;
    private readonly StaticOptionsMonitor _settings;
    private readonly HashService _hashService = new();

    private static readonly ResolveContext Developer =
        new("7", "dev", Roles.Developer, IsAuthenticated: true);

    private static readonly ResolveContext Tester =
        new("8", "tester", Roles.Tester, IsAuthenticated: true);

    private static readonly ResolveContext Anonymous = new(null, null, null, IsAuthenticated: false);

    public ApiDefinitionServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<SwaggerDashboardDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new SwaggerDashboardDbContext(options);
        _db.Database.EnsureCreated();

        _settings = new StaticOptionsMonitor(new SwaggerDashboardOptions());
        _cache = new MemoryDashboardCache(new MemoryCache(new MemoryCacheOptions()), _settings);
        _documents = new RecordingDocumentService();

        _service = new ApiDefinitionService(
            _db,
            _documents,
            new DashboardGeneratorService(NullLogger<DashboardGeneratorService>.Instance),
            _hashService,
            _cache,
            _settings,
            NullLogger<ApiDefinitionService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task First_visit_to_an_unknown_url_provisions_the_api()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var result = await _service.ResolveAsync("api.company.com/swagger", Developer);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(result.WasProvisioned);
        Assert.Equal("Customer API", result.Dashboard!.Title);
        Assert.True(result.Definition!.IsAutoProvisioned);
        Assert.Equal("dev", result.Definition.CreatedBy);
    }

    [Fact]
    public async Task Second_visit_serves_the_stored_dashboard_without_touching_the_target()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        await _service.ResolveAsync("api.company.com/swagger", Developer);
        var fetchesAfterFirstVisit = _documents.FetchCount;

        _cache.InvalidateApi(1);
        var second = await _service.ResolveAsync("api.company.com/swagger", Developer);

        Assert.True(second.IsSuccess);
        Assert.False(second.WasProvisioned);

        // This is the core promise of the design: no download and no re-parse on a revisit,
        // even with a cold cache.
        Assert.Equal(fetchesAfterFirstVisit, _documents.FetchCount);
    }

    [Fact]
    public async Task The_ui_page_and_the_json_url_collapse_onto_one_registration()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var first = await _service.ResolveAsync("api.company.com/swagger/index.html", Developer);
        var second = await _service.ResolveAsync("api.company.com/swagger", Developer);

        Assert.True(first.IsSuccess, first.Error);
        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(first.Definition!.Id, second.Definition!.Id);
        Assert.Equal(1, await _db.ApiDefinitions.CountAsync());
    }

    [Fact]
    public async Task An_anonymous_visitor_cannot_provision_but_gets_a_clear_reason()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var result = await _service.ResolveAsync("api.company.com/swagger", Anonymous);

        Assert.Equal(ResolveStatus.ProvisioningForbidden, result.Status);
        Assert.Contains("giriş", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _documents.FetchCount);
    }

    [Fact]
    public async Task A_tester_cannot_provision_but_can_open_an_api_a_developer_registered()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var denied = await _service.ResolveAsync("api.company.com/swagger", Tester);
        Assert.Equal(ResolveStatus.ProvisioningForbidden, denied.Status);

        await _service.ResolveAsync("api.company.com/swagger", Developer);

        var allowed = await _service.ResolveAsync("api.company.com/swagger", Tester);
        Assert.True(allowed.IsSuccess, allowed.Error);
    }

    [Fact]
    public async Task A_short_alias_resolves_to_the_same_api_as_the_long_url()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var registration = await _service.RegisterAsync(new RegisterApiRequest
        {
            SwaggerUrl = "https://api.company.com/swagger",
            RouteName = "customer-api",
        });

        Assert.True(registration.Success, registration.Error);

        var viaAlias = await _service.ResolveAsync("customer-api", Developer);
        var viaUrl = await _service.ResolveAsync("api.company.com/swagger", Developer);

        Assert.True(viaAlias.IsSuccess, viaAlias.Error);
        Assert.Equal(viaUrl.Definition!.Id, viaAlias.Definition!.Id);
    }

    [Fact]
    public async Task A_reserved_alias_is_refused()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var result = await _service.RegisterAsync(new RegisterApiRequest
        {
            SwaggerUrl = "https://api.company.com/swagger",
            RouteName = "admin",
        });

        Assert.False(result.Success);
        Assert.Equal(0, _documents.FetchCount);
    }

    [Fact]
    public async Task Registering_the_same_document_twice_is_refused_with_the_existing_route()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var first = await _service.RegisterAsync(new RegisterApiRequest
        {
            SwaggerUrl = "https://api.company.com/swagger",
            RouteName = "customer-api",
        });
        Assert.True(first.Success);

        var second = await _service.RegisterAsync(new RegisterApiRequest
        {
            SwaggerUrl = "https://api.company.com/swagger/index.html",
        });

        Assert.False(second.Success);
        Assert.Contains("customer-api", second.Error);
    }

    [Fact]
    public async Task Concurrent_first_visits_produce_exactly_one_registration()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        // Each caller needs its own DbContext, mirroring one scope per request.
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(async () =>
            {
                using var scopedDb = new SwaggerDashboardDbContext(
                    new DbContextOptionsBuilder<SwaggerDashboardDbContext>().UseSqlite(_connection).Options);

                var service = new ApiDefinitionService(
                    scopedDb,
                    _documents,
                    new DashboardGeneratorService(NullLogger<DashboardGeneratorService>.Instance),
                    _hashService,
                    _cache,
                    new StaticOptionsMonitor(new SwaggerDashboardOptions()),
                    NullLogger<ApiDefinitionService>.Instance);

                return await service.ResolveAsync("api.company.com/swagger", Developer);
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error));
        Assert.Equal(1, await _db.ApiDefinitions.CountAsync());
        Assert.Equal(1, results.Count(r => r.WasProvisioned));
    }

    [Fact]
    public async Task Provisioning_writes_the_endpoint_rows_used_for_search_and_diffing()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var result = await _service.ResolveAsync("api.company.com/swagger", Developer);

        var endpoints = await _db.ApiEndpoints.Where(e => e.ApiDefinitionId == result.Definition!.Id).ToListAsync();

        Assert.Equal(result.Dashboard!.Operations.Count, endpoints.Count);
        Assert.Contains(endpoints, e => e.OperationId == "getCustomerById" && e.RequiresAuthentication);
        Assert.Contains(endpoints, e => e.OperationId == "deleteCustomer" && e.IsDeprecated);
    }

    [Fact]
    public async Task A_deactivated_api_stops_resolving()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var result = await _service.ResolveAsync("api.company.com/swagger", Developer);
        await _service.SetActiveAsync(result.Definition!.Id, false, "admin");

        var second = await _service.ResolveAsync("api.company.com/swagger", Developer);

        Assert.Equal(ResolveStatus.Inactive, second.Status);
    }

    [Fact]
    public async Task Role_restrictions_hide_an_api_from_other_roles()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());

        var registration = await _service.RegisterAsync(new RegisterApiRequest
        {
            SwaggerUrl = "https://api.company.com/swagger",
            AllowedRoles = "Developer",
        });

        Assert.True(registration.Success, registration.Error);

        Assert.True((await _service.ResolveAsync("api.company.com/swagger", Developer)).IsSuccess);
        Assert.Equal(
            ResolveStatus.Forbidden,
            (await _service.ResolveAsync("api.company.com/swagger", Tester)).Status);
    }

    [Fact]
    public async Task Requiring_a_sign_in_hides_registered_dashboards_from_anonymous_visitors()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());
        await _service.ResolveAsync("api.company.com/swagger", Developer);

        // Anonymous browsing is fine on an internal network but leaks the endpoint list of
        // every registered API once the platform is reachable from the public internet.
        Assert.True((await _service.ResolveAsync("api.company.com/swagger", Anonymous)).IsSuccess);

        _settings.CurrentValue.Access.RequireAuthenticationToView = true;

        var denied = await _service.ResolveAsync("api.company.com/swagger", Anonymous);
        Assert.Equal(ResolveStatus.ProvisioningForbidden, denied.Status);
        Assert.Contains("giriş", denied.Error, StringComparison.OrdinalIgnoreCase);

        Assert.True((await _service.ResolveAsync("api.company.com/swagger", Tester)).IsSuccess);
    }

    [Fact]
    public async Task An_unreachable_document_reports_the_failure_instead_of_registering_an_empty_api()
    {
        var result = await _service.ResolveAsync("api.company.com/swagger", Developer);

        Assert.Equal(ResolveStatus.ProvisioningFailed, result.Status);
        Assert.Equal(0, await _db.ApiDefinitions.CountAsync());
    }

    [Fact]
    public async Task A_stored_dashboard_from_an_older_schema_version_is_rebuilt_on_read()
    {
        _documents.Serve("https://api.company.com/swagger/v1/swagger.json", SampleDocuments.CustomerApi());
        var registration = await _service.ResolveAsync("api.company.com/swagger", Developer);

        var definition = await _db.ApiDefinitions.SingleAsync();
        definition.DashboardSchemaVersion = 0;
        definition.DashboardJson = "{}";
        await _db.SaveChangesAsync();
        _cache.InvalidateApi(definition.Id);

        var reopened = await _service.ResolveAsync("api.company.com/swagger", Developer);

        Assert.True(reopened.IsSuccess, reopened.Error);
        Assert.Equal(registration.Dashboard!.Operations.Count, reopened.Dashboard!.Operations.Count);
        Assert.Equal(DashboardDocument.CurrentSchemaVersion, (await _db.ApiDefinitions.SingleAsync()).DashboardSchemaVersion);
    }

    [Fact]
    public void Derives_the_base_url_from_the_document_servers_entry()
    {
        var dashboard = new DashboardDocument { Servers = { new DashboardServer { Url = "https://api.company.com/v1" } } };

        var baseUrl = ApiDefinitionService.DeriveBaseUrl(
            new Uri("https://api.company.com/swagger/v1/swagger.json"), dashboard);

        Assert.Equal("https://api.company.com/v1", baseUrl);
    }

    [Fact]
    public void Resolves_a_relative_server_entry_against_the_document_url()
    {
        var dashboard = new DashboardDocument { Servers = { new DashboardServer { Url = "/api/v2" } } };

        var baseUrl = ApiDefinitionService.DeriveBaseUrl(
            new Uri("https://api.company.com/swagger/v1/swagger.json"), dashboard);

        Assert.Equal("https://api.company.com/api/v2", baseUrl);
    }

    [Fact]
    public void Skips_a_templated_server_entry_and_falls_back_to_the_document_origin()
    {
        var dashboard = new DashboardDocument { Servers = { new DashboardServer { Url = "https://{region}.company.com/v1" } } };

        var baseUrl = ApiDefinitionService.DeriveBaseUrl(
            new Uri("https://api.company.com/swagger/v1/swagger.json"), dashboard);

        Assert.Equal("https://api.company.com", baseUrl);
    }

    /// <summary>Stands in for the network, and counts how often it was asked.</summary>
    private sealed class RecordingDocumentService : IOpenApiDocumentService
    {
        private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);

        public int FetchCount { get; private set; }

        public void Serve(string documentUrl, string content) => _documents[documentUrl] = content;

        public Task<OpenApiFetchResult> FetchAsync(Uri swaggerUrl, CancellationToken cancellationToken = default)
        {
            FetchCount++;

            // Mirrors the real probing behaviour: the pasted URL first, then the well known
            // document locations under it.
            foreach (var candidate in OpenApiDocumentService.BuildCandidates(
                         swaggerUrl,
                         ["/swagger/v1/swagger.json", "/openapi.json"]))
            {
                if (_documents.TryGetValue(candidate.AbsoluteUri, out var content))
                {
                    return Task.FromResult(OpenApiFetchResult.Ok(candidate, content));
                }
            }

            return Task.FromResult(OpenApiFetchResult.Fail("Bu adreste OpenAPI dokümanı bulunamadı."));
        }
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public StaticOptionsMonitor(SwaggerDashboardOptions value) => CurrentValue = value;

        public SwaggerDashboardOptions CurrentValue { get; }

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}

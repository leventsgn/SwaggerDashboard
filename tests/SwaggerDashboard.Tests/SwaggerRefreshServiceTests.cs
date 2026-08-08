using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Hashing;
using SwaggerDashboard.Infrastructure.Caching;
using SwaggerDashboard.Infrastructure.Persistence;
using SwaggerDashboard.Infrastructure.Services;
using Xunit;

namespace SwaggerDashboard.Tests;

public class SwaggerRefreshServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SwaggerDashboardDbContext _db;
    private readonly MutableDocumentService _documents = new();
    private readonly MemoryDashboardCache _cache;
    private readonly ApiDefinitionService _definitions;
    private readonly SwaggerRefreshService _refresh;

    public SwaggerRefreshServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new SwaggerDashboardDbContext(
            new DbContextOptionsBuilder<SwaggerDashboardDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var settings = new StaticOptionsMonitor();
        _cache = new MemoryDashboardCache(new MemoryCache(new MemoryCacheOptions()), settings);

        var generator = new DashboardGeneratorService(NullLogger<DashboardGeneratorService>.Instance);
        var hash = new HashService();

        _definitions = new ApiDefinitionService(
            _db, _documents, generator, hash, _cache, settings,
            NullLogger<ApiDefinitionService>.Instance);

        _refresh = new SwaggerRefreshService(
            _db, _documents, generator, hash, _cache, _definitions,
            NullLogger<SwaggerRefreshService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<int> RegisterAsync(string content)
    {
        _documents.Content = content;

        var result = await _definitions.RegisterAsync(new RegisterApiRequest
        {
            SwaggerUrl = "https://api.example.com/swagger/v1/swagger.json",
        });

        Assert.True(result.Success, result.Error);
        return result.Definition!.Id;
    }

    [Fact]
    public async Task An_identical_document_does_not_rebuild_the_dashboard()
    {
        var id = await RegisterAsync(SampleDocuments.Minimal);
        var builtAt = (await _db.ApiDefinitions.AsNoTracking().SingleAsync()).LastDashboardBuildAt;

        // Same content, serialized differently: the canonical hash has to see through that.
        _documents.Content = SampleDocuments.MinimalReordered;
        var result = await _refresh.RefreshAsync(id, "admin");

        Assert.True(result.Success, result.Error);
        Assert.False(result.Changed);
        Assert.Equal(builtAt, (await _db.ApiDefinitions.AsNoTracking().SingleAsync()).LastDashboardBuildAt);
    }

    [Fact]
    public async Task A_changed_document_rebuilds_and_reports_the_added_endpoint()
    {
        var id = await RegisterAsync(SampleDocuments.Minimal);

        _documents.Content = SampleDocuments.MinimalPlusOperation;
        var result = await _refresh.RefreshAsync(id, "admin");

        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        Assert.Contains("POST /pong", result.Diff!.Added);
        Assert.Empty(result.Diff.Removed);

        var definition = await _db.ApiDefinitions.AsNoTracking().SingleAsync();
        Assert.Equal(2, definition.EndpointCount);
        Assert.Equal("1.1.0", definition.ApiVersion);
    }

    [Fact]
    public async Task A_refresh_that_removes_an_endpoint_deletes_its_row()
    {
        var id = await RegisterAsync(SampleDocuments.MinimalPlusOperation);

        _documents.Content = SampleDocuments.Minimal;
        var result = await _refresh.RefreshAsync(id, "admin");

        Assert.True(result.Changed);
        Assert.Contains("POST /pong", result.Diff!.Removed);
        Assert.Single(await _db.ApiEndpoints.ToListAsync());
    }

    [Fact]
    public async Task Refresh_records_the_check_time_even_when_the_document_is_unreachable()
    {
        var id = await RegisterAsync(SampleDocuments.Minimal);

        _documents.Content = null;
        var before = DateTimeOffset.UtcNow;
        var result = await _refresh.RefreshAsync(id, "admin");

        Assert.False(result.Success);
        var definition = await _db.ApiDefinitions.AsNoTracking().SingleAsync();
        Assert.True(definition.LastSwaggerCheckAt >= before);

        // The stored dashboard is left intact so the API keeps working.
        Assert.Equal(1, definition.EndpointCount);
    }

    [Fact]
    public async Task Rebuild_works_from_the_stored_document_without_contacting_the_target()
    {
        var id = await RegisterAsync(SampleDocuments.Minimal);
        var fetchesAfterRegister = _documents.FetchCount;

        _documents.Content = null;
        var result = await _refresh.RebuildAsync(id, "admin");

        Assert.True(result.Success, result.Error);
        Assert.Equal(fetchesAfterRegister, _documents.FetchCount);
    }

    [Fact]
    public async Task Refreshing_clears_the_cached_dashboard_so_the_new_one_is_served()
    {
        var id = await RegisterAsync(SampleDocuments.Minimal);
        Assert.Single((await _definitions.GetDashboardAsync(id))!.Operations);

        _documents.Content = SampleDocuments.MinimalPlusOperation;
        await _refresh.RefreshAsync(id, "admin");

        Assert.Equal(2, (await _definitions.GetDashboardAsync(id))!.Operations.Count);
    }

    [Fact]
    public void The_diff_notices_a_changed_parameter_list()
    {
        var previous = new DashboardDocument
        {
            Operations =
            {
                new DashboardOperation { Method = "GET", Path = "/a", Slug = "a" },
            },
        };

        var current = new DashboardDocument
        {
            Operations =
            {
                new DashboardOperation
                {
                    Method = "GET",
                    Path = "/a",
                    Slug = "a",
                    Parameters = { new DashboardParameter { Name = "page" } },
                },
            },
        };

        var diff = SwaggerRefreshService.BuildDiff(previous, current);

        Assert.Contains("GET /a", diff.Modified);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void The_diff_ignores_an_untouched_operation()
    {
        var operation = new DashboardOperation { Method = "GET", Path = "/a", Slug = "a", Summary = "x" };
        var previous = new DashboardDocument { Operations = { operation } };
        var current = new DashboardDocument
        {
            Operations = { new DashboardOperation { Method = "GET", Path = "/a", Slug = "a", Summary = "x" } },
        };

        Assert.False(SwaggerRefreshService.BuildDiff(previous, current).HasChanges);
    }

    /// <summary>Serves whatever content the test currently wants, or nothing at all.</summary>
    private sealed class MutableDocumentService : IOpenApiDocumentService
    {
        public string? Content { get; set; }

        public int FetchCount { get; private set; }

        public Task<OpenApiFetchResult> FetchAsync(Uri swaggerUrl, CancellationToken cancellationToken = default)
        {
            FetchCount++;

            return Task.FromResult(Content is null
                ? OpenApiFetchResult.Fail("Hedef adrese ulaşılamadı.")
                : OpenApiFetchResult.Ok(swaggerUrl, Content));
        }
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public SwaggerDashboardOptions CurrentValue { get; } = new();

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}

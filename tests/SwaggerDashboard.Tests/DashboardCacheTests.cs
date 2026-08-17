using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Infrastructure.Caching;
using Xunit;

namespace SwaggerDashboard.Tests;

public class DashboardCacheTests
{
    private static MemoryDashboardCache Cache() =>
        new(new MemoryCache(new MemoryCacheOptions()),
            new StaticOptionsMonitor(new SwaggerDashboardOptions()));

    private static DashboardDocument Document(string title) => new() { Title = title };

    [Fact]
    public void A_stored_dashboard_comes_back_under_its_hash()
    {
        var cache = Cache();
        cache.SetDashboard(1, "hash-a", Document("Customer API"));

        Assert.Equal("Customer API", cache.GetDashboard(1, "hash-a")?.Title);

        // A different hash is a different document, which is what makes a swagger change
        // invalidate the cache on its own.
        Assert.Null(cache.GetDashboard(1, "hash-b"));
    }

    [Fact]
    public void Invalidating_one_api_leaves_the_others_alone()
    {
        var cache = Cache();
        cache.SetDashboard(1, "hash", Document("Bir"));
        cache.SetDashboard(2, "hash", Document("İki"));
        cache.SetRoute("api.company.com/swagger", 1);

        cache.InvalidateApi(1);

        Assert.Null(cache.GetDashboard(1, "hash"));
        Assert.False(cache.TryGetRoute("api.company.com/swagger", out _));
        Assert.NotNull(cache.GetDashboard(2, "hash"));
    }

    [Fact]
    public void Clearing_everything_reports_how_many_entries_went()
    {
        // The count is what the admin screen shows; "cleared" with no number cannot be told
        // apart from a button that did nothing.
        var cache = Cache();
        cache.SetDashboard(1, "hash", Document("Bir"));
        cache.SetDashboard(2, "hash", Document("İki"));
        cache.SetRoute("api.company.com/swagger", 1);

        Assert.Equal(3, cache.InvalidateAll());

        Assert.Null(cache.GetDashboard(1, "hash"));
        Assert.Null(cache.GetDashboard(2, "hash"));
        Assert.False(cache.TryGetRoute("api.company.com/swagger", out _));
    }

    [Fact]
    public void Clearing_an_empty_cache_reports_nothing_rather_than_failing()
    {
        Assert.Equal(0, Cache().InvalidateAll());
    }

    [Fact]
    public void The_cache_is_usable_again_after_being_cleared()
    {
        var cache = Cache();
        cache.SetDashboard(1, "hash", Document("Bir"));
        cache.InvalidateAll();

        cache.SetDashboard(1, "hash", Document("Yeniden"));

        Assert.Equal("Yeniden", cache.GetDashboard(1, "hash")?.Title);
        Assert.Equal(1, cache.InvalidateAll());
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public StaticOptionsMonitor(SwaggerDashboardOptions value) => CurrentValue = value;

        public SwaggerDashboardOptions CurrentValue { get; }

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}

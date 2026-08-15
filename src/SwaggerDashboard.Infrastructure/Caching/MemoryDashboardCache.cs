using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Infrastructure.Caching;

/// <summary>
/// IMemoryCache backed implementation of the dashboard cache.
/// </summary>
/// <remarks>
/// IMemoryCache cannot enumerate its keys, so the keys belonging to each API are tracked
/// separately; that bookkeeping is what makes targeted invalidation on refresh possible.
/// </remarks>
public class MemoryDashboardCache : IDashboardCache
{
    private const string DashboardPrefix = "swagger-dashboard";
    private const string RoutePrefix = "swagger-dashboard-route";

    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _keysByApi = new();
    private readonly ConcurrentDictionary<string, int> _apiByRouteKey = new(StringComparer.OrdinalIgnoreCase);

    public MemoryDashboardCache(IMemoryCache cache, IOptionsMonitor<SwaggerDashboardOptions> options)
    {
        _cache = cache;
        _options = options;
    }

    public DashboardDocument? GetDashboard(int apiDefinitionId, string swaggerHash) =>
        _cache.TryGetValue(DashboardKey(apiDefinitionId, swaggerHash), out DashboardDocument? document)
            ? document
            : null;

    public void SetDashboard(int apiDefinitionId, string swaggerHash, DashboardDocument document)
    {
        var key = DashboardKey(apiDefinitionId, swaggerHash);

        _cache.Set(key, document, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow =
                TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.Cache.DashboardMinutes)),
        });

        Track(apiDefinitionId, key);
    }

    public bool TryGetRoute(string routeKey, out int apiDefinitionId) =>
        _cache.TryGetValue(RouteKey(routeKey), out apiDefinitionId);

    public void SetRoute(string routeKey, int apiDefinitionId)
    {
        var key = RouteKey(routeKey);

        _cache.Set(key, apiDefinitionId, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow =
                TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.Cache.RouteResolutionMinutes)),
        });

        Track(apiDefinitionId, key);
        _apiByRouteKey[key] = apiDefinitionId;
    }

    public void InvalidateApi(int apiDefinitionId)
    {
        if (_keysByApi.TryRemove(apiDefinitionId, out var keys))
        {
            foreach (var key in keys.Keys)
            {
                _cache.Remove(key);
                _apiByRouteKey.TryRemove(key, out _);
            }
        }
    }

    public int InvalidateAll()
    {
        var cleared = 0;

        foreach (var apiDefinitionId in _keysByApi.Keys)
        {
            if (!_keysByApi.TryRemove(apiDefinitionId, out var keys))
            {
                continue;
            }

            foreach (var key in keys.Keys)
            {
                _cache.Remove(key);
                _apiByRouteKey.TryRemove(key, out _);
                cleared++;
            }
        }

        return cleared;
    }

    private void Track(int apiDefinitionId, string key)
    {
        var keys = _keysByApi.GetOrAdd(apiDefinitionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys[key] = 0;
    }

    private static string DashboardKey(int apiDefinitionId, string swaggerHash) =>
        $"{DashboardPrefix}:{apiDefinitionId}:{swaggerHash}";

    private static string RouteKey(string routeKey) => $"{RoutePrefix}:{routeKey}";
}

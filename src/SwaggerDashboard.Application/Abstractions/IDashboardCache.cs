using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Cache in front of the stored dashboards. Backed by IMemoryCache today; the interface
/// exists so a distributed cache can replace it without touching callers.
/// </summary>
public interface IDashboardCache
{
    /// <summary>Cache key format: <c>swagger-dashboard:{apiDefinitionId}:{swaggerHash}</c>.</summary>
    DashboardDocument? GetDashboard(int apiDefinitionId, string swaggerHash);

    void SetDashboard(int apiDefinitionId, string swaggerHash, DashboardDocument document);

    /// <summary>Resolved route tail or alias to API definition id.</summary>
    bool TryGetRoute(string routeKey, out int apiDefinitionId);

    void SetRoute(string routeKey, int apiDefinitionId);

    /// <summary>Drops every entry belonging to an API, including its route mappings.</summary>
    void InvalidateApi(int apiDefinitionId);

    /// <summary>
    /// Drops every cached dashboard and route mapping, and reports how many entries went.
    /// </summary>
    /// <remarks>
    /// The count is returned so the screen offering this can say what happened. "Cache
    /// cleared" with no number is indistinguishable from a button that does nothing.
    /// </remarks>
    int InvalidateAll();
}

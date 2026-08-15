using SwaggerDashboard.Domain.Entities;

namespace SwaggerDashboard.Application.Abstractions;

public interface IRequestLogService
{
    Task RecordAsync(
        int apiDefinitionId,
        int? apiEndpointId,
        ProxyResponse response,
        string? userId,
        string? clientIp,
        bool isBulkRun = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiRequestLog>> GetRecentAsync(
        int? apiDefinitionId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The calls one user made to one endpoint, newest first, for the history list on the
    /// endpoint screen.
    /// </summary>
    /// <remarks>
    /// Bulk runs are left out. A single sweep calls every endpoint once, so including them
    /// would bury the calls the user actually made behind rows they never composed.
    /// </remarks>
    Task<IReadOnlyList<ApiRequestLog>> GetForEndpointAsync(
        int apiDefinitionId,
        string endpointSlug,
        string userId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes rows older than the configured retention window.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

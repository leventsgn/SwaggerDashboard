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
    /// One page of the log, plus how many rows match in total.
    /// </summary>
    /// <remarks>
    /// The admin screen used to ask for the newest 200 and show them with nothing to say a
    /// row 201 existed, so a busy day looked like a quiet one. The total is what lets the
    /// screen admit there is more and offer to page through it.
    /// </remarks>
    Task<LogPage> GetPageAsync(
        int? apiDefinitionId,
        int skip,
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

/// <summary>One page of log rows and the size of the set it came from.</summary>
public record LogPage(IReadOnlyList<ApiRequestLog> Rows, int TotalCount, int Skip)
{
    public bool HasMore => Skip + Rows.Count < TotalCount;
}

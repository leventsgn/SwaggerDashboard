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

    /// <summary>Deletes rows older than the configured retention window.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

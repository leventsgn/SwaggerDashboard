using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

public class RequestLogService : IRequestLogService
{
    private readonly SwaggerDashboardDbContext _db;
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;
    private readonly ILogger<RequestLogService> _logger;

    public RequestLogService(
        SwaggerDashboardDbContext db,
        IOptionsMonitor<SwaggerDashboardOptions> options,
        ILogger<RequestLogService> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public async Task RecordAsync(
        int apiDefinitionId,
        int? apiEndpointId,
        ProxyResponse response,
        string? userId,
        string? clientIp,
        bool isBulkRun = false,
        CancellationToken cancellationToken = default)
    {
        var logging = _options.CurrentValue.Logging;
        var masker = new SensitiveDataMasker(logging.MaskedHeaders);

        // Bodies routinely carry personal data, so they are only stored when explicitly
        // enabled; headers are always recorded but masked.
        string? requestBody = null;
        string? responseBody = null;
        var requestTruncated = false;
        var responseTruncated = false;

        if (logging.PersistBodies)
        {
            requestBody = Clip(response.RequestBody, logging.MaxLoggedBodyChars, out requestTruncated);
            responseBody = Clip(response.ResponseBody, logging.MaxLoggedBodyChars, out responseTruncated);
        }

        var entry = new ApiRequestLog
        {
            ApiDefinitionId = apiDefinitionId,
            ApiEndpointId = apiEndpointId,
            UserId = userId,
            RequestUrl = Truncate(response.RequestUrl, 2000),
            HttpMethod = response.RequestMethod,
            RequestHeadersJson = JsonSerializer.Serialize(masker.MaskHeaders(response.RequestHeaders)),
            RequestBody = requestBody,
            RequestBodyTruncated = requestTruncated,
            ResponseStatusCode = response.StatusCode,
            ResponseHeadersJson = JsonSerializer.Serialize(masker.MaskHeaders(response.ResponseHeaders)),
            ResponseBody = responseBody,
            ResponseBodyTruncated = responseTruncated,
            DurationMilliseconds = response.DurationMilliseconds,
            ClientIp = Truncate(clientIp, 64),
            CreatedAt = response.StartedAt,
            IsSuccess = response.Success,
            ErrorMessage = Truncate(response.Error, 2000),
            IsBulkRun = isBulkRun,
        };

        _db.ApiRequestLogs.Add(entry);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A failed audit write must not turn a successful API call into an error for
            // the user; it is reported to the application log instead.
            _logger.LogError(ex, "Request log could not be written for API {ApiDefinitionId}", apiDefinitionId);
            _db.ApiRequestLogs.Remove(entry);
        }
    }

    public async Task<IReadOnlyList<ApiRequestLog>> GetRecentAsync(
        int? apiDefinitionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = _db.ApiRequestLogs.AsNoTracking();

        if (apiDefinitionId is not null)
        {
            query = query.Where(l => l.ApiDefinitionId == apiDefinitionId);
        }

        return await query
            .OrderByDescending(l => l.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var retentionDays = _options.CurrentValue.Logging.RetentionDays;
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        return await _db.ApiRequestLogs
            .Where(l => l.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static string? Clip(string? value, int maxChars, out bool truncated)
    {
        truncated = false;

        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        truncated = true;
        return value[..maxChars];
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

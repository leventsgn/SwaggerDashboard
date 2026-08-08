namespace SwaggerDashboard.Domain.Entities;

/// <summary>
/// One proxied call. Bodies are truncated and sensitive headers masked before persisting.
/// </summary>
public class ApiRequestLog
{
    public long Id { get; set; }

    public int ApiDefinitionId { get; set; }

    public int? ApiEndpointId { get; set; }

    public string? UserId { get; set; }

    public string RequestUrl { get; set; } = string.Empty;

    public string HttpMethod { get; set; } = string.Empty;

    public string? RequestHeadersJson { get; set; }

    public string? RequestBody { get; set; }

    public bool RequestBodyTruncated { get; set; }

    public int ResponseStatusCode { get; set; }

    public string? ResponseHeadersJson { get; set; }

    public string? ResponseBody { get; set; }

    public bool ResponseBodyTruncated { get; set; }

    public long DurationMilliseconds { get; set; }

    public string? ClientIp { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }
}

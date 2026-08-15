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

    /// <summary>
    /// Whether the call came from a bulk run rather than someone opening the endpoint.
    /// </summary>
    /// <remarks>
    /// Recorded so the "recently used" shortcuts can ignore it. One sweep touches every
    /// endpoint of the API, and without this the list would say the user recently used all of
    /// them, which is exactly the information the shortcut exists to filter out.
    /// </remarks>
    public bool IsBulkRun { get; set; }
}

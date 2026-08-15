namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Sends an endpoint call on behalf of the browser.
/// </summary>
/// <remarks>
/// The browser never names a URL. It names an API definition and an operation slug, and the
/// target address is rebuilt server side from the stored base URL plus the operation's path
/// template. Anything else would turn the proxy into an open forwarder.
/// </remarks>
public interface IApiProxyService
{
    Task<ProxyResponse> ExecuteAsync(ProxyRequest request, CancellationToken cancellationToken = default);
}

public record ProxyRequest
{
    public required int ApiDefinitionId { get; init; }

    public required string OperationSlug { get; init; }

    /// <summary>Optional environment name selecting an alternative base URL.</summary>
    public string? EnvironmentName { get; init; }

    public Dictionary<string, string> PathParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Query values; a key may repeat for array parameters.</summary>
    public List<KeyValuePair<string, string>> QueryParameters { get; init; } = [];

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Cookies { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Body { get; init; }

    public string? ContentType { get; init; }

    /// <summary>Form fields when the operation uses form or multipart encoding.</summary>
    public List<KeyValuePair<string, string>> FormFields { get; init; } = [];

    public List<ProxyFile> Files { get; init; } = [];

    public string? UserId { get; init; }

    public string? ClientIp { get; init; }
}

public record ProxyFile(string FieldName, string FileName, string ContentType, byte[] Content);

public record ProxyResponse
{
    public bool Success { get; init; }

    public int StatusCode { get; init; }

    public string? ReasonPhrase { get; init; }

    public string RequestUrl { get; init; } = string.Empty;

    public string RequestMethod { get; init; } = string.Empty;

    public Dictionary<string, string> RequestHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? RequestBody { get; init; }

    public Dictionary<string, string> ResponseHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ResponseBody { get; init; }

    /// <summary>Set when the response was not text and was withheld from the preview.</summary>
    public bool IsBinary { get; init; }

    /// <summary>One-shot token that fetches a binary response from the download endpoint.</summary>
    public string? DownloadToken { get; init; }

    /// <summary>Name the downloaded file is offered under.</summary>
    public string? FileName { get; init; }

    public bool Truncated { get; init; }

    public string? ContentType { get; init; }

    public long ContentLength { get; init; }

    public long DurationMilliseconds { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }

    public string? Error { get; init; }

    public static ProxyResponse Failure(string error, string requestUrl = "", string method = "") => new()
    {
        Success = false,
        Error = error,
        RequestUrl = requestUrl,
        RequestMethod = method,
        StatusCode = 0,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
    };
}

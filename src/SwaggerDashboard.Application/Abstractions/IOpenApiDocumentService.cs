namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Fetches the OpenAPI document behind a user supplied swagger URL.
/// </summary>
public interface IOpenApiDocumentService
{
    /// <summary>
    /// Resolves the pasted URL to the actual OpenAPI document and downloads it.
    /// </summary>
    /// <remarks>
    /// Users normally paste the swagger UI page rather than the JSON, so when the response
    /// is not a document the configured probe paths are tried. The returned
    /// <see cref="OpenApiFetchResult.DocumentUrl"/> is what the target key must be computed
    /// from, otherwise /swagger and /swagger/index.html would register as two APIs.
    /// </remarks>
    Task<OpenApiFetchResult> FetchAsync(Uri swaggerUrl, CancellationToken cancellationToken = default);
}

public record OpenApiFetchResult(
    bool Success,
    Uri? DocumentUrl,
    string? Content,
    string? Error)
{
    public static OpenApiFetchResult Ok(Uri documentUrl, string content) =>
        new(true, documentUrl, content, null);

    public static OpenApiFetchResult Fail(string error) => new(false, null, null, error);
}

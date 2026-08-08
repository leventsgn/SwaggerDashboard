namespace SwaggerDashboard.Domain.Entities;

/// <summary>
/// A dashboard URL that resolves to an API definition.
/// </summary>
/// <remarks>
/// One API is reachable through several addresses: the swagger UI page a user pastes
/// (<c>/swagger/index.html</c>), the shorter form (<c>/swagger</c>), and the OpenAPI
/// document itself (<c>/swagger/v1/swagger.json</c>). The document URL is what identifies
/// the API, so every other spelling is recorded here and points at the same definition
/// instead of registering a second one.
/// </remarks>
public class ApiUrlAlias
{
    public int Id { get; set; }

    public int ApiDefinitionId { get; set; }

    public ApiDefinition? ApiDefinition { get; set; }

    /// <summary>SHA-256 of the normalized URL; unique across all APIs.</summary>
    public string UrlKey { get; set; } = string.Empty;

    /// <summary>The normalized URL this key was computed from, kept for diagnostics.</summary>
    public string Url { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

namespace SwaggerDashboard.Domain.Entities;

/// <summary>
/// A registered API. Created either by an administrator through the management panel
/// or automatically on the first request to a prefixed swagger URL.
/// </summary>
public class ApiDefinition
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Optional short alias, e.g. "customer-api" resolving to /customer-api.
    /// Unique when present; the prefixed-URL form works without it.
    /// </summary>
    public string? RouteName { get; set; }

    /// <summary>
    /// SHA-256 of <see cref="SwaggerUrlNormalized"/>, the canonical identity of the API.
    /// Incoming URLs are matched through <see cref="Aliases"/>, which always contains this
    /// key plus every other spelling that resolved to the same document.
    /// </summary>
    public string TargetKey { get; set; } = string.Empty;

    /// <summary>Base address of the API the proxy sends requests to.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The URL the user originally pasted (may point at the swagger UI page).</summary>
    public string SwaggerUrl { get; set; } = string.Empty;

    /// <summary>Canonical absolute URL of the OpenAPI JSON document.</summary>
    public string SwaggerUrlNormalized { get; set; } = string.Empty;

    public string? SwaggerVersion { get; set; }

    public string? ApiVersion { get; set; }

    /// <summary>Pre-rendered dashboard model, serialized. Never rebuilt on read.</summary>
    public string DashboardJson { get; set; } = string.Empty;

    /// <summary>Schema version of <see cref="DashboardJson"/>; a mismatch forces a rebuild.</summary>
    public int DashboardSchemaVersion { get; set; }

    public string? RawSwaggerJson { get; set; }

    /// <summary>SHA-256 over the canonicalized OpenAPI document.</summary>
    public string SwaggerHash { get; set; } = string.Empty;

    public int EndpointCount { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>True when the record was created by first-visit auto provisioning.</summary>
    public bool IsAutoProvisioned { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastSwaggerCheckAt { get; set; }

    public DateTimeOffset? LastDashboardBuildAt { get; set; }

    public string? CreatedBy { get; set; }

    public string? UpdatedBy { get; set; }

    /// <summary>Comma separated role names allowed to view this API; empty means every authenticated role.</summary>
    public string? AllowedRoles { get; set; }

    public ICollection<ApiEndpoint> Endpoints { get; set; } = new List<ApiEndpoint>();

    public ICollection<ApiEnvironment> Environments { get; set; } = new List<ApiEnvironment>();

    /// <summary>Every dashboard URL that resolves to this API.</summary>
    public ICollection<ApiUrlAlias> Aliases { get; set; } = new List<ApiUrlAlias>();
}

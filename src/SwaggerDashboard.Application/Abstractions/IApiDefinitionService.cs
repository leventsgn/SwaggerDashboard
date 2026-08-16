using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Domain.Entities;

namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Resolves an incoming dashboard URL to a stored API definition, provisioning one on the
/// first visit when that is permitted.
/// </summary>
public interface IApiDefinitionService
{
    /// <summary>
    /// Resolves the tail of a prefixed URL, e.g. "api.company.com/swagger", or a short
    /// route alias such as "customer-api".
    /// </summary>
    /// <remarks>
    /// On a hit this reads the stored dashboard from cache or database and never contacts
    /// the target API; the swagger hash is not re-checked here.
    /// </remarks>
    Task<ResolveResult> ResolveAsync(string routeTail, ResolveContext context, CancellationToken cancellationToken = default);

    Task<ApiDefinition?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<DashboardDocument?> GetDashboardAsync(int apiDefinitionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiDefinition>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active APIs a caller may see, filtered by the same rules that gate a dashboard.
    /// </summary>
    Task<IReadOnlyList<ApiDefinition>> ListVisibleAsync(
        ResolveContext context, CancellationToken cancellationToken = default);

    Task<RegistrationResult> RegisterAsync(RegisterApiRequest request, CancellationToken cancellationToken = default);

    Task UpdateMetadataAsync(UpdateApiRequest request, CancellationToken cancellationToken = default);

    Task SetActiveAsync(int id, bool isActive, string? actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>Who is asking, so that provisioning and visibility rules can be applied.</summary>
public record ResolveContext(string? UserId, string? UserName, string? Role, bool IsAuthenticated);

public enum ResolveStatus
{
    Found,
    InvalidUrl,
    NotRegistered,
    ProvisioningForbidden,
    ProvisioningFailed,
    Inactive,
    Forbidden,
}

public record ResolveResult(
    ResolveStatus Status,
    ApiDefinition? Definition,
    DashboardDocument? Dashboard,
    string? Error,
    bool WasProvisioned = false)
{
    public bool IsSuccess => Status == ResolveStatus.Found;

    public static ResolveResult Found(ApiDefinition definition, DashboardDocument dashboard, bool provisioned = false) =>
        new(ResolveStatus.Found, definition, dashboard, null, provisioned);

    public static ResolveResult Failure(ResolveStatus status, string error) =>
        new(status, null, null, error);
}

public record RegisterApiRequest
{
    /// <summary>The swagger URL as pasted by the user; may point at the UI page.</summary>
    public required string SwaggerUrl { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Optional short alias. Must not collide with a reserved application path.</summary>
    public string? RouteName { get; init; }

    /// <summary>Overrides the base URL derived from the document's servers entry.</summary>
    public string? BaseUrl { get; init; }

    public string? AllowedRoles { get; init; }

    public string? Actor { get; init; }

    public bool IsAutoProvisioned { get; init; }
}

public record UpdateApiRequest
{
    public required int Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? RouteName { get; init; }

    public string? BaseUrl { get; init; }

    public string? AllowedRoles { get; init; }

    public string? Actor { get; init; }
}

public record RegistrationResult(bool Success, ApiDefinition? Definition, string? Error)
{
    public static RegistrationResult Ok(ApiDefinition definition) => new(true, definition, null);

    public static RegistrationResult Fail(string error) => new(false, null, error);
}

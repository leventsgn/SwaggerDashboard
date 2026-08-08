namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Holds the credentials used when calling a target API.
/// </summary>
/// <remarks>
/// These are the target API's secrets, not the dashboard user's password, and they are
/// deliberately never written to the database. The default implementation keeps them in
/// server memory keyed by user and API, with a sliding expiry, so they disappear when the
/// session goes idle and never reach the browser.
/// </remarks>
public interface IApiCredentialStore
{
    ApiCredential? Get(string userId, int apiDefinitionId);

    void Set(string userId, int apiDefinitionId, ApiCredential credential);

    void Clear(string userId, int apiDefinitionId);
}

public record ApiCredential
{
    public ApiAuthKind Kind { get; init; } = ApiAuthKind.None;

    /// <summary>Bearer token, basic password or API key value depending on <see cref="Kind"/>.</summary>
    public string? Secret { get; init; }

    public string? UserName { get; init; }

    /// <summary>Header or query parameter name for API key authentication.</summary>
    public string? ParameterName { get; init; }

    /// <summary>header or query.</summary>
    public string? ParameterIn { get; init; }
}

public enum ApiAuthKind
{
    None = 0,
    Bearer = 1,
    Basic = 2,
    ApiKey = 3,
}

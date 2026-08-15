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

    /// <summary>
    /// Bearer token, basic password, API key value, or OAuth2 client secret depending on
    /// <see cref="Kind"/>.
    /// </summary>
    public string? Secret { get; init; }

    /// <summary>Basic user name, or the OAuth2 client id.</summary>
    public string? UserName { get; init; }

    /// <summary>Header or query parameter name for API key authentication.</summary>
    public string? ParameterName { get; init; }

    /// <summary>header or query.</summary>
    public string? ParameterIn { get; init; }

    /// <summary>OAuth2 token endpoint for the client credentials flow.</summary>
    public string? TokenUrl { get; init; }

    /// <summary>Optional space separated scopes requested with the token.</summary>
    public string? Scope { get; init; }
}

public enum ApiAuthKind
{
    None = 0,
    Bearer = 1,
    Basic = 2,
    ApiKey = 3,

    /// <summary>
    /// OAuth2 client credentials: the platform fetches a token itself and sends it as a
    /// bearer.
    /// </summary>
    /// <remarks>
    /// The only OAuth2 flow offered. The others end at a browser redirect and a consent
    /// screen, which a server side test tool cannot complete on the user's behalf without
    /// becoming an identity client in its own right.
    /// </remarks>
    OAuth2ClientCredentials = 4,
}

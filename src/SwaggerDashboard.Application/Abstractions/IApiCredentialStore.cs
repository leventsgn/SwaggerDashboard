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

    /// <summary>
    /// Where a token is fetched from: the OAuth2 token endpoint, or the API's own login
    /// endpoint.
    /// </summary>
    public string? TokenUrl { get; init; }

    /// <summary>Optional space separated scopes requested with the token.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Field names for <see cref="ApiAuthKind.LoginEndpoint"/>, when the API does not use
    /// the usual ones.
    /// </summary>
    /// <remarks>
    /// A login endpoint is an ordinary endpoint of the API rather than a standard, so every
    /// API names these differently: kullaniciAdi/sifre, email/password, user/pass. The
    /// defaults cover the common spellings and these exist for the ones they miss.
    /// </remarks>
    public string? LoginUserField { get; init; }

    public string? LoginPasswordField { get; init; }

    /// <summary>
    /// Which field of the login response holds the token, when it cannot be recognised.
    /// Dotted paths are allowed, e.g. <c>data.accessToken</c>.
    /// </summary>
    public string? TokenField { get; init; }
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

    /// <summary>
    /// The API's own login endpoint: the platform posts a user name and password to it,
    /// reads the token out of the answer and sends it as a bearer.
    /// </summary>
    /// <remarks>
    /// Most internal APIs authenticate this way rather than through an identity server, and
    /// without this the user had to sign in by hand somewhere else, copy the token and paste
    /// it as a bearer — which expires mid-session and made a bulk run of a protected API
    /// effectively impossible.
    /// </remarks>
    LoginEndpoint = 5,
}

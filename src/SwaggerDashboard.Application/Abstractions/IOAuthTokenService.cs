namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Obtains an access token for the OAuth2 client credentials flow.
/// </summary>
/// <remarks>
/// Server side on purpose. The client secret is the target API's credential; sending it to
/// the browser to let JavaScript fetch a token would put it somewhere the platform cannot
/// protect, and the token endpoint is a user supplied URL that has to go through the same
/// outbound policy as any other target.
/// </remarks>
public interface IOAuthTokenService
{
    Task<OAuthTokenResult> GetTokenAsync(
        string cacheKey, ApiCredential credential, CancellationToken cancellationToken = default);
}

public record OAuthTokenResult(string? AccessToken, string? Error)
{
    public bool Success => AccessToken is not null;

    public static OAuthTokenResult Ok(string accessToken) => new(accessToken, null);

    public static OAuthTokenResult Fail(string error) => new(null, error);
}

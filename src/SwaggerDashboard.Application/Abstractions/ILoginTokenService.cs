namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Signs in to the target API's own login endpoint and returns the token it answers with.
/// </summary>
/// <remarks>
/// Separate from <see cref="IOAuthTokenService"/> because the two are not the same problem.
/// A client credentials grant is a standard: the request shape and the response field names
/// are fixed, and a server either implements it or does not. A login endpoint is just an
/// ordinary endpoint of the API, so both the request and the response are whatever its
/// authors chose, and the work is guessing well and letting the user correct the guess.
/// </remarks>
public interface ILoginTokenService
{
    Task<OAuthTokenResult> GetTokenAsync(
        string cacheKey, ApiCredential credential, CancellationToken cancellationToken = default);
}

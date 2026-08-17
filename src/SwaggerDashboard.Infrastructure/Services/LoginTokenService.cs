using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Execution;
using SwaggerDashboard.Infrastructure.Http;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Signs in to the target API's own login endpoint through the guarded outbound pipeline and
/// caches the token until shortly before it expires.
/// </summary>
public class LoginTokenService : ILoginTokenService
{
    /// <summary>
    /// A token is dropped this long before it actually expires, so a call that starts just
    /// under the wire does not arrive with a token that died in flight.
    /// </summary>
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(30);

    /// <summary>A login response is small; anything larger is not one.</summary>
    private const int MaxResponseBytes = 64 * 1024;

    /// <summary>Field names to send when the user did not name them.</summary>
    private const string DefaultUserField = "username";
    private const string DefaultPasswordField = "password";

    private readonly GuardedHttpSender _sender;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LoginTokenService> _logger;

    public LoginTokenService(GuardedHttpSender sender, IMemoryCache cache, ILogger<LoginTokenService> logger)
    {
        _sender = sender;
        _cache = cache;
        _logger = logger;
    }

    public async Task<OAuthTokenResult> GetTokenAsync(
        string cacheKey,
        ApiCredential credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.TokenUrl) ||
            !Uri.TryCreate(credential.TokenUrl, UriKind.Absolute, out var loginUri))
        {
            return OAuthTokenResult.Fail("Login adresi girilmedi veya geçerli bir adres değil.");
        }

        if (string.IsNullOrWhiteSpace(credential.UserName))
        {
            return OAuthTokenResult.Fail("Kullanıcı adı girilmedi.");
        }

        // The password takes part in the key as a hash rather than itself. Without it, fixing
        // a mistyped password keeps handing back the token fetched with the old one, and the
        // user is told the corrected credentials work while nothing was retried.
        var key = "login-token:" + string.Join(
            ':',
            cacheKey,
            loginUri,
            credential.UserName,
            credential.LoginUserField,
            credential.LoginPasswordField,
            credential.TokenField,
            Fingerprint(credential.Secret));

        if (_cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            return OAuthTokenResult.Ok(cached);
        }

        var response = await _sender.SendAsync(
            loginUri,
            uri => BuildRequest(uri, credential),
            MaxResponseBytes,
            cancellationToken);

        if (!response.IsCompleted)
        {
            return OAuthTokenResult.Fail($"Login isteği tamamlanamadı: {response.Error}");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            _logger.LogWarning("Login endpoint {Uri} answered {StatusCode}", loginUri, response.StatusCode);

            return OAuthTokenResult.Fail(
                $"Login adresi {response.StatusCode} döndü. Kullanıcı adı ve parolayı kontrol edin. {Clip(response.Content)}"
                    .TrimEnd());
        }

        if (!TokenResponseReader.TryRead(response.Content, credential.TokenField, out var token, out var lifetime))
        {
            // The body is the only thing that can tell the user which field to name, and a
            // successful login response is not itself a secret the way a password is. It is
            // clipped, and the token it may contain is the very thing being handed to the
            // caller anyway.
            return OAuthTokenResult.Fail(
                "Login yanıtında token bulunamadı. Yanıttaki alan adını 'Token alanı' " +
                $"kutusuna yazın (örn. data.accessToken). Gelen yanıt: {Clip(response.Content)}");
        }

        var ttl = lifetime - ExpirySafetyMargin;

        if (ttl > TimeSpan.Zero)
        {
            _cache.Set(key, token, ttl);
        }

        return OAuthTokenResult.Ok(token!);
    }

    /// <summary>
    /// Builds the sign-in request as JSON.
    /// </summary>
    /// <remarks>
    /// JSON rather than form encoding: a login endpoint is an ordinary endpoint of a JSON API
    /// and is almost always declared with an application/json body. The field names are
    /// configurable because every API names them differently.
    /// </remarks>
    private static HttpRequestMessage BuildRequest(Uri uri, ApiCredential credential)
    {
        var userField = Field(credential.LoginUserField, DefaultUserField);
        var passwordField = Field(credential.LoginPasswordField, DefaultPasswordField);

        var payload = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [userField] = credential.UserName,
            [passwordField] = credential.Secret,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    private static string Field(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    /// <summary>
    /// Short, non-reversible stand-in for the password, so it can take part in a cache key
    /// without the password itself living in one.
    /// </summary>
    private static string Fingerprint(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "bos";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash)[..16];
    }

    private static string Clip(string? body) =>
        string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : body.Length > 300 ? string.Concat(body.AsSpan(0, 300), "…") : body;
}

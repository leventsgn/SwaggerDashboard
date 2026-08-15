using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Infrastructure.Http;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Fetches client credentials tokens through the guarded outbound pipeline and caches them
/// until shortly before they expire.
/// </summary>
public class OAuthTokenService : IOAuthTokenService
{
    /// <summary>
    /// A token is dropped this long before it actually expires, so a call that starts just
    /// under the wire does not arrive with a token that died in flight.
    /// </summary>
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(30);

    /// <summary>Used when the server does not say how long the token lasts.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    /// <summary>A token response is small; anything larger is not one.</summary>
    private const int MaxResponseBytes = 64 * 1024;

    private readonly GuardedHttpSender _sender;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OAuthTokenService> _logger;

    public OAuthTokenService(GuardedHttpSender sender, IMemoryCache cache, ILogger<OAuthTokenService> logger)
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
            !Uri.TryCreate(credential.TokenUrl, UriKind.Absolute, out var tokenUri))
        {
            return OAuthTokenResult.Fail("Token adresi (token URL) girilmedi veya geçerli bir adres değil.");
        }

        if (string.IsNullOrWhiteSpace(credential.UserName))
        {
            return OAuthTokenResult.Fail("Client ID girilmedi.");
        }

        // The secret takes part in the key, as a hash rather than itself. Without it, editing
        // the client secret keeps handing back the token fetched with the old one: a user
        // testing corrected credentials would be told they work while nothing was retried.
        var key = "oauth-token:" + string.Join(
            ':',
            cacheKey,
            tokenUri,
            credential.UserName,
            credential.Scope,
            Fingerprint(credential.Secret));

        if (_cache.TryGetValue(key, out string? cached) && cached is not null)
        {
            return OAuthTokenResult.Ok(cached);
        }

        var response = await _sender.SendAsync(
            tokenUri,
            uri => BuildRequest(uri, credential),
            MaxResponseBytes,
            cancellationToken);

        if (!response.IsCompleted)
        {
            return OAuthTokenResult.Fail($"Token alınamadı: {response.Error}");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            // The body of a failed token request routinely contains the reason (invalid_client,
            // invalid_scope) and nothing secret, so it is worth passing on.
            _logger.LogWarning(
                "Token endpoint {Uri} answered {StatusCode}", tokenUri, response.StatusCode);

            return OAuthTokenResult.Fail(
                $"Token adresi {response.StatusCode} döndü. {Clip(response.Content)}".TrimEnd());
        }

        if (!TryReadToken(response.Content, out var accessToken, out var lifetime))
        {
            return OAuthTokenResult.Fail("Token yanıtı okunamadı: access_token alanı bulunamadı.");
        }

        var ttl = lifetime - ExpirySafetyMargin;

        if (ttl > TimeSpan.Zero)
        {
            _cache.Set(key, accessToken, ttl);
        }

        return OAuthTokenResult.Ok(accessToken!);
    }

    private static HttpRequestMessage BuildRequest(Uri uri, ApiCredential credential)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
        };

        if (!string.IsNullOrWhiteSpace(credential.Scope))
        {
            form.Add(new("scope", credential.Scope));
        }

        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(form),
        };

        // RFC 6749 §2.3.1: the client id and secret go in the Authorization header, and a
        // server that supports the flow at all must accept them there. Putting them in the
        // body as well would send the secret twice for no gain.
        var raw = $"{credential.UserName}:{credential.Secret}";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    private static bool TryReadToken(string? body, out string? accessToken, out TimeSpan lifetime)
    {
        accessToken = null;
        lifetime = DefaultLifetime;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("access_token", out var token) ||
                token.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            accessToken = token.GetString();

            if (document.RootElement.TryGetProperty("expires_in", out var expires) &&
                expires.TryGetInt32(out var seconds) &&
                seconds > 0)
            {
                lifetime = TimeSpan.FromSeconds(seconds);
            }

            return !string.IsNullOrEmpty(accessToken);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Short, non-reversible stand-in for the secret, so it can take part in a cache key
    /// without the secret itself living in one.
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

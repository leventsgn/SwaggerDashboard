using Microsoft.Extensions.Caching.Memory;
using SwaggerDashboard.Application.Abstractions;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Keeps target API credentials in server memory only.
/// </summary>
/// <remarks>
/// Nothing here is written to the database and nothing is sent to the browser, so a token
/// entered in the auth panel cannot leak through a database dump or a client side script.
/// The sliding expiry means an idle session forgets its tokens on its own.
/// </remarks>
public class MemoryApiCredentialStore : IApiCredentialStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    private readonly IMemoryCache _cache;

    public MemoryApiCredentialStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public ApiCredential? Get(string userId, int apiDefinitionId) =>
        _cache.TryGetValue(Key(userId, apiDefinitionId), out ApiCredential? credential) ? credential : null;

    public void Set(string userId, int apiDefinitionId, ApiCredential credential) =>
        _cache.Set(Key(userId, apiDefinitionId), credential, new MemoryCacheEntryOptions
        {
            SlidingExpiration = Lifetime,
        });

    public void Clear(string userId, int apiDefinitionId) =>
        _cache.Remove(Key(userId, apiDefinitionId));

    private static string Key(string userId, int apiDefinitionId) =>
        $"swagger-dashboard-credential:{userId}:{apiDefinitionId}";
}

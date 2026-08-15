using Microsoft.Extensions.Caching.Memory;
using SwaggerDashboard.Application.Abstractions;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Keeps pending downloads in server memory for a few minutes.
/// </summary>
/// <remarks>
/// Deliberately not persisted: a proxied response may contain anything the target API
/// returned, and it only needs to survive the seconds between the call finishing and the
/// browser starting the download.
/// </remarks>
public class MemoryResponseDownloadStore : IResponseDownloadStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;

    public MemoryResponseDownloadStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public string Store(ResponseDownload download)
    {
        var token = Guid.NewGuid().ToString("N");

        _cache.Set(Key(token), download, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,
            Size = download.Content.Length,
        });

        return token;
    }

    public ResponseDownload? Take(string token, string? userId)
    {
        if (string.IsNullOrWhiteSpace(token) || !_cache.TryGetValue(Key(token), out ResponseDownload? download))
        {
            return null;
        }

        // A token is only valid for the session that produced it, so a leaked link cannot
        // be replayed by someone else.
        if (download is null || !string.Equals(download.UserId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        _cache.Remove(Key(token));
        return download;
    }

    private static string Key(string token) => "swagger-dashboard-download:" + token;
}

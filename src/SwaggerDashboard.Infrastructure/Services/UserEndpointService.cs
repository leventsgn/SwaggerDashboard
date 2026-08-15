using Microsoft.EntityFrameworkCore;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

public class UserEndpointService : IUserEndpointService
{
    /// <summary>
    /// How many log rows are scanned to build the recents list. Bounded so the query stays
    /// cheap on an API that is called constantly.
    /// </summary>
    private const int RecentScanWindow = 200;

    private readonly SwaggerDashboardDbContext _db;

    public UserEndpointService(SwaggerDashboardDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyCollection<string>> GetFavoriteSlugsAsync(
        int apiDefinitionId,
        string userId,
        CancellationToken cancellationToken = default) =>
        await _db.FavoriteEndpoints
            .AsNoTracking()
            .Where(f => f.ApiDefinitionId == apiDefinitionId && f.UserId == userId)
            .Select(f => f.EndpointSlug)
            .ToListAsync(cancellationToken);

    public async Task<bool> ToggleFavoriteAsync(
        int apiDefinitionId,
        string endpointSlug,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.FavoriteEndpoints.FirstOrDefaultAsync(
            f => f.ApiDefinitionId == apiDefinitionId &&
                 f.EndpointSlug == endpointSlug &&
                 f.UserId == userId,
            cancellationToken);

        if (existing is not null)
        {
            _db.FavoriteEndpoints.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
            return false;
        }

        _db.FavoriteEndpoints.Add(new FavoriteEndpoint
        {
            ApiDefinitionId = apiDefinitionId,
            EndpointSlug = endpointSlug,
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<string>> GetRecentSlugsAsync(
        int apiDefinitionId,
        string userId,
        int take,
        CancellationToken cancellationToken = default)
    {
        // Ordering has to survive the de-duplication, so the rows come back newest first and
        // the first occurrence of each endpoint wins.
        var recentEndpointIds = await _db.ApiRequestLogs
            .AsNoTracking()
            .Where(l => l.ApiDefinitionId == apiDefinitionId &&
                        l.UserId == userId &&
                        l.ApiEndpointId != null)
            .OrderByDescending(l => l.Id)
            .Select(l => l.ApiEndpointId!.Value)
            .Take(RecentScanWindow)
            .ToListAsync(cancellationToken);

        var ordered = recentEndpointIds.Distinct().Take(Math.Max(1, take)).ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var slugs = await _db.ApiEndpoints
            .AsNoTracking()
            .Where(e => ordered.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Slug, cancellationToken);

        return ordered
            .Where(slugs.ContainsKey)
            .Select(id => slugs[id])
            .ToList();
    }
}

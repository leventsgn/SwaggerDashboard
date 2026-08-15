using Microsoft.EntityFrameworkCore;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

public class SavedRequestService : ISavedRequestService
{
    private const int MaxNameLength = 200;

    /// <summary>
    /// A stored request is a convenience, not an archive. The cap keeps one endpoint's history
    /// from filling the table, and the number is high enough that a normal user never meets it.
    /// </summary>
    private const int MaxPerEndpoint = 50;

    /// <summary>
    /// Bodies are user supplied and can be megabytes. Storing one is not worth an unbounded
    /// row, and the message says so rather than truncating the request silently.
    /// </summary>
    private const int MaxPayloadChars = 128 * 1024;

    private readonly SwaggerDashboardDbContext _db;

    public SavedRequestService(SwaggerDashboardDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SavedRequest>> ListAsync(
        int apiDefinitionId,
        string endpointSlug,
        string userId,
        CancellationToken cancellationToken = default) =>
        await _db.SavedRequests
            .AsNoTracking()
            .Where(r => r.ApiDefinitionId == apiDefinitionId &&
                        r.EndpointSlug == endpointSlug &&
                        r.UserId == userId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public async Task<SavedRequest?> GetAsync(
        int id,
        string userId,
        CancellationToken cancellationToken = default) =>
        await _db.SavedRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);

    public async Task<SavedRequestResult> SaveAsync(
        int apiDefinitionId,
        string endpointSlug,
        string userId,
        string name,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            return SavedRequestResult.Fail("İstek adı boş olamaz.");
        }

        if (name.Length > MaxNameLength)
        {
            return SavedRequestResult.Fail($"İstek adı en fazla {MaxNameLength} karakter olabilir.");
        }

        if (payloadJson.Length > MaxPayloadChars)
        {
            return SavedRequestResult.Fail(
                $"İstek gövdesi kaydedilemeyecek kadar büyük (en fazla {MaxPayloadChars / 1024} KB).");
        }

        var existing = await _db.SavedRequests.FirstOrDefaultAsync(
            r => r.ApiDefinitionId == apiDefinitionId &&
                 r.EndpointSlug == endpointSlug &&
                 r.UserId == userId &&
                 r.Name == name,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;

        if (existing is not null)
        {
            existing.PayloadJson = payloadJson;
            existing.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return SavedRequestResult.Ok();
        }

        var count = await _db.SavedRequests.CountAsync(
            r => r.ApiDefinitionId == apiDefinitionId &&
                 r.EndpointSlug == endpointSlug &&
                 r.UserId == userId,
            cancellationToken);

        if (count >= MaxPerEndpoint)
        {
            return SavedRequestResult.Fail(
                $"Bu endpoint için en fazla {MaxPerEndpoint} istek kaydedilebilir; önce birini silin.");
        }

        _db.SavedRequests.Add(new SavedRequest
        {
            ApiDefinitionId = apiDefinitionId,
            EndpointSlug = endpointSlug,
            UserId = userId,
            Name = name,
            PayloadJson = payloadJson,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);
        return SavedRequestResult.Ok();
    }

    public async Task<bool> DeleteAsync(int id, string userId, CancellationToken cancellationToken = default)
    {
        var saved = await _db.SavedRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);

        if (saved is null)
        {
            return false;
        }

        _db.SavedRequests.Remove(saved);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

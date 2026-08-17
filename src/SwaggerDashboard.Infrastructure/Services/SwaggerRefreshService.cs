using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Hashing;
using SwaggerDashboard.Application.Routing;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Implements the "Swagger Güncelle" and "Dashboard Yeniden Oluştur" actions.
/// </summary>
public class SwaggerRefreshService : ISwaggerRefreshService
{
    private readonly SwaggerDashboardDbContext _db;
    private readonly IOpenApiDocumentService _documentService;
    private readonly IDashboardGeneratorService _generator;
    private readonly IHashService _hashService;
    private readonly IDashboardCache _cache;
    private readonly ApiDefinitionService _definitionService;
    private readonly ILogger<SwaggerRefreshService> _logger;

    public SwaggerRefreshService(
        SwaggerDashboardDbContext db,
        IOpenApiDocumentService documentService,
        IDashboardGeneratorService generator,
        IHashService hashService,
        IDashboardCache cache,
        ApiDefinitionService definitionService,
        ILogger<SwaggerRefreshService> logger)
    {
        _db = db;
        _documentService = documentService;
        _generator = generator;
        _hashService = hashService;
        _cache = cache;
        _definitionService = definitionService;
        _logger = logger;
    }

    public async Task<RefreshResult> RefreshAsync(
        int apiDefinitionId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var definition = await _db.ApiDefinitions
            .FirstOrDefaultAsync(a => a.Id == apiDefinitionId, cancellationToken);

        if (definition is null)
        {
            return RefreshResult.Fail($"API tanımı bulunamadı: {apiDefinitionId}");
        }

        if (!SwaggerUrlNormalizer.TryNormalize(definition.SwaggerUrlNormalized, out var uri, out var urlError))
        {
            return RefreshResult.Fail(urlError);
        }

        var fetch = await _documentService.FetchAsync(uri, cancellationToken);
        definition.LastSwaggerCheckAt = DateTimeOffset.UtcNow;

        if (!fetch.Success || fetch.Content is null)
        {
            await _db.SaveChangesAsync(cancellationToken);
            return RefreshResult.Fail(fetch.Error!);
        }

        var newHash = _hashService.ComputeSwaggerHash(fetch.Content);

        if (string.Equals(newHash, definition.SwaggerHash, StringComparison.Ordinal) &&
            definition.DashboardSchemaVersion == DashboardDocument.CurrentSchemaVersion)
        {
            // Identical document and a current dashboard shape: nothing to rebuild.
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Swagger unchanged for {ApiDefinitionId}", apiDefinitionId);
            return RefreshResult.Unchanged();
        }

        return await ApplyAsync(definition, fetch.Content, newHash, actor, cancellationToken);
    }

    public async Task<RefreshResult> RebuildAsync(
        int apiDefinitionId,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var definition = await _db.ApiDefinitions
            .FirstOrDefaultAsync(a => a.Id == apiDefinitionId, cancellationToken);

        if (definition is null)
        {
            return RefreshResult.Fail($"API tanımı bulunamadı: {apiDefinitionId}");
        }

        // Rebuild works from the stored raw document so it does not depend on the target
        // API being reachable; only if nothing was stored does it download again.
        var content = definition.RawSwaggerJson;

        if (string.IsNullOrWhiteSpace(content))
        {
            if (!SwaggerUrlNormalizer.TryNormalize(definition.SwaggerUrlNormalized, out var uri, out var urlError))
            {
                return RefreshResult.Fail(urlError);
            }

            var fetch = await _documentService.FetchAsync(uri, cancellationToken);
            if (!fetch.Success || fetch.Content is null)
            {
                return RefreshResult.Fail(fetch.Error!);
            }

            content = fetch.Content;
            definition.LastSwaggerCheckAt = DateTimeOffset.UtcNow;
        }

        var hash = _hashService.ComputeSwaggerHash(content);
        return await ApplyAsync(definition, content, hash, actor, cancellationToken);
    }

    private async Task<RefreshResult> ApplyAsync(
        Domain.Entities.ApiDefinition definition,
        string content,
        string newHash,
        string? actor,
        CancellationToken cancellationToken)
    {
        var generation = _generator.Generate(content);
        if (!generation.Success || generation.Document is null)
        {
            return RefreshResult.Fail(generation.Error!);
        }

        var previous = ApiDefinitionService.Deserialize(definition.DashboardJson);
        var diff = BuildDiff(previous, generation.Document);

        var now = DateTimeOffset.UtcNow;
        definition.DashboardJson = ApiDefinitionService.Serialize(generation.Document);
        definition.DashboardSchemaVersion = DashboardDocument.CurrentSchemaVersion;
        definition.RawSwaggerJson = content;
        definition.SwaggerHash = newHash;
        definition.SwaggerVersion = generation.Document.OpenApiVersion;
        definition.ApiVersion = generation.Document.Version;
        definition.EndpointCount = generation.Document.Operations.Count;
        definition.LastDashboardBuildAt = now;
        definition.UpdatedAt = now;
        definition.UpdatedBy = actor;

        await _definitionService.SyncEndpointsAsync(definition, generation.Document, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // The cache key contains the old hash, so stale entries would simply be orphaned;
        // they are dropped explicitly to keep memory bounded and route entries correct.
        _cache.InvalidateApi(definition.Id);
        _cache.SetDashboard(definition.Id, newHash, generation.Document);

        _logger.LogInformation(
            "Rebuilt dashboard for {ApiDefinitionId}: +{Added} -{Removed} ~{Modified}",
            definition.Id, diff.Added.Count, diff.Removed.Count, diff.Modified.Count);

        return RefreshResult.Updated(diff);
    }

    /// <summary>
    /// Compares two generated documents so the administrator sees what actually changed.
    /// </summary>
    internal static EndpointDiff BuildDiff(DashboardDocument? previous, DashboardDocument current)
    {
        var diff = new EndpointDiff();

        var previousOperations = previous?.Operations
            .ToDictionary(o => $"{o.Method} {o.Path}", StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DashboardOperation>(StringComparer.OrdinalIgnoreCase);

        var currentOperations = current.Operations
            .ToDictionary(o => $"{o.Method} {o.Path}", StringComparer.OrdinalIgnoreCase);

        foreach (var (key, operation) in currentOperations)
        {
            if (!previousOperations.TryGetValue(key, out var before))
            {
                diff.Added.Add(key);
                continue;
            }

            if (HasChanged(before, operation))
            {
                diff.Modified.Add(key);
            }
        }

        foreach (var key in previousOperations.Keys)
        {
            if (!currentOperations.ContainsKey(key))
            {
                diff.Removed.Add(key);
            }
        }

        diff.Added.Sort(StringComparer.OrdinalIgnoreCase);
        diff.Removed.Sort(StringComparer.OrdinalIgnoreCase);
        diff.Modified.Sort(StringComparer.OrdinalIgnoreCase);

        return diff;
    }

    private static bool HasChanged(DashboardOperation before, DashboardOperation after)
    {
        if (before.Deprecated != after.Deprecated ||
            !string.Equals(before.Summary, after.Summary, StringComparison.Ordinal) ||
            !string.Equals(before.OperationId, after.OperationId, StringComparison.Ordinal) ||
            before.Parameters.Count != after.Parameters.Count ||
            before.Responses.Count != after.Responses.Count ||
            (before.RequestBody is null) != (after.RequestBody is null))
        {
            return true;
        }

        for (var i = 0; i < before.Parameters.Count; i++)
        {
            var a = before.Parameters[i];
            var b = after.Parameters[i];

            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(a.In, b.In, StringComparison.Ordinal) ||
                a.Required != b.Required ||
                !string.Equals(a.Schema.Type, b.Schema.Type, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

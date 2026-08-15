using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Hashing;
using SwaggerDashboard.Application.Routing;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Resolves dashboard URLs and owns the lifecycle of an API definition.
/// </summary>
public class ApiDefinitionService : IApiDefinitionService
{
    /// <summary>
    /// One in-flight provisioning per target key. Without it two users opening the same
    /// cold URL would both download and both insert, and one would lose the race on the
    /// unique index.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProvisionLocks = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,

        // Summaries and descriptions are often not in English; escaping them to \u
        // sequences would triple their stored size for no benefit.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    private readonly SwaggerDashboardDbContext _db;
    private readonly IOpenApiDocumentService _documentService;
    private readonly IDashboardGeneratorService _generator;
    private readonly IHashService _hashService;
    private readonly IDashboardCache _cache;
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;
    private readonly ILogger<ApiDefinitionService> _logger;

    public ApiDefinitionService(
        SwaggerDashboardDbContext db,
        IOpenApiDocumentService documentService,
        IDashboardGeneratorService generator,
        IHashService hashService,
        IDashboardCache cache,
        IOptionsMonitor<SwaggerDashboardOptions> options,
        ILogger<ApiDefinitionService> logger)
    {
        _db = db;
        _documentService = documentService;
        _generator = generator;
        _hashService = hashService;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task<ResolveResult> ResolveAsync(
        string routeTail,
        ResolveContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ResolveCoreAsync(routeTail, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The caller is a Blazor component: an exception escaping here tears down the
            // circuit and leaves the visitor on a page that never finishes loading, with no
            // message and no way to retry. A failed resolve has to come back as a result.
            _logger.LogError(ex, "Resolving {RouteTail} failed unexpectedly", routeTail);

            return ResolveResult.Failure(
                ResolveStatus.ProvisioningFailed,
                "Adres çözümlenirken beklenmeyen bir hata oluştu. Ayrıntı uygulama logunda.");
        }
    }

    private async Task<ResolveResult> ResolveCoreAsync(
        string routeTail,
        ResolveContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(routeTail))
        {
            return ResolveResult.Failure(ResolveStatus.InvalidUrl, "Adres boş.");
        }

        routeTail = routeTail.Trim('/');

        var definition = ReservedRoutes.LooksLikeTargetUrl(routeTail)
            ? null
            : await FindByRouteNameAsync(routeTail, cancellationToken);

        if (definition is null)
        {
            if (!SwaggerUrlNormalizer.TryNormalize(routeTail, out var normalized, out var normalizeError))
            {
                return ResolveResult.Failure(ResolveStatus.InvalidUrl, normalizeError);
            }

            // The address the user typed may be the UI page, the short form or the document
            // itself, so lookup goes through the alias table rather than the canonical key.
            var requestKey = _hashService.ComputeSha256(normalized.AbsoluteUri);
            definition = await FindByUrlKeyAsync(requestKey, cancellationToken);

            if (definition is null)
            {
                return await ProvisionAsync(normalized, requestKey, context, cancellationToken);
            }
        }

        return await LoadDashboardAsync(definition, context, cancellationToken);
    }

    /// <summary>
    /// Reads the stored dashboard. This is the hot path taken on every later visit: no
    /// download, no parse, and no hash comparison happen here.
    /// </summary>
    private async Task<ResolveResult> LoadDashboardAsync(
        ApiDefinition definition,
        ResolveContext context,
        bool wasProvisioned,
        CancellationToken cancellationToken)
    {
        if (!definition.IsActive)
        {
            return ResolveResult.Failure(ResolveStatus.Inactive, $"'{definition.Name}' pasif durumda.");
        }

        if (_options.CurrentValue.Access.RequireAuthenticationToView && !context.IsAuthenticated)
        {
            return ResolveResult.Failure(
                ResolveStatus.ProvisioningForbidden,
                "Bu dashboard'u görüntülemek için giriş yapmalısınız.");
        }

        if (!IsVisibleTo(definition, context))
        {
            return ResolveResult.Failure(
                ResolveStatus.Forbidden, $"'{definition.Name}' için görüntüleme yetkiniz yok.");
        }

        var cached = _cache.GetDashboard(definition.Id, definition.SwaggerHash);
        if (cached is not null)
        {
            return ResolveResult.Found(definition, cached, wasProvisioned);
        }

        var dashboard = Deserialize(definition.DashboardJson);

        if (dashboard is null || definition.DashboardSchemaVersion != DashboardDocument.CurrentSchemaVersion)
        {
            // A stored document that cannot be read, or that predates the current generator
            // shape, is the one case where a read is allowed to rebuild.
            _logger.LogInformation(
                "Rebuilding dashboard for {ApiDefinitionId}: stored schema version {Stored}, current {Current}",
                definition.Id, definition.DashboardSchemaVersion, DashboardDocument.CurrentSchemaVersion);

            if (string.IsNullOrWhiteSpace(definition.RawSwaggerJson))
            {
                return ResolveResult.Failure(
                    ResolveStatus.ProvisioningFailed,
                    "Kayıtlı dashboard okunamadı ve ham doküman saklanmamış. Yönetim panelinden Swagger Güncelle çalıştırın.");
            }

            var regenerated = _generator.Generate(definition.RawSwaggerJson);
            if (!regenerated.Success || regenerated.Document is null)
            {
                return ResolveResult.Failure(ResolveStatus.ProvisioningFailed, regenerated.Error!);
            }

            dashboard = regenerated.Document;
            definition.DashboardJson = Serialize(dashboard);
            definition.DashboardSchemaVersion = DashboardDocument.CurrentSchemaVersion;
            definition.LastDashboardBuildAt = DateTimeOffset.UtcNow;
            await SyncEndpointsAsync(definition, dashboard, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        _cache.SetDashboard(definition.Id, definition.SwaggerHash, dashboard);
        return ResolveResult.Found(definition, dashboard, wasProvisioned);
    }

    private Task<ResolveResult> LoadDashboardAsync(
        ApiDefinition definition,
        ResolveContext context,
        CancellationToken cancellationToken) =>
        LoadDashboardAsync(definition, context, false, cancellationToken);

    /// <summary>
    /// Handles the first visit to an address the platform has not seen before.
    /// </summary>
    /// <remarks>
    /// A miss on the alias table does not mean the API is unknown: the same document may
    /// already be registered under a different spelling of its URL. That is only knowable
    /// after the document has been fetched, so the flow is fetch, then match on the
    /// canonical key, then either bind a new alias or create the definition.
    /// </remarks>
    private async Task<ResolveResult> ProvisionAsync(
        Uri swaggerUrl,
        string requestKey,
        ResolveContext context,
        CancellationToken cancellationToken)
    {
        var provisioning = _options.CurrentValue.Provisioning;

        if (!provisioning.AutoProvisionOnFirstVisit)
        {
            return ResolveResult.Failure(
                ResolveStatus.NotRegistered,
                "Bu swagger adresi kayıtlı değil. Otomatik kayıt kapalı; yöneticinizden API'yi tanımlamasını isteyin.");
        }

        if (!context.IsAuthenticated)
        {
            return ResolveResult.Failure(
                ResolveStatus.ProvisioningForbidden,
                "Bu swagger adresi henüz kayıtlı değil. Yeni bir API kaydı oluşturmak için giriş yapmalısınız.");
        }

        if (!Array.Exists(Roles.CanProvision, r => string.Equals(r, context.Role, StringComparison.OrdinalIgnoreCase)))
        {
            return ResolveResult.Failure(
                ResolveStatus.ProvisioningForbidden,
                "Bu swagger adresi kayıtlı değil ve yeni API kaydı oluşturma yetkiniz yok.");
        }

        // One provisioning at a time per address, so two users opening the same cold URL
        // do not both download and both insert.
        var gate = ProvisionLocks.GetOrAdd(requestKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            // Another request may have finished provisioning while this one waited.
            var existing = await FindByUrlKeyAsync(requestKey, cancellationToken);
            if (existing is not null)
            {
                return await LoadDashboardAsync(existing, context, cancellationToken);
            }

            var fetch = await _documentService.FetchAsync(swaggerUrl, cancellationToken);
            if (!fetch.Success || fetch.DocumentUrl is null || fetch.Content is null)
            {
                return ResolveResult.Failure(ResolveStatus.ProvisioningFailed, fetch.Error!);
            }

            var targetKey = _hashService.ComputeSha256(fetch.DocumentUrl.AbsoluteUri);
            var known = await FindByTargetKeyAsync(targetKey, cancellationToken);

            if (known is not null)
            {
                // A new spelling of an address that is already registered: remember it so
                // the next visit resolves without another download.
                await AddAliasAsync(known, requestKey, swaggerUrl.AbsoluteUri, cancellationToken);
                return await LoadDashboardAsync(known, context, cancellationToken);
            }

            var created = await CreateDefinitionAsync(
                new RegisterApiRequest
                {
                    SwaggerUrl = swaggerUrl.AbsoluteUri,
                    Actor = context.UserName,
                    IsAutoProvisioned = true,
                },
                swaggerUrl,
                fetch.DocumentUrl,
                fetch.Content,
                targetKey,
                requestKey,
                cancellationToken);

            if (!created.Success || created.Definition is null)
            {
                return ResolveResult.Failure(ResolveStatus.ProvisioningFailed, created.Error!);
            }

            return await LoadDashboardAsync(created.Definition, context, wasProvisioned: true, cancellationToken);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
            {
                ProvisionLocks.TryRemove(requestKey, out _);
            }
        }
    }

    public async Task<RegistrationResult> RegisterAsync(
        RegisterApiRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!SwaggerUrlNormalizer.TryNormalize(request.SwaggerUrl, out var swaggerUri, out var urlError))
        {
            return RegistrationResult.Fail(urlError);
        }

        if (!string.IsNullOrWhiteSpace(request.RouteName))
        {
            if (!ReservedRoutes.IsValidAlias(request.RouteName, out var aliasError))
            {
                return RegistrationResult.Fail(aliasError!);
            }

            var aliasTaken = await _db.ApiDefinitions
                .AnyAsync(a => a.RouteName == request.RouteName, cancellationToken);

            if (aliasTaken)
            {
                return RegistrationResult.Fail($"'{request.RouteName}' route adı zaten kullanılıyor.");
            }
        }

        var fetch = await _documentService.FetchAsync(swaggerUri, cancellationToken);
        if (!fetch.Success || fetch.DocumentUrl is null || fetch.Content is null)
        {
            return RegistrationResult.Fail(fetch.Error!);
        }

        // The canonical key comes from the resolved document URL so that /swagger and
        // /swagger/index.html collapse onto one registration.
        var targetKey = _hashService.ComputeSha256(fetch.DocumentUrl.AbsoluteUri);

        var duplicate = await FindByTargetKeyAsync(targetKey, cancellationToken);
        if (duplicate is not null)
        {
            return RegistrationResult.Fail(
                $"Bu doküman zaten '{duplicate.Name}' adıyla kayıtlı (/{BuildRoute(duplicate)}).");
        }

        return await CreateDefinitionAsync(
            request,
            swaggerUri,
            fetch.DocumentUrl,
            fetch.Content,
            targetKey,
            _hashService.ComputeSha256(swaggerUri.AbsoluteUri),
            cancellationToken);
    }

    /// <summary>
    /// Persists a new API definition from an already downloaded document.
    /// </summary>
    /// <remarks>
    /// Shared by explicit registration and by first-visit provisioning so that both paths
    /// produce identical rows, aliases and cache state.
    /// </remarks>
    private async Task<RegistrationResult> CreateDefinitionAsync(
        RegisterApiRequest request,
        Uri requestedUrl,
        Uri documentUrl,
        string content,
        string targetKey,
        string requestKey,
        CancellationToken cancellationToken)
    {
        var generation = _generator.Generate(content);
        if (!generation.Success || generation.Document is null)
        {
            return RegistrationResult.Fail(generation.Error!);
        }

        var dashboard = generation.Document;
        var now = DateTimeOffset.UtcNow;

        var definition = new ApiDefinition
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? dashboard.Title : request.Name,
            Description = request.Description ?? Truncate(dashboard.Description, 1000),
            RouteName = string.IsNullOrWhiteSpace(request.RouteName) ? null : request.RouteName,
            TargetKey = targetKey,
            SwaggerUrl = request.SwaggerUrl,
            SwaggerUrlNormalized = documentUrl.AbsoluteUri,
            BaseUrl = string.IsNullOrWhiteSpace(request.BaseUrl)
                ? DeriveBaseUrl(documentUrl, dashboard)
                : request.BaseUrl.TrimEnd('/'),
            SwaggerVersion = dashboard.OpenApiVersion,
            ApiVersion = dashboard.Version,
            DashboardJson = Serialize(dashboard),
            DashboardSchemaVersion = DashboardDocument.CurrentSchemaVersion,
            RawSwaggerJson = content,
            SwaggerHash = _hashService.ComputeSwaggerHash(content),
            EndpointCount = dashboard.Operations.Count,
            IsActive = true,
            IsAutoProvisioned = request.IsAutoProvisioned,
            AllowedRoles = request.AllowedRoles,
            CreatedAt = now,
            UpdatedAt = now,
            LastSwaggerCheckAt = now,
            LastDashboardBuildAt = now,
            CreatedBy = request.Actor,
            UpdatedBy = request.Actor,
        };

        _db.ApiDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken);

        await SyncEndpointsAsync(definition, dashboard, cancellationToken);

        definition.Environments.Add(new ApiEnvironment
        {
            ApiDefinitionId = definition.Id,
            Name = "Default",
            BaseUrl = definition.BaseUrl,
            IsDefault = true,
            CreatedAt = now,
        });

        AddServerEnvironments(definition, dashboard, now);

        // Both the document URL and whatever the user actually typed must resolve here.
        AddAlias(definition, targetKey, documentUrl.AbsoluteUri, now);
        if (!string.Equals(requestKey, targetKey, StringComparison.Ordinal))
        {
            AddAlias(definition, requestKey, requestedUrl.AbsoluteUri, now);
        }

        await _db.SaveChangesAsync(cancellationToken);

        _cache.SetDashboard(definition.Id, definition.SwaggerHash, dashboard);

        _logger.LogInformation(
            "Registered API {Name} ({Id}) for {DocumentUrl} with {EndpointCount} endpoints, auto={Auto}",
            definition.Name, definition.Id, definition.SwaggerUrlNormalized, definition.EndpointCount,
            definition.IsAutoProvisioned);

        return RegistrationResult.Ok(definition);
    }

    public async Task<ApiDefinition?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        await _db.ApiDefinitions
            .Include(a => a.Environments)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<DashboardDocument?> GetDashboardAsync(
        int apiDefinitionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _db.ApiDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == apiDefinitionId, cancellationToken);

        if (definition is null)
        {
            return null;
        }

        var cached = _cache.GetDashboard(definition.Id, definition.SwaggerHash);
        if (cached is not null)
        {
            return cached;
        }

        var dashboard = Deserialize(definition.DashboardJson);
        if (dashboard is not null)
        {
            _cache.SetDashboard(definition.Id, definition.SwaggerHash, dashboard);
        }

        return dashboard;
    }

    public async Task<IReadOnlyList<ApiDefinition>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = _db.ApiDefinitions.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(a => a.IsActive);
        }

        return await query.OrderBy(a => a.Name).ToListAsync(cancellationToken);
    }

    public async Task UpdateMetadataAsync(UpdateApiRequest request, CancellationToken cancellationToken = default)
    {
        var definition = await _db.ApiDefinitions.FirstOrDefaultAsync(a => a.Id == request.Id, cancellationToken)
            ?? throw new InvalidOperationException($"API tanımı bulunamadı: {request.Id}");

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            definition.Name = request.Name;
        }

        definition.Description = request.Description;
        definition.AllowedRoles = request.AllowedRoles;

        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            definition.BaseUrl = request.BaseUrl.TrimEnd('/');
        }

        var newAlias = string.IsNullOrWhiteSpace(request.RouteName) ? null : request.RouteName.Trim();

        if (newAlias != definition.RouteName)
        {
            if (newAlias is not null)
            {
                if (!ReservedRoutes.IsValidAlias(newAlias, out var aliasError))
                {
                    throw new InvalidOperationException(aliasError);
                }

                var taken = await _db.ApiDefinitions
                    .AnyAsync(a => a.RouteName == newAlias && a.Id != definition.Id, cancellationToken);

                if (taken)
                {
                    throw new InvalidOperationException($"'{newAlias}' route adı zaten kullanılıyor.");
                }
            }

            definition.RouteName = newAlias;
        }

        definition.UpdatedAt = DateTimeOffset.UtcNow;
        definition.UpdatedBy = request.Actor;

        await _db.SaveChangesAsync(cancellationToken);
        _cache.InvalidateApi(definition.Id);
    }

    public async Task SetActiveAsync(int id, bool isActive, string? actor, CancellationToken cancellationToken = default)
    {
        var definition = await _db.ApiDefinitions.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"API tanımı bulunamadı: {id}");

        definition.IsActive = isActive;
        definition.UpdatedAt = DateTimeOffset.UtcNow;
        definition.UpdatedBy = actor;

        await _db.SaveChangesAsync(cancellationToken);
        _cache.InvalidateApi(id);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var definition = await _db.ApiDefinitions.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (definition is null)
        {
            return;
        }

        _db.ApiDefinitions.Remove(definition);
        await _db.SaveChangesAsync(cancellationToken);
        _cache.InvalidateApi(id);
    }

    /// <summary>
    /// Mirrors the generated operations into the endpoint table used for search and diffing.
    /// </summary>
    internal async Task SyncEndpointsAsync(
        ApiDefinition definition,
        DashboardDocument dashboard,
        CancellationToken cancellationToken)
    {
        var existing = await _db.ApiEndpoints
            .Where(e => e.ApiDefinitionId == definition.Id)
            .ToListAsync(cancellationToken);

        var bySlug = existing.ToDictionary(e => e.Slug, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in dashboard.Operations)
        {
            seen.Add(operation.Slug);

            if (!bySlug.TryGetValue(operation.Slug, out var endpoint))
            {
                endpoint = new ApiEndpoint
                {
                    ApiDefinitionId = definition.Id,
                    Slug = operation.Slug,
                    CreatedAt = now,
                };
                _db.ApiEndpoints.Add(endpoint);
            }

            endpoint.Path = operation.Path;
            endpoint.HttpMethod = operation.Method;
            endpoint.OperationId = Truncate(operation.OperationId, 300);
            endpoint.Summary = Truncate(operation.Summary, 1000);
            endpoint.Description = operation.Description;
            endpoint.Tag = Truncate(operation.Tag, 200);
            endpoint.IsDeprecated = operation.Deprecated;
            endpoint.RequiresAuthentication = operation.RequiresAuthentication;
            endpoint.RequestSchemaJson = operation.RequestBody is null
                ? null
                : JsonSerializer.Serialize(operation.RequestBody, JsonOptions);
            endpoint.ResponseSchemaJson = operation.Responses.Count == 0
                ? null
                : JsonSerializer.Serialize(operation.Responses, JsonOptions);
            endpoint.SecuritySchemaJson = operation.SecuritySchemeKeys.Count == 0
                ? null
                : JsonSerializer.Serialize(operation.SecuritySchemeKeys, JsonOptions);
            endpoint.UpdatedAt = now;
        }

        foreach (var endpoint in existing.Where(e => !seen.Contains(e.Slug)))
        {
            _db.ApiEndpoints.Remove(endpoint);
        }

        definition.EndpointCount = dashboard.Operations.Count;
    }

    /// <summary>Resolves the optional short alias, e.g. /customer-api.</summary>
    private async Task<ApiDefinition?> FindByRouteNameAsync(string routeName, CancellationToken cancellationToken) =>
        await LookupAsync(
            "route:" + routeName.ToLowerInvariant(),
            () => _db.ApiDefinitions.FirstOrDefaultAsync(a => a.RouteName == routeName, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Resolves any address that has been seen before, whether it is the document URL, the
    /// swagger UI page or a shorter form of either.
    /// </summary>
    private async Task<ApiDefinition?> FindByUrlKeyAsync(string urlKey, CancellationToken cancellationToken) =>
        await LookupAsync(
            "url:" + urlKey,
            () => _db.ApiUrlAliases
                .Where(a => a.UrlKey == urlKey)
                .Select(a => a.ApiDefinition!)
                .FirstOrDefaultAsync(cancellationToken),
            cancellationToken);

    /// <summary>Resolves the canonical document key.</summary>
    private async Task<ApiDefinition?> FindByTargetKeyAsync(string targetKey, CancellationToken cancellationToken) =>
        await LookupAsync(
            "target:" + targetKey,
            () => _db.ApiDefinitions.FirstOrDefaultAsync(a => a.TargetKey == targetKey, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Caches the route to id mapping so a resolve does not hit the database on every visit,
    /// then loads the definition itself.
    /// </summary>
    private async Task<ApiDefinition?> LookupAsync(
        string routeKey,
        Func<Task<ApiDefinition?>> query,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetRoute(routeKey, out var cachedId))
        {
            var cached = await _db.ApiDefinitions
                .Include(a => a.Environments)
                .FirstOrDefaultAsync(a => a.Id == cachedId, cancellationToken);

            if (cached is not null)
            {
                return cached;
            }
        }

        var definition = await query();

        if (definition is not null)
        {
            _cache.SetRoute(routeKey, definition.Id);
            await _db.Entry(definition).Collection(a => a.Environments).LoadAsync(cancellationToken);
        }

        return definition;
    }

    /// <summary>Records a new spelling of an address that resolves to an existing API.</summary>
    private async Task AddAliasAsync(
        ApiDefinition definition,
        string urlKey,
        string url,
        CancellationToken cancellationToken)
    {
        if (await _db.ApiUrlAliases.AnyAsync(a => a.UrlKey == urlKey, cancellationToken))
        {
            return;
        }

        _db.ApiUrlAliases.Add(new ApiUrlAlias
        {
            ApiDefinitionId = definition.Id,
            UrlKey = urlKey,
            Url = url,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Bound additional address {Url} to API {ApiDefinitionId}", url, definition.Id);
    }

    private static void AddAlias(ApiDefinition definition, string urlKey, string url, DateTimeOffset now) =>
        definition.Aliases.Add(new ApiUrlAlias
        {
            ApiDefinitionId = definition.Id,
            UrlKey = urlKey,
            Url = url,
            CreatedAt = now,
        });

    private static bool IsVisibleTo(ApiDefinition definition, ResolveContext context) =>
        IsVisibleTo(definition, context.Role);

    /// <summary>
    /// Whether a role may see and use this API. Public because the proxy has to ask the same
    /// question at call time, from a role it read out of the database rather than a cookie.
    /// </summary>
    public static bool IsVisibleTo(ApiDefinition definition, string? role)
    {
        if (string.IsNullOrWhiteSpace(definition.AllowedRoles))
        {
            return true;
        }

        if (string.Equals(role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var allowed = definition.AllowedRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Array.Exists(allowed, r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Picks the address the proxy will call: the document's first server entry when it is
    /// usable, otherwise the origin the document itself was served from.
    /// </summary>
    internal static string DeriveBaseUrl(Uri documentUrl, DashboardDocument dashboard)
    {
        foreach (var entry in dashboard.Servers)
        {
            var server = entry.Url;

            if (string.IsNullOrWhiteSpace(server) || server.Contains('{'))
            {
                // Templated server URLs need variable values the dashboard does not have.
                continue;
            }

            // The scheme has to be checked explicitly: on Unix "/api/v2" parses as an
            // absolute file:// URI, which would silently become the proxy's base address.
            if (Uri.TryCreate(server, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            {
                return absolute.AbsoluteUri.TrimEnd('/');
            }

            if (Uri.TryCreate(documentUrl, server, out var relative))
            {
                return relative.AbsoluteUri.TrimEnd('/');
            }
        }

        return documentUrl.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Turns the document's remaining servers into environments.
    /// </summary>
    /// <remarks>
    /// A document that lists production, test and dev is telling us the environments; making
    /// the user retype them from a file they already gave us is busywork. The first server is
    /// already the default environment, so only the others are added, and only when they are
    /// usable absolute addresses. The document names them through the description; without one
    /// they are numbered, because an environment nobody can tell apart is worse than none.
    /// </remarks>
    private static void AddServerEnvironments(
        ApiDefinition definition,
        DashboardDocument dashboard,
        DateTimeOffset now)
    {
        var index = 1;

        foreach (var server in dashboard.Servers)
        {
            index++;

            if (string.IsNullOrWhiteSpace(server.Url) || server.Url.Contains('{'))
            {
                continue;
            }

            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var baseUrl = uri.AbsoluteUri.TrimEnd('/');

            if (definition.Environments.Any(e =>
                    string.Equals(e.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(server.Description)
                ? $"Sunucu {index}"
                : Truncate(server.Description.Trim(), 64)!;

            if (definition.Environments.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                name = $"{name} ({index})";
            }

            definition.Environments.Add(new ApiEnvironment
            {
                ApiDefinitionId = definition.Id,
                Name = name,
                BaseUrl = baseUrl,
                IsDefault = false,
                CreatedAt = now,
            });
        }
    }

    /// <summary>The route the user should bookmark for this API.</summary>
    public static string BuildRoute(ApiDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.RouteName))
        {
            return definition.RouteName;
        }

        return Uri.TryCreate(definition.SwaggerUrlNormalized, UriKind.Absolute, out var uri)
            ? SwaggerUrlNormalizer.ToRouteTail(uri)
            : definition.SwaggerUrlNormalized;
    }

    internal static string Serialize(DashboardDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    internal static DashboardDocument? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DashboardDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}

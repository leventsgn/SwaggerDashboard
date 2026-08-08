using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Execution;
using SwaggerDashboard.Infrastructure.Http;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Executes an endpoint call on behalf of the browser.
/// </summary>
public class ApiProxyService : IApiProxyService
{
    private readonly SwaggerDashboardDbContext _db;
    private readonly IApiDefinitionService _definitionService;
    private readonly IApiCredentialStore _credentialStore;
    private readonly GuardedHttpSender _sender;
    private readonly IRequestLogService _logService;
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;
    private readonly ILogger<ApiProxyService> _logger;

    public ApiProxyService(
        SwaggerDashboardDbContext db,
        IApiDefinitionService definitionService,
        IApiCredentialStore credentialStore,
        GuardedHttpSender sender,
        IRequestLogService logService,
        IOptionsMonitor<SwaggerDashboardOptions> options,
        ILogger<ApiProxyService> logger)
    {
        _db = db;
        _definitionService = definitionService;
        _credentialStore = credentialStore;
        _sender = sender;
        _logService = logService;
        _options = options;
        _logger = logger;
    }

    public async Task<ProxyResponse> ExecuteAsync(
        ProxyRequest request,
        CancellationToken cancellationToken = default)
    {
        var definition = await _definitionService.GetByIdAsync(request.ApiDefinitionId, cancellationToken);
        if (definition is null)
        {
            return ProxyResponse.Failure($"API tanımı bulunamadı: {request.ApiDefinitionId}");
        }

        if (!definition.IsActive)
        {
            return ProxyResponse.Failure($"'{definition.Name}' pasif durumda, istek gönderilemez.");
        }

        var dashboard = await _definitionService.GetDashboardAsync(definition.Id, cancellationToken);
        var operation = dashboard?.Operations
            .FirstOrDefault(o => string.Equals(o.Slug, request.OperationSlug, StringComparison.OrdinalIgnoreCase));

        if (operation is null)
        {
            return ProxyResponse.Failure($"Endpoint bulunamadı: {request.OperationSlug}");
        }

        var baseUrl = definition.BaseUrl;

        if (!string.IsNullOrWhiteSpace(request.EnvironmentName))
        {
            var environment = definition.Environments
                .FirstOrDefault(e => string.Equals(e.Name, request.EnvironmentName, StringComparison.OrdinalIgnoreCase));

            if (environment is null)
            {
                return ProxyResponse.Failure($"Ortam bulunamadı: {request.EnvironmentName}");
            }

            baseUrl = environment.BaseUrl;
        }

        var credential = request.UserId is null
            ? null
            : _credentialStore.Get(request.UserId, definition.Id);

        var built = TargetRequestBuilder.Build(baseUrl, operation, request, credential);
        if (!built.Success || built.Uri is null)
        {
            return ProxyResponse.Failure(built.Error!, string.Empty, operation.Method);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var (bodyText, contentFactory) = BuildContent(request);

        var guarded = await _sender.SendAsync(
            built.Uri,
            uri =>
            {
                var message = new HttpRequestMessage(new HttpMethod(built.Method), uri);

                foreach (var (name, value) in built.Headers)
                {
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    message.Headers.TryAddWithoutValidation(name, value);
                }

                message.Content = contentFactory();
                return message;
            },
            _options.CurrentValue.Outbound.MaxProxyResponseBytes,
            cancellationToken);

        stopwatch.Stop();
        var completedAt = DateTimeOffset.UtcNow;

        var response = new ProxyResponse
        {
            Success = guarded.IsCompleted && guarded.StatusCode is >= 200 and < 400,
            StatusCode = guarded.StatusCode,
            ReasonPhrase = guarded.ReasonPhrase,
            RequestUrl = built.Uri.AbsoluteUri,
            RequestMethod = built.Method,
            RequestHeaders = built.Headers,
            RequestBody = bodyText,
            ResponseHeaders = guarded.Headers,
            ResponseBody = guarded.Content,
            IsBinary = guarded.IsCompleted && guarded.Content is null && guarded.RawContent.Length > 0,
            Truncated = guarded.Truncated,
            ContentType = guarded.ContentType,
            ContentLength = guarded.TotalBytes,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Error = guarded.Error,
        };

        var endpointId = await _db.ApiEndpoints
            .Where(e => e.ApiDefinitionId == definition.Id && e.Slug == operation.Slug)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        await _logService.RecordAsync(
            definition.Id, endpointId, response, request.UserId, request.ClientIp, cancellationToken);

        if (!guarded.IsCompleted)
        {
            _logger.LogWarning(
                "Proxy call to {Url} did not complete: {Error}", built.Uri, guarded.Error);
        }

        return response;
    }

    /// <summary>
    /// Produces the outbound body. Multipart is assembled from the uploaded files and form
    /// fields; a form encoded operation uses the field list; everything else sends the raw
    /// body with the declared content type.
    /// </summary>
    private static (string? BodyText, Func<HttpContent?> Factory) BuildContent(ProxyRequest request)
    {
        if (request.Files.Count > 0)
        {
            var description = string.Join(", ",
                request.FormFields.Select(f => $"{f.Key}={f.Value}")
                    .Concat(request.Files.Select(f => $"{f.FieldName}=@{f.FileName}")));

            return (description, () =>
            {
                var content = new MultipartFormDataContent();

                foreach (var (key, value) in request.FormFields)
                {
                    content.Add(new StringContent(value ?? string.Empty, Encoding.UTF8), key);
                }

                foreach (var file in request.Files)
                {
                    var fileContent = new ByteArrayContent(file.Content);
                    fileContent.Headers.TryAddWithoutValidation("Content-Type", file.ContentType);
                    content.Add(fileContent, file.FieldName, file.FileName);
                }

                return content;
            });
        }

        if (request.FormFields.Count > 0)
        {
            var encoded = string.Join("&", request.FormFields.Select(f =>
                $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value ?? string.Empty)}"));

            return (encoded, () => new StringContent(
                encoded, Encoding.UTF8, "application/x-www-form-urlencoded"));
        }

        if (string.IsNullOrEmpty(request.Body))
        {
            return (null, () => null);
        }

        var contentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? "application/json"
            : request.ContentType;

        var mediaType = contentType.Split(';')[0].Trim();

        return (request.Body, () => new StringContent(request.Body, Encoding.UTF8, mediaType));
    }
}

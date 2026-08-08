using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Routing;
using SwaggerDashboard.Infrastructure.Http;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Resolves a pasted swagger URL to the OpenAPI document and downloads it.
/// </summary>
public class OpenApiDocumentService : IOpenApiDocumentService
{
    private readonly GuardedHttpSender _sender;
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;
    private readonly ILogger<OpenApiDocumentService> _logger;

    public OpenApiDocumentService(
        GuardedHttpSender sender,
        IOptionsMonitor<SwaggerDashboardOptions> options,
        ILogger<OpenApiDocumentService> logger)
    {
        _sender = sender;
        _options = options;
        _logger = logger;
    }

    public async Task<OpenApiFetchResult> FetchAsync(Uri swaggerUrl, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var attempted = new List<string>();
        string? firstBlockReason = null;

        foreach (var candidate in BuildCandidates(swaggerUrl, options.Provisioning.DocumentProbePaths))
        {
            if (!attempted.Contains(candidate.AbsoluteUri))
            {
                attempted.Add(candidate.AbsoluteUri);
            }
            else
            {
                continue;
            }

            var response = await _sender.SendAsync(
                candidate,
                uri => new HttpRequestMessage(HttpMethod.Get, uri)
                {
                    Headers = { { "Accept", "application/json, application/yaml, text/yaml, text/plain" } },
                },
                options.Outbound.MaxDocumentBytes,
                cancellationToken);

            if (response.Outcome == GuardedOutcome.Blocked)
            {
                // A policy denial is terminal: probing further paths on a forbidden host
                // would just be more forbidden requests.
                return OpenApiFetchResult.Fail(response.Error ?? "Adres engellendi.");
            }

            if (!response.IsCompleted)
            {
                firstBlockReason ??= response.Error;
                continue;
            }

            if (response.StatusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogDebug("Probe {Uri} returned {Status}", candidate, response.StatusCode);
                continue;
            }

            if (response.Truncated)
            {
                return OpenApiFetchResult.Fail(
                    $"Doküman {options.Outbound.MaxDocumentBytes} bayt sınırını aşıyor.");
            }

            if (LooksLikeOpenApiDocument(response.Content))
            {
                // The canonical document URL, not the pasted one, is what identifies the API.
                return OpenApiFetchResult.Ok(
                    SwaggerUrlNormalizer.Canonicalize(response.Uri), response.Content);
            }

            _logger.LogDebug("Probe {Uri} did not return an OpenAPI document", candidate);
        }

        var message = firstBlockReason is not null
            ? $"OpenAPI dokümanı bulunamadı: {firstBlockReason}"
            : "Bu adreste OpenAPI dokümanı bulunamadı. Denenen adresler: " +
              string.Join(", ", attempted.Take(6));

        return OpenApiFetchResult.Fail(message);
    }

    /// <summary>
    /// Produces the URLs to try, in order: the pasted URL itself, then the probe paths
    /// relative to its directory, then the probe paths at the host root.
    /// </summary>
    /// <remarks>
    /// Users usually paste the swagger UI page. Parsing that HTML is deliberately avoided,
    /// so the well known document locations are probed instead.
    /// </remarks>
    internal static IEnumerable<Uri> BuildCandidates(Uri swaggerUrl, IReadOnlyList<string> probePaths)
    {
        yield return swaggerUrl;

        var path = swaggerUrl.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        var directory = lastSlash >= 0 ? path[..lastSlash] : string.Empty;

        // "/swagger/index.html" gives a directory of "/swagger", which is where a .NET API
        // usually exposes the document.
        if (!string.IsNullOrEmpty(directory) && directory != "/")
        {
            foreach (var probe in probePaths)
            {
                var trimmed = probe.TrimStart('/');
                if (Uri.TryCreate(swaggerUrl, $"{directory}/{trimmed}", out var candidate))
                {
                    yield return candidate;
                }

                // /swagger + /swagger/v1/swagger.json would double the segment, so also try
                // the probe path anchored at the directory's parent.
                if (trimmed.StartsWith(directory.TrimStart('/') + "/", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(swaggerUrl, "/" + trimmed, out var rooted))
                {
                    yield return rooted;
                }
            }
        }

        foreach (var probe in probePaths)
        {
            if (Uri.TryCreate(swaggerUrl, probe, out var candidate))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Checks that the payload is an OpenAPI document rather than an HTML page or an
    /// unrelated JSON response.
    /// </summary>
    internal static bool LooksLikeOpenApiDocument(string content)
    {
        var trimmed = content.TrimStart();

        if (trimmed.StartsWith('<'))
        {
            return false;
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 256,
                });

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var hasVersion = root.TryGetProperty("openapi", out _) || root.TryGetProperty("swagger", out _);
                var hasPaths = root.TryGetProperty("paths", out _);

                return hasVersion && hasPaths;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // YAML documents are handed to the reader, which supports them; a cheap prefix
        // check keeps unrelated text out.
        return trimmed.StartsWith("openapi:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("swagger:", StringComparison.OrdinalIgnoreCase);
    }
}

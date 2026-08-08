using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Security;

namespace SwaggerDashboard.Infrastructure.Http;

/// <summary>
/// Sends outbound requests with the platform's safety rules applied: policy check per hop,
/// bounded redirects, request timeout and a hard cap on how much body is read.
/// </summary>
public class GuardedHttpSender
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOutboundUrlValidator _validator;
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;
    private readonly ILogger<GuardedHttpSender> _logger;

    public GuardedHttpSender(
        IHttpClientFactory httpClientFactory,
        IOutboundUrlValidator validator,
        IOptionsMonitor<SwaggerDashboardOptions> options,
        ILogger<GuardedHttpSender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _validator = validator;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Executes the request, following redirects manually so every hop is validated.
    /// </summary>
    /// <param name="requestFactory">
    /// Creates a fresh request message for a given URL. A new instance is required per hop
    /// because an HttpRequestMessage cannot be sent twice.
    /// </param>
    public async Task<GuardedResponse> SendAsync(
        Uri uri,
        Func<Uri, HttpRequestMessage> requestFactory,
        long maxResponseBytes,
        CancellationToken cancellationToken)
    {
        var outbound = _options.CurrentValue.Outbound;
        var client = _httpClientFactory.CreateClient(OutboundHttpClient.Name);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(outbound.TimeoutSeconds));

        var currentUri = uri;

        for (var hop = 0; hop <= outbound.MaxRedirects; hop++)
        {
            var validation = await _validator.ValidateAsync(currentUri, timeoutSource.Token);
            if (!validation.IsAllowed)
            {
                return GuardedResponse.Blocked(validation.Reason ?? "Adres engellendi.", currentUri);
            }

            HttpResponseMessage response;
            using var request = requestFactory(currentUri);

            try
            {
                response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            }
            catch (OutboundBlockedException ex)
            {
                return GuardedResponse.Blocked(ex.Message, currentUri);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return GuardedResponse.Failed(
                    $"İstek {outbound.TimeoutSeconds} saniye içinde tamamlanmadı.", currentUri);
            }
            catch (HttpRequestException ex)
            {
                var reason = ex.InnerException is OutboundBlockedException blocked ? blocked.Message : ex.Message;
                return GuardedResponse.Failed($"Hedef adrese ulaşılamadı: {reason}", currentUri);
            }

            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                var location = response.Headers.Location;
                var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                response.Dispose();

                if (hop == outbound.MaxRedirects)
                {
                    return GuardedResponse.Failed(
                        $"Yönlendirme sınırı ({outbound.MaxRedirects}) aşıldı.", currentUri);
                }

                _logger.LogDebug("Following redirect {From} -> {To}", currentUri, next);
                currentUri = next;
                continue;
            }

            var read = await ReadBoundedAsync(response, maxResponseBytes, timeoutSource.Token);
            return new GuardedResponse(
                GuardedOutcome.Completed,
                currentUri,
                (int)response.StatusCode,
                response.ReasonPhrase,
                CollectHeaders(response),
                read.Content,
                read.Bytes,
                read.Truncated,
                response.Content.Headers.ContentType?.ToString(),
                read.TotalBytes,
                null);
        }

        return GuardedResponse.Failed("Yönlendirme sınırı aşıldı.", currentUri);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, values) in response.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        return headers;
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> so a huge or endless response cannot
    /// exhaust memory. Content-Length is only a hint, so the cap is enforced while reading.
    /// </summary>
    private static async Task<BoundedRead> ReadBoundedAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        long total = 0;
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;

            if (buffer.Length < maxBytes)
            {
                var writable = (int)Math.Min(read, maxBytes - buffer.Length);
                buffer.Write(chunk, 0, writable);

                if (writable < read)
                {
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }
        }

        var bytes = buffer.ToArray();
        return new BoundedRead(bytes, DecodeIfText(response, bytes), truncated, total);
    }

    /// <summary>
    /// Decodes the body as text when the content type says it is text, and returns null for
    /// binary payloads so they are offered as a download instead of being pasted into the UI.
    /// </summary>
    private static string? DecodeIfText(HttpResponseMessage response, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        var isText = mediaType is null ||
                     mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                     mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                     mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                     mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                     mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase) ||
                     mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

        if (!isText)
        {
            return null;
        }

        var encoding = System.Text.Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown charset falls back to UTF-8 rather than failing the call.
            }
        }

        return encoding.GetString(bytes);
    }

    private record BoundedRead(byte[] Bytes, string? Content, bool Truncated, long TotalBytes);
}

public enum GuardedOutcome
{
    Completed,
    Blocked,
    Failed,
}

public record GuardedResponse(
    GuardedOutcome Outcome,
    Uri Uri,
    int StatusCode,
    string? ReasonPhrase,
    Dictionary<string, string> Headers,
    string? Content,
    byte[] RawContent,
    bool Truncated,
    string? ContentType,
    long TotalBytes,
    string? Error)
{
    public bool IsCompleted => Outcome == GuardedOutcome.Completed;

    public static GuardedResponse Blocked(string reason, Uri uri) => new(
        GuardedOutcome.Blocked, uri, 0, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, [], false, null, 0, reason);

    public static GuardedResponse Failed(string reason, Uri uri) => new(
        GuardedOutcome.Failed, uri, 0, null,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, [], false, null, 0, reason);
}

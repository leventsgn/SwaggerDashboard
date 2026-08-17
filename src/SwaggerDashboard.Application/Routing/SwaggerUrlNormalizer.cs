using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SwaggerDashboard.Application.Routing;

/// <summary>
/// Turns the tail of a prefixed dashboard URL into a canonical absolute swagger URL.
/// </summary>
/// <remarks>
/// The user takes their normal swagger link and replaces the scheme with the dashboard
/// host, so <c>https://api.company.com/swagger</c> becomes
/// <c>https://dashboard.company.com/api.company.com/swagger</c>. The scheme-less form is
/// canonical because reverse proxies such as nginx collapse the double slash in an
/// embedded <c>https://</c> by default; the collapsed form is still accepted and repaired.
/// </remarks>
public static partial class SwaggerUrlNormalizer
{
    private const string HttpsPrefix = "https://";
    private const string HttpPrefix = "http://";

    /// <summary>
    /// Parses a raw catch-all route value into an absolute URL.
    /// </summary>
    public static bool TryNormalize(
        string? rawTarget,
        [NotNullWhen(true)] out Uri? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            error = "Hedef swagger adresi boş.";
            return false;
        }

        var candidate = rawTarget.Trim();

        // The catch-all value arrives without a leading slash, but a direct link or a
        // redirect may still carry one.
        candidate = candidate.TrimStart('/');

        // Repair "https:/host" produced by proxies that collapse duplicate slashes.
        if (candidate.StartsWith("https:/", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = string.Concat(HttpsPrefix, candidate.AsSpan("https:/".Length));
        }
        else if (candidate.StartsWith("http:/", StringComparison.OrdinalIgnoreCase) &&
                 !candidate.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = string.Concat(HttpPrefix, candidate.AsSpan("http:/".Length));
        }

        var hadExplicitScheme =
            candidate.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase);

        if (!hadExplicitScheme)
        {
            // Any other scheme is rejected outright. Prefixing it with https:// instead
            // would smuggle "ftp://host/x" through as the host "ftp" with a strange path.
            var schemeMatch = SchemePrefix().Match(candidate);
            if (schemeMatch.Success)
            {
                error = $"Desteklenmeyen protokol: {schemeMatch.Groups[1].Value}. Yalnızca http ve https kabul edilir.";
                return false;
            }

            // A scheme-less value must still look like a host, otherwise "swagger" alone
            // would silently become "https://swagger".
            var hostPart = candidate.Split('/', 2)[0];
            if (!hostPart.Contains('.') && !hostPart.Contains(':') &&
                !hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                error = $"'{rawTarget}' bir swagger adresi gibi görünmüyor.";
                return false;
            }

            candidate = HttpsPrefix + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            error = $"'{rawTarget}' geçerli bir adres değil.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Desteklenmeyen protokol: {uri.Scheme}. Yalnızca http ve https kabul edilir.";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = $"'{rawTarget}' adresinde host bulunamadı.";
            return false;
        }

        normalized = Canonicalize(uri);
        return true;
    }

    /// <summary>
    /// Produces the canonical form used for the target key: lowercase scheme and host,
    /// default port removed, fragment dropped, trailing slash trimmed.
    /// </summary>
    public static Uri Canonicalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty,
        };

        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        var path = builder.Path;
        if (path.Length > 1 && path.EndsWith('/'))
        {
            builder.Path = path.TrimEnd('/');
        }

        return builder.Uri;
    }

    /// <summary>
    /// Renders an absolute URL back into the scheme-less form used in dashboard links.
    /// </summary>
    public static string ToRouteTail(Uri uri)
    {
        var canonical = Canonicalize(uri);
        var tail = canonical.Authority + canonical.PathAndQuery;

        if (canonical.Scheme == Uri.UriSchemeHttp)
        {
            // http is unusual enough that it must stay explicit, otherwise the link would
            // silently upgrade to https on the next visit.
            return HttpPrefix + tail;
        }

        return tail.TrimEnd('/');
    }

    /// <summary>
    /// Matches a leading "scheme:" so unsupported protocols can be named in the error.
    /// Dots are excluded from the scheme and a following digit is rejected, so
    /// "api.company.com:8443" reads as a host and port rather than as a scheme.
    /// </summary>
    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+\-]*):(?![0-9])", RegexOptions.None)]
    private static partial Regex SchemePrefix();
}

using System.Text;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// Assembles the outbound request from the stored operation plus the values the user typed.
/// </summary>
/// <remarks>
/// This is the single place a target URL is constructed, and it always starts from the
/// server side base URL and the operation's own path template. The caller supplies values
/// for placeholders, never the path itself, which is what keeps the proxy from being usable
/// as a general purpose forwarder.
/// </remarks>
public static class TargetRequestBuilder
{
    /// <summary>Headers that describe a single hop and must not be forwarded.</summary>
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
        "Content-Length",
        "Expect",
    };

    public static bool IsHopByHop(string headerName) => HopByHopHeaders.Contains(headerName);

    public static BuildResult Build(
        string baseUrl,
        DashboardOperation operation,
        ProxyRequest request,
        ApiCredential? credential)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return BuildResult.Fail("API için taban adres (BaseUrl) tanımlı değil.");
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            return BuildResult.Fail($"Taban adres geçersiz: {baseUrl}");
        }

        var pathResult = SubstitutePath(operation, request.PathParameters);
        if (pathResult.Error is not null)
        {
            return BuildResult.Fail(pathResult.Error);
        }

        var builder = new UriBuilder(new Uri(baseUri, pathResult.Path));
        var query = BuildQuery(request.QueryParameters, credential);
        if (query.Length > 0)
        {
            builder.Query = query;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(name) || IsHopByHop(name))
            {
                continue;
            }

            headers[name] = value;
        }

        if (request.Cookies.Count > 0)
        {
            headers["Cookie"] = string.Join("; ", request.Cookies.Select(c => $"{c.Key}={c.Value}"));
        }

        ApplyCredential(headers, credential);

        return new BuildResult(true, builder.Uri, operation.Method, headers, null);
    }

    private static void ApplyCredential(IDictionary<string, string> headers, ApiCredential? credential)
    {
        if (credential is null)
        {
            return;
        }

        switch (credential.Kind)
        {
            case ApiAuthKind.Bearer when !string.IsNullOrWhiteSpace(credential.Secret):
                headers["Authorization"] = credential.Secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? credential.Secret
                    : "Bearer " + credential.Secret;
                break;

            case ApiAuthKind.Basic when !string.IsNullOrWhiteSpace(credential.UserName):
                var raw = $"{credential.UserName}:{credential.Secret}";
                headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                break;

            case ApiAuthKind.ApiKey when
                !string.IsNullOrWhiteSpace(credential.ParameterName) &&
                !string.IsNullOrWhiteSpace(credential.Secret) &&
                !string.Equals(credential.ParameterIn, "query", StringComparison.OrdinalIgnoreCase):
                headers[credential.ParameterName] = credential.Secret;
                break;
        }
    }

    private static string BuildQuery(
        IReadOnlyCollection<KeyValuePair<string, string>> queryParameters,
        ApiCredential? credential)
    {
        var parts = new List<string>();

        foreach (var (key, value) in queryParameters)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value ?? string.Empty)}");
        }

        if (credential is { Kind: ApiAuthKind.ApiKey } &&
            string.Equals(credential.ParameterIn, "query", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(credential.ParameterName) &&
            !string.IsNullOrWhiteSpace(credential.Secret))
        {
            parts.Add($"{Uri.EscapeDataString(credential.ParameterName)}={Uri.EscapeDataString(credential.Secret)}");
        }

        return string.Join("&", parts);
    }

    /// <summary>
    /// Replaces {placeholders} in the operation path with the supplied values. Values are
    /// escaped as path segments so that a value containing a slash cannot walk the path.
    /// </summary>
    private static (string Path, string? Error) SubstitutePath(
        DashboardOperation operation,
        IReadOnlyDictionary<string, string> pathParameters)
    {
        var template = operation.Path;
        var builder = new StringBuilder();
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open);
            if (close < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);

            var name = template[(open + 1)..close];

            // OpenAPI allows a modifier suffix such as {id*}; the name is what matters.
            name = name.TrimEnd('*', '+', '#');

            if (!pathParameters.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
            {
                return (string.Empty, $"Zorunlu path parametresi girilmedi: {name}");
            }

            builder.Append(Uri.EscapeDataString(value));
            index = close + 1;
        }

        return (builder.ToString().TrimStart('/'), null);
    }

    public record BuildResult(
        bool Success,
        Uri? Uri,
        string Method,
        Dictionary<string, string> Headers,
        string? Error)
    {
        public static BuildResult Fail(string error) =>
            new(false, null, string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), error);
    }
}

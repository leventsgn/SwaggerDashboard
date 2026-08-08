namespace SwaggerDashboard.Application.Routing;

/// <summary>
/// Path prefixes the catch-all dashboard route must never swallow.
/// </summary>
/// <remarks>
/// The prefixed-URL model maps <c>/{**target}</c> onto the whole site, so without this list
/// registering an alias called "admin" would make the management panel unreachable.
/// </remarks>
public static class ReservedRoutes
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "account",
        "login",
        "logout",
        "api",
        "proxy",
        "health",
        "healthz",
        "error",
        "_blazor",
        "_framework",
        "_content",
        "css",
        "js",
        "lib",
        "images",
        "favicon.ico",
        "robots.txt",
        "swagger",
        "openapi",
        "endpoint",
        "apis",
    };

    public static bool IsReserved(string? firstSegment) =>
        !string.IsNullOrWhiteSpace(firstSegment) && Reserved.Contains(firstSegment);

    /// <summary>
    /// Validates a user chosen short alias. Aliases live in the same namespace as the
    /// application's own paths, so they must be a single safe segment.
    /// </summary>
    public static bool IsValidAlias(string? alias, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(alias))
        {
            error = "Route adı boş olamaz.";
            return false;
        }

        if (alias.Length > 64)
        {
            error = "Route adı en fazla 64 karakter olabilir.";
            return false;
        }

        foreach (var ch in alias)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_')
            {
                error = "Route adı yalnızca harf, rakam, tire ve alt çizgi içerebilir.";
                return false;
            }
        }

        if (alias.Contains('.'))
        {
            error = "Route adı nokta içeremez; nokta içeren değerler host adresi olarak yorumlanır.";
            return false;
        }

        if (IsReserved(alias))
        {
            error = $"'{alias}' uygulama tarafından kullanılan bir yol adı.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when the incoming route tail looks like a target URL rather than an alias.
    /// A dot or an explicit scheme marks it as a host.
    /// </summary>
    public static bool LooksLikeTargetUrl(string routeTail)
    {
        if (string.IsNullOrWhiteSpace(routeTail))
        {
            return false;
        }

        if (routeTail.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            routeTail.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstSegment = routeTail.TrimStart('/').Split('/', 2)[0];
        return firstSegment.Contains('.') || firstSegment.Contains(':');
    }
}

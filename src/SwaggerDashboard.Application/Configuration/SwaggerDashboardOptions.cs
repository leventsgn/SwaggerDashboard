namespace SwaggerDashboard.Application.Configuration;

public class SwaggerDashboardOptions
{
    public const string SectionName = "SwaggerDashboard";

    public OutboundOptions Outbound { get; set; } = new();

    public ProvisioningOptions Provisioning { get; set; } = new();

    public LoggingOptions Logging { get; set; } = new();

    public CacheOptions Cache { get; set; } = new();
}

public class OutboundOptions
{
    /// <summary>
    /// Host suffixes the platform may reach, e.g. "company.com" or "api.company.com".
    /// A leading "*." is accepted and ignored. Empty means nothing is reachable unless
    /// <see cref="AllowAnyHost"/> is set.
    /// </summary>
    public List<string> AllowedHostSuffixes { get; set; } = [];

    /// <summary>
    /// Disables the host allow list. Intended for local development only; the platform
    /// refuses to start with this enabled outside the Development environment.
    /// </summary>
    public bool AllowAnyHost { get; set; }

    /// <summary>Permits plain http targets. Off by default.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Permits loopback and private range targets. Required for local development against
    /// an API on localhost, and refused outside the Development environment.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRedirects { get; set; } = 3;

    /// <summary>Hard cap on a downloaded swagger document.</summary>
    public int MaxDocumentBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Hard cap on a proxied response body.</summary>
    public int MaxProxyResponseBytes { get; set; } = 16 * 1024 * 1024;
}

public class ProvisioningOptions
{
    /// <summary>
    /// Whether visiting an unknown swagger URL may create an API definition. When false
    /// only an administrator can register APIs through the management panel.
    /// </summary>
    public bool AutoProvisionOnFirstVisit { get; set; } = true;

    /// <summary>Provisioning attempts allowed per user per hour.</summary>
    public int MaxProvisionsPerUserPerHour { get; set; } = 20;

    /// <summary>
    /// Relative paths probed when the pasted URL is a swagger UI page rather than the
    /// OpenAPI document itself. Tried in order against the directory of the pasted URL
    /// and then against the host root.
    /// </summary>
    public List<string> DocumentProbePaths { get; set; } =
    [
        "/swagger/v1/swagger.json",
        "/swagger/swagger.json",
        "/openapi/v1.json",
        "/openapi.json",
        "/swagger/v1/swagger.yaml",
    ];
}

public class LoggingOptions
{
    /// <summary>
    /// Whether request and response bodies are written to ApiRequestLogs. Bodies routinely
    /// contain personal data, so this is opt-in.
    /// </summary>
    public bool PersistBodies { get; set; }

    public int MaxLoggedBodyChars { get; set; } = 8 * 1024;

    /// <summary>Days a request log row is kept; rows older than this are purged.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Header names replaced with a mask before persisting.</summary>
    public List<string> MaskedHeaders { get; set; } =
    [
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "Api-Key",
        "Client-Secret",
        "Password",
        "Proxy-Authorization",
    ];
}

public class CacheOptions
{
    public int DashboardMinutes { get; set; } = 720;

    public int RouteResolutionMinutes { get; set; } = 60;
}

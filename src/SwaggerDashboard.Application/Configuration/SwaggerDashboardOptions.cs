namespace SwaggerDashboard.Application.Configuration;

public class SwaggerDashboardOptions
{
    public const string SectionName = "SwaggerDashboard";

    public OutboundOptions Outbound { get; set; } = new();

    public ProvisioningOptions Provisioning { get; set; } = new();

    public LoggingOptions Logging { get; set; } = new();

    public CacheOptions Cache { get; set; } = new();

    public HostingOptions Hosting { get; set; } = new();

    public AccessOptions Access { get; set; } = new();
}

public class AccessOptions
{
    /// <summary>
    /// Require a signed-in user before any dashboard is shown.
    /// </summary>
    /// <remarks>
    /// Off by default, which suits a platform reachable only from inside the corporate
    /// network: anyone who can reach it may read the dashboards, and only the execute and
    /// register actions need a role. Turn it on whenever the platform is reachable from the
    /// public internet, otherwise the endpoint list of every registered internal API is
    /// readable by anyone who learns a URL.
    /// </remarks>
    public bool RequireAuthenticationToView { get; set; }
}

public class HostingOptions
{
    /// <summary>
    /// Trust the X-Forwarded-* headers of the reverse proxy in front of the application.
    /// </summary>
    /// <remarks>
    /// Required on every platform that terminates TLS at the edge and forwards plain HTTP
    /// (Fly, Render, Railway, App Service, nginx). Without it the application believes the
    /// request arrived over HTTP and the HTTPS redirect bounces forever, so the site never
    /// loads at all. Enable it only when a proxy really is in front: the headers are
    /// accepted from any peer, so a directly reachable application would let callers forge
    /// their own scheme and address.
    /// </remarks>
    public bool BehindReverseProxy { get; set; }

    /// <summary>
    /// Directory the data protection keys are written to.
    /// </summary>
    /// <remarks>
    /// A container without this regenerates its keys on every start, which invalidates
    /// every sign-in cookie and antiforgery token: users are signed out and login forms
    /// break on each deploy. Point it at persistent storage.
    /// </remarks>
    public string? DataProtectionKeyPath { get; set; }
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
    /// How often every active API's swagger document is re-checked in the background.
    /// Zero or less turns the scheduled check off.
    /// </summary>
    /// <remarks>
    /// Off by default. The check reaches out to every registered target on a timer, and a
    /// platform should not start making outbound calls nobody asked for; an operator who
    /// wants documents to track upstream changes on their own turns it on deliberately.
    /// </remarks>
    public int AutoRefreshHours { get; set; }

    /// <summary>
    /// Pause between two APIs during a scheduled sweep, in seconds.
    /// </summary>
    /// <remarks>
    /// Checking fifty documents in one burst looks like a scraper to whoever is on the other
    /// end. The delay spreads the load; it costs nothing, because nobody is waiting for the
    /// result of a background check.
    /// </remarks>
    public int AutoRefreshDelaySeconds { get; set; } = 2;

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

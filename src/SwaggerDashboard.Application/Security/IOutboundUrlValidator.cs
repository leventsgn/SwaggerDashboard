using System.Net;

namespace SwaggerDashboard.Application.Security;

/// <summary>
/// Decides whether the platform may open an outbound connection to a URL.
/// </summary>
/// <remarks>
/// Because the prefixed-URL model lets any authenticated user name the target, this is the
/// control that keeps the platform from becoming an SSRF gateway. It is an interface so
/// that tests can supply DNS answers instead of resolving real names.
/// </remarks>
public interface IOutboundUrlValidator
{
    Task<OutboundValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-checks the address the socket is actually about to connect to. Called from the
    /// connect callback so that a name resolving to a public address on the first lookup
    /// cannot be re-pointed at an internal address afterwards.
    /// </summary>
    bool IsAllowedAddress(IPAddress address);
}

public record OutboundValidationResult(bool IsAllowed, string? Reason)
{
    public static OutboundValidationResult Allowed() => new(true, null);

    public static OutboundValidationResult Denied(string reason) => new(false, reason);
}

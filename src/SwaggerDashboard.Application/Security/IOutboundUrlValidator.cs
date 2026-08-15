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
    /// Checks the parts of the policy that need no name resolution: the protocol and the
    /// host allow list.
    /// </summary>
    /// <remarks>
    /// Used where an address is being saved rather than dialled. A configuration screen must
    /// reject a target the proxy will refuse later, but it must not depend on the host being
    /// resolvable right now: an environment is often configured before it exists.
    /// </remarks>
    bool IsHostAllowed(Uri uri, out string? reason);

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

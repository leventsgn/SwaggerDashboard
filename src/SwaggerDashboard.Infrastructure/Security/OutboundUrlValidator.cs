using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Security;

namespace SwaggerDashboard.Infrastructure.Security;

/// <summary>
/// Enforces the outbound policy: scheme, host allow list and address range.
/// </summary>
public class OutboundUrlValidator : IOutboundUrlValidator
{
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;
    private readonly IDnsResolver _dns;
    private readonly ILogger<OutboundUrlValidator> _logger;

    public OutboundUrlValidator(
        IOptionsMonitor<SwaggerDashboardOptions> options,
        IDnsResolver dns,
        ILogger<OutboundUrlValidator> logger)
    {
        _options = options;
        _dns = dns;
        _logger = logger;
    }

    private OutboundOptions Outbound => _options.CurrentValue.Outbound;

    public async Task<OutboundValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && Outbound.AllowInsecureHttp))
        {
            return OutboundValidationResult.Denied(
                $"Protokol '{uri.Scheme}' bu ortamda kullanılamaz. HTTPS kullanın.");
        }

        if (!IsHostAllowed(uri.Host))
        {
            return OutboundValidationResult.Denied(
                $"'{uri.Host}' izinli alan adı listesinde değil. Yöneticinizden bu adresi listeye eklemesini isteyin.");
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _dns.ResolveAsync(uri.Host, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS resolution failed for {Host}", uri.Host);
            return OutboundValidationResult.Denied($"'{uri.Host}' adresi çözümlenemedi.");
        }

        if (addresses.Count == 0)
        {
            return OutboundValidationResult.Denied($"'{uri.Host}' için IP adresi bulunamadı.");
        }

        // Every answer has to be acceptable. A name that resolves to one public and one
        // internal address must not be reachable at all.
        foreach (var address in addresses)
        {
            if (!IsAllowedAddress(address))
            {
                return OutboundValidationResult.Denied(
                    $"'{uri.Host}' iç ağdaki bir adrese ({address}) çözümleniyor.");
            }
        }

        return OutboundValidationResult.Allowed();
    }

    public bool IsAllowedAddress(IPAddress address)
    {
        if (Outbound.AllowPrivateNetworks)
        {
            return true;
        }

        return !IpAddressRules.IsPrivateOrReserved(address);
    }

    private bool IsHostAllowed(string host)
    {
        if (Outbound.AllowAnyHost)
        {
            return true;
        }

        foreach (var raw in Outbound.AllowedHostSuffixes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var suffix = raw.Trim().TrimStart('*', '.').ToLowerInvariant();
            if (suffix.Length == 0)
            {
                continue;
            }

            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A suffix entry matches subdomains, but only on a label boundary so that
            // "evil-company.com" does not match an allow list entry of "company.com".
            if (host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

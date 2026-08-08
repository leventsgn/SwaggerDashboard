using System.Net;
using System.Net.Sockets;

namespace SwaggerDashboard.Application.Security;

/// <summary>
/// Classification of IP addresses the platform must never dial when the allow list is
/// meant to keep it on the public internet.
/// </summary>
public static class IpAddressRules
{
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            return octets[0] switch
            {
                0 => true,                                  // 0.0.0.0/8 "this network"
                10 => true,                                 // 10.0.0.0/8 private
                127 => true,                                // loopback
                169 when octets[1] == 254 => true,          // 169.254.0.0/16 link local, cloud metadata
                172 when octets[1] >= 16 && octets[1] <= 31 => true, // 172.16.0.0/12 private
                192 when octets[1] == 168 => true,          // 192.168.0.0/16 private
                192 when octets[1] == 0 && octets[2] == 0 => true,   // 192.0.0.0/24 protocol assignments
                100 when octets[1] >= 64 && octets[1] <= 127 => true, // 100.64.0.0/10 carrier grade NAT
                198 when octets[1] == 18 || octets[1] == 19 => true,  // 198.18.0.0/15 benchmarking
                >= 224 => true,                             // multicast and reserved
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                address.IsIPv6UniqueLocal || address.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            return false;
        }

        // Anything that is neither IPv4 nor IPv6 is not something to dial.
        return true;
    }
}

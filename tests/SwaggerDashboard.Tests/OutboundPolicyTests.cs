using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Infrastructure.Security;
using Xunit;

namespace SwaggerDashboard.Tests;

public class OutboundPolicyTests
{
    private static OutboundUrlValidator CreateValidator(
        OutboundOptions options,
        params (string Host, string[] Addresses)[] dns)
    {
        var monitor = new StaticOptionsMonitor(new SwaggerDashboardOptions { Outbound = options });
        var resolver = new StubDnsResolver(dns);

        return new OutboundUrlValidator(monitor, resolver, NullLogger<OutboundUrlValidator>.Instance);
    }

    private static OutboundOptions Strict(params string[] allowed) => new()
    {
        AllowedHostSuffixes = allowed.ToList(),
        AllowAnyHost = false,
        AllowInsecureHttp = false,
        AllowPrivateNetworks = false,
    };

    [Fact]
    public async Task Allows_an_allow_listed_host_resolving_to_a_public_address()
    {
        var validator = CreateValidator(Strict("company.com"), ("api.company.com", ["93.184.216.34"]));

        var result = await validator.ValidateAsync(new Uri("https://api.company.com/swagger/v1/swagger.json"));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Blocks_a_host_that_is_not_on_the_allow_list()
    {
        var validator = CreateValidator(Strict("company.com"), ("evil.example", ["93.184.216.34"]));

        var result = await validator.ValidateAsync(new Uri("https://evil.example/swagger.json"));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Suffix_matching_respects_label_boundaries()
    {
        // "evil-company.com" ends with "company.com" as a string but is a different domain.
        var validator = CreateValidator(Strict("company.com"), ("evil-company.com", ["93.184.216.34"]));

        var result = await validator.ValidateAsync(new Uri("https://evil-company.com/swagger.json"));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Blocks_plain_http_unless_it_is_explicitly_enabled()
    {
        var validator = CreateValidator(Strict("company.com"), ("api.company.com", ["93.184.216.34"]));

        var result = await validator.ValidateAsync(new Uri("http://api.company.com/swagger.json"));

        Assert.False(result.IsAllowed);
        Assert.Contains("HTTPS", result.Reason);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.5")]
    [InlineData("172.16.0.9")]
    [InlineData("169.254.169.254")] // cloud instance metadata
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    public async Task Blocks_an_allow_listed_host_that_points_at_an_internal_address(string address)
    {
        var validator = CreateValidator(Strict("company.com"), ("api.company.com", [address]));

        var result = await validator.ValidateAsync(new Uri("https://api.company.com/swagger.json"));

        Assert.False(result.IsAllowed);
        Assert.Contains("iç ağ", result.Reason);
    }

    [Fact]
    public async Task Blocks_when_only_one_of_several_answers_is_internal()
    {
        var validator = CreateValidator(
            Strict("company.com"),
            ("api.company.com", ["93.184.216.34", "10.0.0.1"]));

        var result = await validator.ValidateAsync(new Uri("https://api.company.com/swagger.json"));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Blocks_ipv6_loopback_and_unique_local_addresses()
    {
        var validator = CreateValidator(Strict("company.com"), ("api.company.com", ["::1"]));
        Assert.False((await validator.ValidateAsync(new Uri("https://api.company.com/x"))).IsAllowed);

        var unique = CreateValidator(Strict("company.com"), ("api.company.com", ["fd00::1"]));
        Assert.False((await unique.ValidateAsync(new Uri("https://api.company.com/x"))).IsAllowed);
    }

    [Fact]
    public async Task Blocks_an_ipv4_mapped_ipv6_loopback()
    {
        // ::ffff:127.0.0.1 is loopback wearing an IPv6 costume.
        var validator = CreateValidator(Strict("company.com"), ("api.company.com", ["::ffff:127.0.0.1"]));

        Assert.False((await validator.ValidateAsync(new Uri("https://api.company.com/x"))).IsAllowed);
    }

    [Fact]
    public void The_connect_time_check_rejects_a_rebound_address()
    {
        var validator = CreateValidator(Strict("company.com"));

        // Same predicate the socket connect callback applies, which is what closes the
        // window between the policy check and the actual connection.
        Assert.False(validator.IsAllowedAddress(IPAddress.Parse("10.0.0.5")));
        Assert.True(validator.IsAllowedAddress(IPAddress.Parse("93.184.216.34")));
    }

    [Fact]
    public async Task Development_settings_can_open_the_policy_deliberately()
    {
        var options = new OutboundOptions
        {
            AllowAnyHost = true,
            AllowInsecureHttp = true,
            AllowPrivateNetworks = true,
        };

        var validator = CreateValidator(options, ("localhost", ["127.0.0.1"]));

        Assert.True((await validator.ValidateAsync(new Uri("http://localhost:5000/swagger.json"))).IsAllowed);
    }

    private sealed class StubDnsResolver : IDnsResolver
    {
        private readonly Dictionary<string, IReadOnlyList<IPAddress>> _answers;

        public StubDnsResolver((string Host, string[] Addresses)[] answers)
        {
            _answers = answers.ToDictionary(
                a => a.Host,
                a => (IReadOnlyList<IPAddress>)a.Addresses.Select(IPAddress.Parse).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(_answers.TryGetValue(host, out var addresses) ? addresses : []);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public StaticOptionsMonitor(SwaggerDashboardOptions value) => CurrentValue = value;

        public SwaggerDashboardOptions CurrentValue { get; }

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}

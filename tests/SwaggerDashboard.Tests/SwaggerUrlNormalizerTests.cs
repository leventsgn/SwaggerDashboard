using SwaggerDashboard.Application.Routing;
using Xunit;

namespace SwaggerDashboard.Tests;

public class SwaggerUrlNormalizerTests
{
    [Theory]
    [InlineData("api.company.com/swagger", "https://api.company.com/swagger")]
    [InlineData("api.company.com/swagger/index.html", "https://api.company.com/swagger/index.html")]
    [InlineData("https://api.company.com/swagger", "https://api.company.com/swagger")]
    [InlineData("http://api.company.com/swagger", "http://api.company.com/swagger")]
    [InlineData("API.Company.COM/Swagger", "https://api.company.com/Swagger")]
    public void Normalizes_the_common_forms(string input, string expected)
    {
        Assert.True(SwaggerUrlNormalizer.TryNormalize(input, out var uri, out _));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Fact]
    public void Repairs_a_scheme_whose_double_slash_was_collapsed_by_a_proxy()
    {
        // nginx collapses "//" in a path by default, turning an embedded https:// into https:/.
        Assert.True(SwaggerUrlNormalizer.TryNormalize("https:/api.company.com/swagger", out var uri, out _));
        Assert.Equal("https://api.company.com/swagger", uri.AbsoluteUri);
    }

    [Fact]
    public void Drops_the_default_port_the_fragment_and_a_trailing_slash()
    {
        Assert.True(SwaggerUrlNormalizer.TryNormalize(
            "https://api.company.com:443/swagger/#/Customers", out var uri, out _));

        Assert.Equal("https://api.company.com/swagger", uri.AbsoluteUri);
    }

    [Fact]
    public void Keeps_a_non_default_port()
    {
        Assert.True(SwaggerUrlNormalizer.TryNormalize("api.company.com:8443/swagger", out var uri, out _));
        Assert.Equal("https://api.company.com:8443/swagger", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("customer-api")]
    [InlineData("ftp://api.company.com/swagger")]
    public void Rejects_values_that_are_not_swagger_addresses(string input)
    {
        Assert.False(SwaggerUrlNormalizer.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Round_trips_through_the_scheme_less_route_form()
    {
        Assert.True(SwaggerUrlNormalizer.TryNormalize("api.company.com/swagger/v1/swagger.json", out var uri, out _));

        var tail = SwaggerUrlNormalizer.ToRouteTail(uri);
        Assert.Equal("api.company.com/swagger/v1/swagger.json", tail);

        Assert.True(SwaggerUrlNormalizer.TryNormalize(tail, out var again, out _));
        Assert.Equal(uri.AbsoluteUri, again.AbsoluteUri);
    }

    [Fact]
    public void Keeps_http_explicit_in_the_route_form_so_it_does_not_silently_upgrade()
    {
        Assert.True(SwaggerUrlNormalizer.TryNormalize("http://api.company.com/swagger", out var uri, out _));
        Assert.Equal("http://api.company.com/swagger", SwaggerUrlNormalizer.ToRouteTail(uri));
    }
}


/// <summary>
/// Which addresses are tried when the user pastes a Swagger UI page rather than the document.
/// </summary>
public class DocumentDiscoveryTests
{
    private static readonly string[] Probes =
    [
        "/swagger/v1/swagger.json",
        "/swagger/swagger.json",
        "/openapi/v1.json",
        "/openapi.json",
        "/swagger/v1/swagger.yaml",
    ];

    private static List<string> Candidates(string pastedUrl) =>
        SwaggerDashboard.Infrastructure.Services.OpenApiDocumentService
            .BuildCandidates(new Uri(pastedUrl), Probes)
            .Select(u => u.AbsolutePath)
            .ToList();

    [Fact]
    public void The_document_next_to_the_ui_page_is_tried()
    {
        Assert.Contains(
            "/swagger/v1/swagger.json",
            Candidates("https://api.company.com/swagger/index.html"));
    }

    [Fact]
    public void An_api_behind_a_path_prefix_is_found_without_doubling_the_segment()
    {
        // A reverse proxy in front of several services publishes each one under its own
        // prefix, so the UI page is at /Denemeapi/swagger/index.html and the document at
        // /Denemeapi/swagger/v1/swagger.json. Joining the probe to the directory doubled
        // "swagger", and the fallback anchored at the host root looked on the wrong
        // application, so the API could not be registered from the address the user has.
        var candidates = Candidates("https://dasist.cen.deneme.local/Denemeapi/swagger/index.html");

        Assert.Contains("/Denemeapi/swagger/v1/swagger.json", candidates);
        Assert.Contains("/Denemeapi/swagger/v1/swagger.yaml", candidates);
    }

    [Fact]
    public void A_prefixed_openapi_layout_is_covered_too()
    {
        Assert.Contains(
            "/Denemeapi/openapi/v1.json",
            Candidates("https://dasist.cen.deneme.local/Denemeapi/openapi/index.html"));
    }

    [Fact]
    public void The_pasted_address_is_always_tried_first()
    {
        var candidates = Candidates("https://api.company.com/custom/place/openapi.json");

        Assert.Equal("/custom/place/openapi.json", candidates[0]);
    }

    [Fact]
    public void The_same_address_is_never_requested_twice()
    {
        // Every candidate is a request to somebody else's server, and several rules reach
        // the same address for a UI page sitting directly under /swagger.
        var candidates = Candidates("https://api.company.com/swagger/index.html");

        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }
}

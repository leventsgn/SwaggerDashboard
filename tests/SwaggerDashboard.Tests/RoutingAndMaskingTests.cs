using SwaggerDashboard.Application.Execution;
using SwaggerDashboard.Application.Routing;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Web.Components.Pages;
using Xunit;

namespace SwaggerDashboard.Tests;

public class ReservedRouteTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("login")]
    [InlineData("_blazor")]
    [InlineData("health")]
    public void Rejects_an_alias_that_would_shadow_an_application_path(string alias)
    {
        Assert.False(ReservedRoutes.IsValidAlias(alias, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_an_alias_containing_a_dot_because_it_would_read_as_a_host()
    {
        Assert.False(ReservedRoutes.IsValidAlias("api.company.com", out _));
    }

    [Theory]
    [InlineData("customer-api")]
    [InlineData("orders_v2")]
    [InlineData("billing")]
    public void Accepts_a_plain_alias(string alias)
    {
        Assert.True(ReservedRoutes.IsValidAlias(alias, out _));
    }

    [Theory]
    [InlineData("api.company.com/swagger", true)]
    [InlineData("https://api.company.com/swagger", true)]
    [InlineData("localhost:5000/swagger", true)]
    [InlineData("customer-api", false)]
    public void Distinguishes_a_target_url_from_a_short_alias(string routeTail, bool expected)
    {
        Assert.Equal(expected, ReservedRoutes.LooksLikeTargetUrl(routeTail));
    }
}

public class SlugTests
{
    [Fact]
    public void Prefers_the_operation_id_when_it_is_present()
    {
        var slug = SlugGenerator.Create("GET", "/customers/{id}", "getCustomerById", new HashSet<string>());

        Assert.Equal("get-customer-by-id", slug);
    }

    [Fact]
    public void Falls_back_to_method_and_path_without_an_operation_id()
    {
        var slug = SlugGenerator.Create("POST", "/customers/{id}/documents", null, new HashSet<string>());

        Assert.Equal("post-customers-id-documents", slug);
    }

    [Fact]
    public void Disambiguates_collisions_with_a_numeric_suffix()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("ping", SlugGenerator.Create("GET", "/ping", "ping", taken));
        Assert.Equal("ping-2", SlugGenerator.Create("POST", "/ping", "ping", taken));
        Assert.Equal("ping-3", SlugGenerator.Create("PUT", "/ping", "ping", taken));
    }

    [Fact]
    public void Strips_characters_that_are_unsafe_in_a_url()
    {
        var slug = SlugGenerator.Create("GET", "/x", "Get Customer (v2)/Detail", new HashSet<string>());

        Assert.Matches("^[a-z0-9-]+$", slug);
    }
}

public class EndpointLinkTests
{
    [Fact]
    public void Splits_the_endpoint_suffix_off_a_shareable_link()
    {
        var (route, slug) = Dashboard.SplitEndpointLink("api.company.com/swagger/endpoint/get-customer-by-id");

        Assert.Equal("api.company.com/swagger", route);
        Assert.Equal("get-customer-by-id", slug);
    }

    [Fact]
    public void Leaves_a_plain_api_route_untouched()
    {
        var (route, slug) = Dashboard.SplitEndpointLink("api.company.com/swagger");

        Assert.Equal("api.company.com/swagger", route);
        Assert.Null(slug);
    }

    [Fact]
    public void Uses_the_last_marker_so_an_api_path_containing_endpoint_still_resolves()
    {
        var (route, slug) = Dashboard.SplitEndpointLink("api.company.com/endpoint/docs/endpoint/list-items");

        Assert.Equal("api.company.com/endpoint/docs", route);
        Assert.Equal("list-items", slug);
    }

    [Fact]
    public void Ignores_a_trailing_marker_with_no_slug()
    {
        var (route, slug) = Dashboard.SplitEndpointLink("api.company.com/swagger/endpoint/");

        Assert.Null(slug);
        Assert.Equal("api.company.com/swagger/endpoint/", route);
    }
}

public class MaskingTests
{
    private static readonly SensitiveDataMasker Masker = new(
        ["Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Client-Secret", "Password"]);

    [Fact]
    public void Keeps_the_auth_scheme_visible_but_removes_the_secret()
    {
        Assert.Equal("Bearer ***", Masker.MaskValue("Authorization", "Bearer eyJhbGciOi.secret.value"));
        Assert.Equal("Basic ***", Masker.MaskValue("Authorization", "Basic dXNlcjpwYXNz"));
    }

    [Fact]
    public void Masks_api_keys_and_cookies_entirely()
    {
        Assert.Equal("***", Masker.MaskValue("X-Api-Key", "abc123"));
        Assert.Equal("***", Masker.MaskValue("Cookie", "session=abc"));
    }

    [Fact]
    public void Leaves_ordinary_headers_alone()
    {
        Assert.Equal("application/json", Masker.MaskValue("Content-Type", "application/json"));
    }

    [Fact]
    public void Masks_across_a_whole_header_collection_case_insensitively()
    {
        var masked = Masker.MaskHeaders(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = "Bearer abc",
            ["Accept"] = "*/*",
        });

        Assert.Equal("Bearer ***", masked["Authorization"]);
        Assert.Equal("*/*", masked["Accept"]);
    }
}

public class CodeSnippetTests
{
    private static readonly ExecutedCall Call = new(
        "POST",
        "https://api.company.com/v1/customers",
        new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer super-secret-token",
            ["X-Api-Key"] = "another-secret",
            ["Accept"] = "application/json",
        },
        """{"name":"Ada"}""",
        "application/json");

    [Fact]
    public void Never_writes_a_real_secret_into_a_snippet()
    {
        foreach (var snippet in new[]
                 {
                     CodeSnippetGenerator.ToCurl(Call),
                     CodeSnippetGenerator.ToCSharp(Call),
                     CodeSnippetGenerator.ToJavaScript(Call),
                 })
        {
            Assert.DoesNotContain("super-secret-token", snippet);
            Assert.DoesNotContain("another-secret", snippet);
            Assert.Contains(CodeSnippetGenerator.SecretPlaceholder, snippet);
        }
    }

    [Fact]
    public void Includes_the_method_url_and_body()
    {
        var curl = CodeSnippetGenerator.ToCurl(Call);

        Assert.Contains("curl -X POST", curl);
        Assert.Contains("https://api.company.com/v1/customers", curl);
        Assert.Contains("\"name\":\"Ada\"", curl);
        Assert.Contains("Bearer " + CodeSnippetGenerator.SecretPlaceholder, curl);
    }

    [Fact]
    public void Keeps_non_sensitive_headers_readable()
    {
        Assert.Contains("application/json", CodeSnippetGenerator.ToJavaScript(Call));
    }
}

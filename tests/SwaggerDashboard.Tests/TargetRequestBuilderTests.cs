using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;
using Xunit;

namespace SwaggerDashboard.Tests;

public class TargetRequestBuilderTests
{
    private static DashboardOperation Operation(string method = "GET", string path = "/customers/{id}") => new()
    {
        Slug = "op",
        Method = method,
        Path = path,
    };

    private static ProxyRequest Request(Action<ProxyRequest>? configure = null)
    {
        var request = new ProxyRequest { ApiDefinitionId = 1, OperationSlug = "op" };
        configure?.Invoke(request);
        return request;
    }

    [Fact]
    public void Builds_the_url_from_the_stored_base_and_the_operation_template()
    {
        var request = Request(r => r.PathParameters["id"] = "42");

        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, null);

        Assert.True(result.Success);
        Assert.Equal("https://api.company.com/v1/customers/42", result.Uri!.AbsoluteUri);
    }

    [Fact]
    public void Escapes_a_path_value_so_it_cannot_walk_out_of_its_segment()
    {
        var request = Request(r => r.PathParameters["id"] = "../../admin/secrets");

        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, null);

        Assert.True(result.Success);
        Assert.StartsWith("https://api.company.com/v1/customers/", result.Uri!.AbsoluteUri);
        Assert.DoesNotContain("admin/secrets", result.Uri.AbsoluteUri);
    }

    [Fact]
    public void Reports_a_missing_required_path_value_rather_than_calling_a_broken_url()
    {
        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), Request(), null);

        Assert.False(result.Success);
        Assert.Contains("id", result.Error);
    }

    [Fact]
    public void Repeats_a_query_key_for_array_parameters_and_escapes_values()
    {
        var request = Request(r =>
        {
            r.PathParameters["id"] = "1";
            r.QueryParameters.Add(new("tag", "a b"));
            r.QueryParameters.Add(new("tag", "c&d"));
        });

        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, null);

        Assert.Equal("?tag=a%20b&tag=c%26d", result.Uri!.Query);
    }

    [Fact]
    public void Drops_hop_by_hop_headers()
    {
        var request = Request(r =>
        {
            r.PathParameters["id"] = "1";
            r.Headers["Connection"] = "keep-alive";
            r.Headers["Transfer-Encoding"] = "chunked";
            r.Headers["Proxy-Authorization"] = "Basic abc";
            r.Headers["Host"] = "spoofed.example";
            r.Headers["X-Correlation-Id"] = "abc-123";
        });

        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, null);

        Assert.DoesNotContain("Connection", result.Headers.Keys);
        Assert.DoesNotContain("Transfer-Encoding", result.Headers.Keys);
        Assert.DoesNotContain("Proxy-Authorization", result.Headers.Keys);
        Assert.DoesNotContain("Host", result.Headers.Keys);
        Assert.Equal("abc-123", result.Headers["X-Correlation-Id"]);
    }

    [Fact]
    public void Applies_a_bearer_credential_without_doubling_the_scheme()
    {
        var request = Request(r => r.PathParameters["id"] = "1");
        var credential = new ApiCredential { Kind = ApiAuthKind.Bearer, Secret = "abc.def" };

        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, credential);
        Assert.Equal("Bearer abc.def", result.Headers["Authorization"]);

        var prefixed = new ApiCredential { Kind = ApiAuthKind.Bearer, Secret = "Bearer abc.def" };
        var second = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, prefixed);
        Assert.Equal("Bearer abc.def", second.Headers["Authorization"]);
    }

    [Fact]
    public void Encodes_basic_credentials()
    {
        var request = Request(r => r.PathParameters["id"] = "1");
        var credential = new ApiCredential { Kind = ApiAuthKind.Basic, UserName = "alex", Secret = "secret" };

        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, credential);

        Assert.Equal("Basic " + Convert.ToBase64String("alex:secret"u8.ToArray()), result.Headers["Authorization"]);
    }

    [Fact]
    public void Places_an_api_key_in_the_header_or_the_query_as_declared()
    {
        var request = Request(r => r.PathParameters["id"] = "1");

        var header = new ApiCredential
        {
            Kind = ApiAuthKind.ApiKey, ParameterName = "X-Api-Key", ParameterIn = "header", Secret = "k1",
        };
        var headerResult = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, header);
        Assert.Equal("k1", headerResult.Headers["X-Api-Key"]);

        var query = new ApiCredential
        {
            Kind = ApiAuthKind.ApiKey, ParameterName = "code", ParameterIn = "query", Secret = "k2",
        };
        var queryResult = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, query);
        Assert.Contains("code=k2", queryResult.Uri!.Query);
        Assert.DoesNotContain("code", queryResult.Headers.Keys);
    }

    [Fact]
    public void Joins_cookies_into_a_single_header()
    {
        var request = Request(r =>
        {
            r.PathParameters["id"] = "1";
            r.Cookies["session"] = "abc";
            r.Cookies["locale"] = "tr";
        });

        var result = TargetRequestBuilder.Build("https://api.company.com/v1", Operation(), request, null);

        Assert.Equal("session=abc; locale=tr", result.Headers["Cookie"]);
    }

    [Fact]
    public void Rejects_an_unusable_base_url()
    {
        var result = TargetRequestBuilder.Build("not a url", Operation(), Request(), null);

        Assert.False(result.Success);
    }

    [Fact]
    public void Keeps_the_base_path_when_the_operation_path_is_rooted()
    {
        var request = Request(r => r.PathParameters["id"] = "7");

        var result = TargetRequestBuilder.Build("https://api.company.com/api/v2", Operation(), request, null);

        Assert.Equal("https://api.company.com/api/v2/customers/7", result.Uri!.AbsoluteUri);
    }
}

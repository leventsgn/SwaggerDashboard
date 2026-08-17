using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;
using Xunit;

namespace SwaggerDashboard.Tests;

public class RequestReplayTests
{
    private static DashboardOperation Operation(
        string path = "/customers/{id}",
        params DashboardParameter[] parameters) => new()
    {
        Slug = "op",
        Method = "GET",
        Path = path,
        Parameters = [.. parameters],
    };

    private static DashboardParameter Parameter(string name, string location) => new()
    {
        Name = name,
        In = location,
        Schema = new FieldSchema { Type = SchemaTypes.String },
    };

    [Fact]
    public void A_path_value_is_read_back_out_of_the_logged_url()
    {
        var operation = Operation("/customers/{id}", Parameter("id", ParameterLocations.Path));

        var payload = RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/v1/customers/42", null);

        Assert.NotNull(payload);
        Assert.Equal("42", Assert.Single(payload!.Parameters["path:id"]));
    }

    [Fact]
    public void The_base_path_in_front_of_the_template_is_ignored()
    {
        // The prefix belongs to the environment, not the operation, so matching starts from
        // the end of the URL.
        var operation = Operation("/customers/{id}", Parameter("id", ParameterLocations.Path));

        var payload = RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/gateway/public/v2/customers/7", null);

        Assert.Equal("7", Assert.Single(payload!.Parameters["path:id"]));
    }

    [Fact]
    public void A_url_from_a_different_endpoint_is_refused()
    {
        // Filling the form from someone else's URL would put values in fields that never held
        // them, so a literal segment that does not match ends the attempt.
        var operation = Operation("/customers/{id}", Parameter("id", ParameterLocations.Path));

        Assert.Null(RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/v1/payments/42", null));
    }

    [Fact]
    public void A_url_shorter_than_the_template_is_refused()
    {
        var operation = Operation("/customers/{id}/documents", Parameter("id", ParameterLocations.Path));

        Assert.Null(RequestReplay.FromLoggedRequest(operation, "https://api.company.com/documents", null));
    }

    [Fact]
    public void Query_values_come_back_including_repeated_ones()
    {
        var operation = Operation(
            "/customers",
            Parameter("status", ParameterLocations.Query),
            Parameter("tags", ParameterLocations.Query));

        var payload = RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/v1/customers?status=active&tags=a&tags=b", null);

        Assert.Equal("active", Assert.Single(payload!.Parameters["query:status"]));
        Assert.Equal(["a", "b"], payload.Parameters["query:tags"]);
    }

    [Fact]
    public void A_query_value_the_document_does_not_declare_is_dropped()
    {
        // There is no field for it, so carrying it would promise a value the next run cannot
        // send.
        var operation = Operation("/customers", Parameter("status", ParameterLocations.Query));

        var payload = RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/v1/customers?status=active&debug=1", null);

        Assert.Single(payload!.Parameters);
        Assert.False(payload.Parameters.ContainsKey("query:debug"));
    }

    [Fact]
    public void An_escaped_path_value_is_decoded()
    {
        var operation = Operation("/customers/{id}", Parameter("id", ParameterLocations.Path));

        var payload = RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/v1/customers/ada%20y%C4%B1lmaz", null);

        Assert.Equal("ada yılmaz", Assert.Single(payload!.Parameters["path:id"]));
    }

    [Fact]
    public void The_logged_body_is_carried_over_unchanged()
    {
        var operation = Operation("/customers");

        var payload = RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/v1/customers", """{"ad":"Ada"}""");

        Assert.Equal("""{"ad":"Ada"}""", payload!.Body);
    }

    [Fact]
    public void A_replayed_call_fills_the_same_form_a_saved_request_would()
    {
        // History and saved requests share one payload type and one apply step; this is the
        // test that keeps the two loading paths from drifting apart.
        var operation = Operation(
            "/customers/{id}",
            Parameter("id", ParameterLocations.Path),
            Parameter("status", ParameterLocations.Query));

        var payload = RequestReplay.FromLoggedRequest(
            operation, "https://api.company.com/v1/customers/9?status=pasif", null);

        var nodes = RequestComposer.CreateParameterNodes(operation);
        RequestComposer.ApplyPayload(payload!, nodes, null);

        Assert.Equal("9", nodes["path:id"].Value);
        Assert.Equal("pasif", nodes["query:status"].Value);
        Assert.True(nodes["query:status"].Included);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bir adres değil")]
    public void An_unusable_logged_url_is_refused(string? requestUrl)
    {
        Assert.Null(RequestReplay.FromLoggedRequest(Operation("/customers"), requestUrl, null));
    }
}

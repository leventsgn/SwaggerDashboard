using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;
using Xunit;

namespace SwaggerDashboard.Tests;

public class RequestComposerTests
{
    private static DashboardOperation Operation(
        string method = "GET",
        string path = "/customers/{id}",
        List<DashboardParameter>? parameters = null,
        DashboardRequestBody? body = null) => new()
    {
        Slug = "op",
        Method = method,
        Path = path,
        Parameters = parameters ?? [],
        RequestBody = body,
    };

    private static DashboardParameter Parameter(
        string name,
        string location,
        bool required = false,
        FieldSchema? schema = null) => new()
    {
        Name = name,
        In = location,
        Required = required,
        Schema = schema ?? new FieldSchema { Type = SchemaTypes.String },
    };

    [Fact]
    public void A_generated_request_carries_a_value_for_every_required_path_parameter()
    {
        // Without this the bulk run could not call a single parameterised endpoint.
        var operation = Operation(parameters:
        [
            Parameter("id", ParameterLocations.Path, required: true,
                new FieldSchema { Type = SchemaTypes.String, Format = "uuid" }),
        ]);

        var result = RequestComposer.CreateSampleRequest(operation, 1, null, null, null);

        Assert.NotNull(result.Request);
        Assert.True(Guid.TryParse(result.Request!.PathParameters["id"], out _));
    }

    [Fact]
    public void Query_and_header_values_are_generated_and_land_in_their_own_places()
    {
        var operation = Operation(parameters:
        [
            Parameter("page", ParameterLocations.Query, schema: new FieldSchema { Type = SchemaTypes.Integer }),
            Parameter("X-Correlation-Id", ParameterLocations.Header,
                schema: new FieldSchema { Type = SchemaTypes.String, Format = "uuid" }),
        ]);

        var request = RequestComposer.CreateSampleRequest(operation, 1, null, null, null).Request;

        Assert.NotNull(request);
        Assert.Equal("page", Assert.Single(request!.QueryParameters).Key);
        Assert.True(Guid.TryParse(request.Headers["X-Correlation-Id"], out _));
    }

    [Fact]
    public void A_generated_body_follows_the_declared_schema()
    {
        var operation = Operation("POST", "/customers", body: new DashboardRequestBody
        {
            Required = true,
            Contents =
            [
                new DashboardContent
                {
                    ContentType = "application/json",
                    Schema = new FieldSchema
                    {
                        Type = SchemaTypes.Object,
                        Properties =
                        [
                            new FieldProperty
                            {
                                Name = "name",
                                Required = true,
                                Schema = new FieldSchema { Type = SchemaTypes.String },
                            },
                            new FieldProperty
                            {
                                Name = "age",
                                Required = true,
                                Schema = new FieldSchema { Type = SchemaTypes.Integer },
                            },
                        ],
                    },
                },
            ],
        });

        var request = RequestComposer.CreateSampleRequest(operation, 1, null, null, null).Request;

        Assert.NotNull(request);
        Assert.Contains("\"name\"", request!.Body);

        // The integer must not be quoted, or the target would reject its own schema.
        Assert.DoesNotContain("\"age\": \"", request.Body);
    }

    [Fact]
    public void A_file_upload_is_refused_rather_than_faked()
    {
        // There is no honest generated content for a file, and a request that pretends
        // otherwise would report a failure the user cannot act on.
        var operation = Operation("POST", "/customers/{id}/documents", body: new DashboardRequestBody
        {
            Contents =
            [
                new DashboardContent
                {
                    ContentType = "multipart/form-data",
                    Schema = new FieldSchema { Type = SchemaTypes.Object },
                },
            ],
        });

        var result = RequestComposer.CreateSampleRequest(operation, 1, null, null, null);

        Assert.Null(result.Request);
        Assert.Contains("Dosya", result.Reason);
    }

    [Fact]
    public void An_ungeneratable_required_path_parameter_stops_the_request_being_built()
    {
        // Nothing the generator offers matches this pattern, so the path template would keep
        // its placeholder and the call would go to a nonsense URL.
        var operation = Operation(parameters:
        [
            Parameter("id", ParameterLocations.Path, required: true, new FieldSchema
            {
                Type = SchemaTypes.String,
                Pattern = @"^\d{4}-\d{2}$",
            }),
        ]);

        var result = RequestComposer.CreateSampleRequest(operation, 1, null, null, null);

        Assert.Null(result.Request);
        Assert.Contains("id", result.Reason);
    }

    [Fact]
    public void An_empty_optional_parameter_is_left_out_of_the_request()
    {
        var operation = Operation(parameters: [Parameter("status", ParameterLocations.Query)]);
        var nodes = RequestComposer.CreateParameterNodes(operation);
        var request = new SwaggerDashboard.Application.Abstractions.ProxyRequest
        {
            ApiDefinitionId = 1,
            OperationSlug = "op",
        };

        Assert.True(RequestComposer.TryApplyParameters(operation, nodes, request, out _));
        Assert.Empty(request.QueryParameters);
    }

    [Fact]
    public void The_environment_and_caller_are_carried_onto_the_generated_request()
    {
        var request = RequestComposer
            .CreateSampleRequest(Operation("GET", "/customers"), 7, "Test", "42", "203.0.113.9")
            .Request;

        Assert.NotNull(request);
        Assert.Equal(7, request!.ApiDefinitionId);
        Assert.Equal("Test", request.EnvironmentName);
        Assert.Equal("42", request.UserId);
        Assert.Equal("203.0.113.9", request.ClientIp);
    }
}

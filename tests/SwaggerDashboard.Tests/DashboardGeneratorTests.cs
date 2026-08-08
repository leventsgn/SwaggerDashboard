using Microsoft.Extensions.Logging.Abstractions;
using SwaggerDashboard.Application.Dashboards;
using Xunit;

namespace SwaggerDashboard.Tests;

public class DashboardGeneratorTests
{
    private readonly DashboardGeneratorService _generator = new(NullLogger<DashboardGeneratorService>.Instance);

    private DashboardDocument Generate(string content)
    {
        var result = _generator.Generate(content);
        Assert.True(result.Success, result.Error);
        return result.Document!;
    }

    [Fact]
    public void Reads_info_servers_and_every_operation()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());

        Assert.Equal("Customer API", dashboard.Title);
        Assert.Equal("1.4.0", dashboard.Version);
        Assert.Contains("https://api.company.com/v1", dashboard.Servers);
        Assert.Equal(DashboardDocument.CurrentSchemaVersion, dashboard.SchemaVersion);

        var methods = dashboard.Operations.Select(o => o.Method).Distinct().ToList();
        Assert.Contains("GET", methods);
        Assert.Contains("POST", methods);
        Assert.Contains("PUT", methods);
        Assert.Contains("PATCH", methods);
        Assert.Contains("DELETE", methods);
    }

    [Fact]
    public void Marks_deprecated_operations()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var deleteCustomer = dashboard.Operations.Single(o => o.OperationId == "deleteCustomer");

        Assert.True(deleteCustomer.Deprecated);
    }

    [Fact]
    public void Merges_path_level_parameters_into_each_operation()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var getById = dashboard.Operations.Single(o => o.OperationId == "getCustomerById");

        var id = getById.Parameters.Single(p => p.Name == "id");
        Assert.Equal(ParameterLocations.Path, id.In);
        Assert.True(id.Required);
    }

    [Fact]
    public void Resolves_refs_and_flattens_nested_objects()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var list = dashboard.Operations.Single(o => o.OperationId == "listCustomers");

        var response = list.Responses.Single(r => r.StatusCode == "200");
        var schema = response.Contents.Single().Schema;

        Assert.Equal(SchemaTypes.Array, schema.Type);
        Assert.Equal(SchemaTypes.Object, schema.Items!.Type);
        Assert.Contains(schema.Items.Properties, p => p.Name == "address");

        var address = schema.Items.Properties.Single(p => p.Name == "address").Schema;
        Assert.Contains(address.Properties, p => p.Name == "postalCode");
    }

    [Fact]
    public void Stops_at_a_recursive_ref_instead_of_expanding_forever()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var nodes = dashboard.Operations.Single(o => o.OperationId == "getNodeTree");

        var schema = nodes.Responses.Single(r => r.StatusCode == "200").Contents.Single().Schema;
        var children = schema.Properties.Single(p => p.Name == "children").Schema;
        var item = children.Items!;

        Assert.True(item.Truncated);
        Assert.Equal("Node", item.RefName);
    }

    [Fact]
    public void Flattens_allOf_into_one_object()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var create = dashboard.Operations.Single(o => o.OperationId == "createCustomer");
        var schema = create.RequestBody!.Contents.Single().Schema;

        Assert.Equal(SchemaTypes.Object, schema.Type);
        Assert.Contains(schema.Properties, p => p.Name == "name");
        Assert.Contains(schema.Properties, p => p.Name == "password");
        Assert.Contains(schema.Properties, p => p.Name == "address");
        Assert.True(schema.Properties.Single(p => p.Name == "name").Required);
    }

    [Fact]
    public void Exposes_oneOf_as_selectable_variants_with_the_discriminator()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var payment = dashboard.Operations.Single(o => o.OperationId == "createPayment");
        var schema = payment.RequestBody!.Contents.Single().Schema;

        Assert.Equal(2, schema.Variants.Count);
        Assert.Equal("kind", schema.DiscriminatorProperty);
        Assert.Contains(schema.Variants, v => v.Name == "CardPayment");
    }

    [Fact]
    public void Reads_enums_defaults_and_constraints()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var list = dashboard.Operations.Single(o => o.OperationId == "listCustomers");

        var status = list.Parameters.Single(p => p.Name == "status");
        Assert.Equal(["active", "passive", "blocked"], status.Schema.Enum);

        var page = list.Parameters.Single(p => p.Name == "page");
        Assert.Equal("1", page.Schema.Default);
        Assert.Equal(1m, page.Schema.Minimum);
    }

    [Fact]
    public void Records_the_security_schemes_an_operation_requires()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());

        Assert.Contains(dashboard.SecuritySchemes, s => s.Key == "bearerAuth");
        Assert.Contains(dashboard.SecuritySchemes, s => s.Key == "apiKeyAuth" && s.ParameterName == "X-Api-Key");

        var getById = dashboard.Operations.Single(o => o.OperationId == "getCustomerById");
        Assert.True(getById.RequiresAuthentication);
        Assert.Contains("bearerAuth", getById.SecuritySchemeKeys);

        var nodes = dashboard.Operations.Single(o => o.OperationId == "getNodeTree");
        Assert.False(nodes.RequiresAuthentication);
    }

    [Fact]
    public void Recognises_a_multipart_file_upload()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());
        var upload = dashboard.Operations.Single(o => o.OperationId == "uploadCustomerDocument");
        var content = upload.RequestBody!.Contents.Single();

        Assert.Equal("multipart/form-data", content.ContentType);
        Assert.Equal("binary", content.Schema.Properties.Single(p => p.Name == "file").Schema.Format);
    }

    [Fact]
    public void Groups_operations_by_tag()
    {
        var dashboard = Generate(SampleDocuments.CustomerApi());

        var files = dashboard.Tags.Single(t => t.Name == "Files");
        Assert.Single(files.OperationSlugs);
        Assert.Equal(dashboard.Operations.Count, dashboard.Tags.Sum(t => t.OperationSlugs.Count));
    }

    [Fact]
    public void Falls_back_to_a_path_segment_when_the_document_has_no_tags()
    {
        var dashboard = Generate("""
            {
              "openapi": "3.0.1",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/api/v1/orders/{id}": { "get": { "responses": { "200": { "description": "ok" } } } }
              }
            }
            """);

        Assert.Equal("orders", dashboard.Operations.Single().Tag);
    }

    [Fact]
    public void Slugs_are_unique_and_url_safe_even_without_operation_ids()
    {
        var dashboard = Generate("""
            {
              "openapi": "3.0.1",
              "info": { "title": "T", "version": "1" },
              "paths": {
                "/a/{id}": {
                  "get": { "responses": { "200": { "description": "ok" } } },
                  "post": { "responses": { "200": { "description": "ok" } } }
                }
              }
            }
            """);

        var slugs = dashboard.Operations.Select(o => o.Slug).ToList();

        Assert.Equal(slugs.Count, slugs.Distinct().Count());
        Assert.All(slugs, slug => Assert.Matches("^[a-z0-9-]+$", slug));
    }

    [Fact]
    public void Reports_an_unusable_document_instead_of_throwing()
    {
        var result = _generator.Generate("this is not a document");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}

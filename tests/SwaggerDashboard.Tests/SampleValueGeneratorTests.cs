using System.Text.Json.Nodes;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;
using Xunit;

namespace SwaggerDashboard.Tests;

public class SampleValueGeneratorTests
{
    private static FieldSchema Schema(string type, string? format = null) =>
        new() { Type = type, Format = format };

    [Fact]
    public void A_uuid_field_gets_a_real_guid()
    {
        var value = SampleValueGenerator.Generate(Schema(SchemaTypes.String, "uuid"));

        Assert.True(Guid.TryParse(value, out _));
    }

    [Fact]
    public void Date_and_date_time_fields_get_parseable_values()
    {
        Assert.True(DateTime.TryParse(
            SampleValueGenerator.Generate(Schema(SchemaTypes.String, "date")), out _));

        Assert.True(DateTimeOffset.TryParse(
            SampleValueGenerator.Generate(Schema(SchemaTypes.String, "date-time")), out _));
    }

    [Fact]
    public void Generated_addresses_use_the_reserved_documentation_ranges()
    {
        // A sample that escapes into a real request must not reach anyone's system.
        Assert.Equal("ornek@example.com", SampleValueGenerator.Generate(Schema(SchemaTypes.String, "email")));
        Assert.Equal("192.0.2.1", SampleValueGenerator.Generate(Schema(SchemaTypes.String, "ipv4")));
        Assert.Contains("example.com", SampleValueGenerator.Generate(Schema(SchemaTypes.String, "uri")));
    }

    [Fact]
    public void An_enum_always_wins_over_a_generated_value()
    {
        var schema = new FieldSchema { Type = SchemaTypes.String, Format = "uuid", Enum = { "acik", "kapali" } };

        Assert.Equal("acik", SampleValueGenerator.Generate(schema));
    }

    [Fact]
    public void A_binary_field_is_left_to_the_upload_control()
    {
        Assert.Null(SampleValueGenerator.Generate(Schema(SchemaTypes.String, "binary")));
    }

    [Fact]
    public void Numeric_bounds_are_respected()
    {
        var withMinimum = new FieldSchema { Type = SchemaTypes.Integer, Minimum = 10 };
        Assert.Equal("10", SampleValueGenerator.Generate(withMinimum));

        var withMaximum = new FieldSchema { Type = SchemaTypes.Integer, Maximum = 0 };
        Assert.Equal("0", SampleValueGenerator.Generate(withMaximum));
    }

    [Fact]
    public void Length_bounds_are_respected()
    {
        var padded = SampleValueGenerator.Generate(new FieldSchema { Type = SchemaTypes.String, MinLength = 12 });
        Assert.Equal(12, padded!.Length);

        var trimmed = SampleValueGenerator.Generate(new FieldSchema { Type = SchemaTypes.String, MaxLength = 3 });
        Assert.Equal(3, trimmed!.Length);
    }

    [Fact]
    public void A_value_that_satisfies_the_pattern_is_used()
    {
        var schema = new FieldSchema { Type = SchemaTypes.String, Pattern = "^[a-z]+$" };

        Assert.Equal("ornek", SampleValueGenerator.Generate(schema));
    }

    [Fact]
    public void A_fixed_length_pattern_is_satisfied_by_trial()
    {
        var schema = new FieldSchema { Type = SchemaTypes.String, Pattern = "^[0-9]{5}$" };

        Assert.Equal("11111", SampleValueGenerator.Generate(schema));
    }

    [Fact]
    public void A_letter_pattern_is_satisfied_too()
    {
        var upper = new FieldSchema { Type = SchemaTypes.String, Pattern = "^[A-Z]{2}$" };
        Assert.Equal("AA", SampleValueGenerator.Generate(upper));
    }

    [Fact]
    public void A_field_whose_pattern_nothing_satisfies_is_left_empty()
    {
        // Leaving the gap visible beats filling it with something the API will reject.
        var schema = new FieldSchema { Type = SchemaTypes.String, Pattern = @"^TR\d{2}[A-Z]{4}\d{16}$" };

        Assert.Null(SampleValueGenerator.Generate(schema));
    }

    [Fact]
    public void An_invalid_pattern_does_not_throw()
    {
        var schema = new FieldSchema { Type = SchemaTypes.String, Pattern = "^([a-z" };

        Assert.Null(SampleValueGenerator.Generate(schema));
    }

    [Fact]
    public void The_field_name_is_used_only_for_unambiguous_hints()
    {
        Assert.Equal("ornek@example.com", SampleValueGenerator.Generate(Schema(SchemaTypes.String), "userEmail"));
        Assert.Equal("+905550000000", SampleValueGenerator.Generate(Schema(SchemaTypes.String), "phoneNumber"));
        Assert.Equal("ornek", SampleValueGenerator.Generate(Schema(SchemaTypes.String), "title"));
    }
}

public class FormNodeSampleFillTests
{
    private static FieldProperty Property(string name, string type, bool required = false, string? format = null) =>
        new() { Name = name, Required = required, Schema = new FieldSchema { Type = type, Format = format } };

    private static FieldSchema Object(params FieldProperty[] properties) =>
        new() { Type = SchemaTypes.Object, Properties = properties.ToList() };

    [Fact]
    public void Fills_empty_fields_and_produces_a_sendable_body()
    {
        var node = new FormNode(Object(
            Property("name", SchemaTypes.String, required: true),
            Property("email", SchemaTypes.String, format: "email"),
            Property("count", SchemaTypes.Integer)));

        node.FillWithSamples();
        var json = node.ToJson()!.AsObject();

        Assert.Equal("ornek", (string?)json["name"]);
        Assert.Equal("ornek@example.com", (string?)json["email"]);
        Assert.Equal(1L, (long?)json["count"]);
    }

    [Fact]
    public void Never_overwrites_a_value_the_user_already_entered()
    {
        var node = new FormNode(Object(Property("name", SchemaTypes.String, required: true)));
        node.Children[0].Value = "Zeynep Kaya";

        node.FillWithSamples();

        Assert.Equal("Zeynep Kaya", node.Children[0].Value);
    }

    [Fact]
    public void Skips_read_only_properties_because_they_belong_to_responses()
    {
        var schema = new FieldSchema
        {
            Type = SchemaTypes.Object,
            Properties =
            [
                new FieldProperty { Name = "id", Schema = new FieldSchema { Type = SchemaTypes.String, ReadOnly = true } },
                new FieldProperty { Name = "name", Schema = new FieldSchema { Type = SchemaTypes.String } },
            ],
        };

        var node = new FormNode(schema);
        node.FillWithSamples();

        Assert.Empty(node.Children[0].Value);
        Assert.Equal("ornek", node.Children[1].Value);
    }

    [Fact]
    public void Gives_an_empty_array_one_filled_item()
    {
        var schema = new FieldSchema
        {
            Type = SchemaTypes.Array,
            Items = new FieldSchema { Type = SchemaTypes.String },
        };

        var node = new FormNode(schema, "tags");
        node.FillWithSamples();

        var array = node.ToJson()!.AsArray();
        Assert.Single(array);
        Assert.Equal("ornek", (string?)array[0]);
    }

    [Fact]
    public void Descends_into_nested_objects()
    {
        var address = new FieldSchema
        {
            Type = SchemaTypes.Object,
            Properties = [new FieldProperty { Name = "city", Schema = new FieldSchema { Type = SchemaTypes.String } }],
        };

        var node = new FormNode(new FieldSchema
        {
            Type = SchemaTypes.Object,
            Properties = [new FieldProperty { Name = "address", Schema = address }],
        });

        node.FillWithSamples();

        Assert.Equal("ornek", (string?)node.ToJson()!["address"]!["city"]);
    }

    [Fact]
    public void A_schema_default_survives_the_fill()
    {
        var schema = new FieldSchema { Type = SchemaTypes.Integer, Default = "5" };
        var node = new FormNode(schema, "page");

        node.FillWithSamples();

        Assert.Equal("5", node.Value);
    }

    [Fact]
    public void Filling_the_sample_document_produces_a_body_the_form_can_round_trip()
    {
        var generator = new DashboardGeneratorService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DashboardGeneratorService>.Instance);

        var dashboard = generator.Generate(SampleDocuments.CustomerApi()).Document!;
        var create = dashboard.Operations.Single(o => o.OperationId == "createCustomer");

        var node = new FormNode(create.RequestBody!.Contents.Single().Schema, null, true) { Included = true };
        node.FillWithSamples();

        var json = JsonNode.Parse(node.ToJsonString())!.AsObject();

        Assert.Equal("ornek", (string?)json["name"]);
        Assert.Equal("ornek@example.com", (string?)json["email"]);

        // postalCode declares ^[0-9]{5}$; the generated value is verified against that
        // pattern before it is used.
        Assert.Matches("^[0-9]{5}$", (string?)json["address"]!["postalCode"]);
    }
}

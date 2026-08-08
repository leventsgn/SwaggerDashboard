using System.Text.Json.Nodes;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;
using Xunit;

namespace SwaggerDashboard.Tests;

public class FormNodeTests
{
    private static FieldSchema Object(params FieldProperty[] properties) => new()
    {
        Type = SchemaTypes.Object,
        Properties = properties.ToList(),
    };

    private static FieldProperty Property(string name, string type, bool required = false) =>
        new() { Name = name, Required = required, Schema = new FieldSchema { Type = type } };

    [Fact]
    public void Emits_typed_json_rather_than_stringifying_everything()
    {
        var node = new FormNode(Object(
            Property("name", SchemaTypes.String, required: true),
            Property("age", SchemaTypes.Integer, required: true),
            Property("score", SchemaTypes.Number, required: true),
            Property("active", SchemaTypes.Boolean, required: true)));

        node.Children[0].Value = "Ada";
        node.Children[1].Value = "42";
        node.Children[2].Value = "3.5";
        node.Children[3].Value = "true";

        var json = node.ToJson()!.AsObject();

        Assert.Equal("Ada", (string?)json["name"]);
        Assert.Equal(42L, (long?)json["age"]);
        Assert.Equal(3.5m, (decimal?)json["score"]);
        Assert.True((bool?)json["active"]);
    }

    [Fact]
    public void Omits_optional_properties_the_user_never_filled_in()
    {
        var node = new FormNode(Object(
            Property("name", SchemaTypes.String, required: true),
            Property("nickname", SchemaTypes.String)));

        node.Children[0].Value = "Ada";

        var json = node.ToJson()!.AsObject();

        Assert.True(json.ContainsKey("name"));
        Assert.False(json.ContainsKey("nickname"));
    }

    [Fact]
    public void Includes_an_optional_property_once_it_is_ticked()
    {
        var node = new FormNode(Object(Property("nickname", SchemaTypes.String)));

        node.Children[0].Included = true;
        node.Children[0].Value = "ada";

        Assert.True(node.ToJson()!.AsObject().ContainsKey("nickname"));
    }

    [Fact]
    public void Applies_the_schema_default_and_marks_the_field_as_sent()
    {
        var schema = new FieldSchema { Type = SchemaTypes.Integer, Default = "1" };
        var node = new FormNode(schema, "page");

        Assert.Equal("1", node.Value);
        Assert.True(node.Included);
    }

    [Fact]
    public void An_example_prefills_the_field_without_forcing_it_to_be_sent()
    {
        var schema = new FieldSchema { Type = SchemaTypes.String, Example = "ornek" };
        var node = new FormNode(schema, "q");

        Assert.Equal("ornek", node.Value);
        Assert.False(node.Included);
    }

    [Fact]
    public void Arrays_grow_and_shrink()
    {
        var schema = new FieldSchema
        {
            Type = SchemaTypes.Array,
            Items = new FieldSchema { Type = SchemaTypes.String },
        };

        var node = new FormNode(schema, "tags");
        node.AddItem();
        node.AddItem();
        node.Items[0].Value = "a";
        node.Items[1].Value = "b";

        Assert.Equal(2, node.ToJson()!.AsArray().Count);

        node.RemoveItem(node.Items[0]);
        Assert.Single(node.ToJson()!.AsArray());
    }

    [Fact]
    public void Round_trips_between_the_form_and_the_raw_json_view()
    {
        var node = new FormNode(Object(
            Property("name", SchemaTypes.String, required: true),
            Property("age", SchemaTypes.Integer)));

        node.LoadFrom(JsonNode.Parse("""{"name":"Ada","age":36}"""));

        Assert.Equal("Ada", node.Children[0].Value);
        Assert.Equal("36", node.Children[1].Value);
        Assert.True(node.Children[1].Included);

        var json = node.ToJson()!.AsObject();
        Assert.Equal(36L, (long?)json["age"]);
    }

    [Fact]
    public void Loading_json_clears_properties_that_are_absent()
    {
        var node = new FormNode(Object(Property("nickname", SchemaTypes.String)));
        node.Children[0].Included = true;
        node.Children[0].Value = "old";

        node.LoadFrom(JsonNode.Parse("""{}"""));

        Assert.False(node.Children[0].Included);
    }

    [Fact]
    public void Selecting_a_oneOf_variant_rebuilds_the_child_fields()
    {
        var schema = new FieldSchema
        {
            Type = SchemaTypes.Object,
            Variants =
            [
                new FieldVariant { Name = "Card", Schema = Object(Property("cardNumber", SchemaTypes.String, true)) },
                new FieldVariant { Name = "Transfer", Schema = Object(Property("iban", SchemaTypes.String, true)) },
            ],
        };

        var node = new FormNode(schema);
        Assert.Equal("cardNumber", node.Children.Single().Name);

        node.SelectVariant(1);
        Assert.Equal("iban", node.Children.Single().Name);
    }

    [Fact]
    public void A_truncated_recursive_node_is_edited_as_raw_json()
    {
        var schema = new FieldSchema { Type = SchemaTypes.Object, Truncated = true, RefName = "Node" };
        var node = new FormNode(schema, "children") { Value = """{"name":"root"}""" };

        // Truncated nodes are not expanded into children; the typed text is parsed as JSON.
        Assert.False(node.IsObject);
        Assert.Equal("root", (string?)node.ToJson()!["name"]);
    }

    [Fact]
    public void An_unparseable_value_is_sent_as_text_instead_of_being_dropped()
    {
        var schema = new FieldSchema { Type = SchemaTypes.Integer };
        var node = new FormNode(schema, "count") { Value = "abc" };

        Assert.Equal("abc", (string?)node.ToJson());
    }
}

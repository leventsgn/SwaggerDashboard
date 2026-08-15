using Bunit;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;
using SwaggerDashboard.Web.Components.Shared;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Renders one field of the generated request form.
/// </summary>
public class SchemaFieldComponentTests : TestContext
{
    private static FieldSchema Schema(
        string type = SchemaTypes.String,
        string? format = null,
        int? minLength = null,
        int? maxLength = null,
        params string[] enumValues) => new()
    {
        Type = type,
        Format = format,
        MinLength = minLength,
        MaxLength = maxLength,
        Enum = [.. enumValues],
    };

    private IRenderedComponent<SchemaField> Render(FormNode node) =>
        RenderComponent<SchemaField>(p => p.Add(c => c.Node, node));

    [Fact]
    public void An_unconstrained_text_field_carries_no_length_limit()
    {
        // A nullable constraint rendered as zero produced maxlength="0", which silently
        // refused every keystroke: the form looked normal and could not be typed into.
        var component = Render(new FormNode(Schema(), "name"));

        var input = component.Find("input[type=text]");

        Assert.False(input.HasAttribute("maxlength"));
        Assert.False(input.HasAttribute("minlength"));
    }

    [Fact]
    public void A_declared_length_limit_is_passed_to_the_input()
    {
        var component = Render(new FormNode(Schema(minLength: 8, maxLength: 64), "password"));

        var input = component.Find("input[type=text]");

        Assert.Equal("8", input.GetAttribute("minlength"));
        Assert.Equal("64", input.GetAttribute("maxlength"));
    }

    [Fact]
    public void A_required_field_is_marked_and_has_no_include_checkbox()
    {
        // Required fields are always sent, so a switch that cannot be turned off would only
        // suggest a choice the user does not have.
        var component = Render(new FormNode(Schema(), "name", required: true));

        Assert.Contains("sd-required", component.Markup);
        Assert.Empty(component.FindAll(".sd-field-label input[type=checkbox]"));
    }

    [Fact]
    public void An_optional_field_can_be_switched_on_and_off()
    {
        var node = new FormNode(Schema(), "status");
        var component = Render(node);

        component.Find(".sd-field-label input[type=checkbox]").Change(true);
        Assert.True(node.Included);

        component.Find(".sd-field-label input[type=checkbox]").Change(false);
        Assert.False(node.Included);
    }

    [Fact]
    public void An_enum_renders_as_a_list_with_a_blank_option_when_optional()
    {
        var component = Render(new FormNode(Schema(enumValues: ["active", "passive"]), "status"));

        var options = component.FindAll("option").Select(o => o.GetAttribute("value")).ToList();

        Assert.Equal(["", "active", "passive"], options);
    }

    [Fact]
    public void A_required_enum_does_not_offer_an_empty_choice()
    {
        var component = Render(new FormNode(Schema(enumValues: ["active", "passive"]), "status", required: true));

        Assert.Equal(["active", "passive"], component.FindAll("option").Select(o => o.GetAttribute("value")));
    }

    [Fact]
    public void A_boolean_renders_as_a_switch_that_writes_true_or_false()
    {
        var node = new FormNode(Schema(SchemaTypes.Boolean), "isActive");
        var component = Render(node);

        component.Find(".form-switch input").Change(true);
        Assert.Equal("true", node.Value);

        component.Find(".form-switch input").Change(false);
        Assert.Equal("false", node.Value);
    }

    [Fact]
    public void An_array_can_gain_and_lose_items()
    {
        var node = new FormNode(new FieldSchema
        {
            Type = SchemaTypes.Array,
            Items = Schema(),
        }, "tags");

        var component = Render(node);
        Assert.Empty(component.FindAll(".sd-array-item"));

        component.Find("button.btn-outline-secondary:not(.sd-icon-btn)").Click();
        Assert.Single(component.FindAll(".sd-array-item"));

        component.Find(".sd-array-item .sd-icon-btn").Click();
        Assert.Empty(component.FindAll(".sd-array-item"));
    }

    [Fact]
    public void An_object_renders_its_properties_as_nested_fields()
    {
        var node = new FormNode(new FieldSchema
        {
            Type = SchemaTypes.Object,
            Properties =
            [
                new FieldProperty { Name = "line1", Schema = Schema(), Required = true },
                new FieldProperty { Name = "city", Schema = Schema() },
            ],
        });

        var component = Render(node);

        var labels = component.FindAll(".sd-field-label span").Select(e => e.TextContent).ToList();
        Assert.Contains("line1", labels);
        Assert.Contains("city", labels);
        Assert.Equal(2, component.FindAll(".sd-field-nested").Count);
    }

    [Fact]
    public void A_free_form_object_says_so_and_offers_raw_json()
    {
        // There is nothing to generate for a schema with no declared properties: the keys are
        // whatever the API expects, and inventing them would produce a rejected request.
        var component = Render(new FormNode(new FieldSchema { Type = SchemaTypes.Object }));

        Assert.Contains("serbest biçimli", component.Markup);
        Assert.Single(component.FindAll("textarea"));
    }

    [Fact]
    public void A_truncated_recursive_schema_explains_itself_instead_of_rendering_nothing()
    {
        var component = Render(new FormNode(new FieldSchema
        {
            Type = SchemaTypes.Object,
            Truncated = true,
            RefName = "Node",
        }, "node"));

        Assert.Contains("özyinelemeli", component.Markup);
        Assert.Single(component.FindAll("textarea"));
    }

    [Fact]
    public void A_variant_choice_rebuilds_the_fields_under_it()
    {
        // oneOf: picking a variant has to replace the children, or the form would send the
        // fields of a shape the user is no longer choosing.
        var node = new FormNode(new FieldSchema
        {
            Variants =
            [
                new FieldVariant
                {
                    Name = "card",
                    Schema = new FieldSchema
                    {
                        Type = SchemaTypes.Object,
                        Properties = [new FieldProperty { Name = "cardNumber", Schema = Schema(), Required = true }],
                    },
                },
                new FieldVariant
                {
                    Name = "iban",
                    Schema = new FieldSchema
                    {
                        Type = SchemaTypes.Object,
                        Properties = [new FieldProperty { Name = "iban", Schema = Schema(), Required = true }],
                    },
                },
            ],
        });

        var component = Render(node);
        Assert.Contains("cardNumber", component.Markup);

        component.Find("select").Change("1");

        Assert.DoesNotContain("cardNumber", component.Markup);
        Assert.Contains("iban", component.Markup);
    }

    [Fact]
    public void Editing_a_value_reports_the_change_upwards()
    {
        // The raw JSON view is regenerated from this callback, so a missed notification would
        // let the two views drift apart.
        var node = new FormNode(Schema(), "name");
        var notified = 0;

        var component = RenderComponent<SchemaField>(p => p
            .Add(c => c.Node, node)
            .Add(c => c.OnChanged, () => notified++));

        component.Find("input[type=text]").Change("Ada");

        Assert.Equal("Ada", node.Value);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void A_password_format_is_not_shown_in_clear_text()
    {
        var component = Render(new FormNode(Schema(format: "password"), "password"));

        Assert.Single(component.FindAll("input[type=password]"));
    }
}

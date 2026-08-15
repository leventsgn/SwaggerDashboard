using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.Json.Nodes;
using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// The editable value tree behind the generated request form.
/// </summary>
/// <remarks>
/// The form view and the raw JSON view are two projections of this one tree, which is what
/// keeps them in sync: editing a field mutates the node and the JSON is re-rendered, and
/// pasting JSON re-populates the nodes.
/// </remarks>
public class FormNode
{
    public FormNode(FieldSchema schema, string? name = null, bool required = false)
    {
        Schema = schema;
        Name = name;
        Required = required;

        if (schema.Variants.Count > 0)
        {
            SelectedVariant = 0;
        }

        Initialize();
    }

    public FieldSchema Schema { get; }

    public string? Name { get; }

    public bool Required { get; }

    /// <summary>Scalar value as typed by the user.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Whether an optional property is sent at all. Required properties are always included;
    /// optional ones default to excluded so a generated body does not send a wall of nulls.
    /// </summary>
    /// <remarks>
    /// This is the only thing that decides whether an optional field is sent. It used to be
    /// overruled by "does the field have a value?", which made the tick box on the form inert:
    /// unticking a filled field changed nothing about the request.
    /// </remarks>
    public bool Included { get; set; }

    /// <summary>The node this one hangs under, so including a field can include its parents.</summary>
    public FormNode? Parent { get; private set; }

    public List<FormNode> Children { get; } = [];

    public List<FormNode> Items { get; } = [];

    public int? SelectedVariant { get; set; }

    /// <summary>Effective schema after variant selection.</summary>
    public FieldSchema EffectiveSchema =>
        SelectedVariant is { } index && index >= 0 && index < Schema.Variants.Count
            ? Schema.Variants[index].Schema
            : Schema;

    public bool IsObject => EffectiveSchema.Type == SchemaTypes.Object && !EffectiveSchema.Truncated;

    public bool IsArray => EffectiveSchema.Type == SchemaTypes.Array;

    private void Initialize()
    {
        Included = Required;

        var schema = EffectiveSchema;

        // Every path that writes a value also ticks the field. A value shown in a form the
        // user is about to send but silently dropped is the same defect as one sent without
        // being shown, in the other direction.
        if (!string.IsNullOrEmpty(schema.Default))
        {
            Value = schema.Default;
            Included = true;
        }
        else if (!string.IsNullOrEmpty(schema.Example))
        {
            Value = schema.Example;
            Included = true;
        }
        else if (schema.Enum.Count > 0 && Required)
        {
            Value = schema.Enum[0];
        }

        if (IsObject)
        {
            foreach (var property in schema.Properties)
            {
                Children.Add(new FormNode(property.Schema, property.Name, property.Required)
                {
                    Parent = this,
                });
            }
        }
    }

    /// <summary>
    /// Ticks or unticks this field.
    /// </summary>
    /// <remarks>
    /// Including a nested field includes the objects above it: a city inside an address that
    /// is itself unticked would otherwise be dropped on the way out, which is not what the
    /// person who ticked the city asked for.
    /// </remarks>
    public void SetIncluded(bool included)
    {
        Included = included;

        if (!included)
        {
            return;
        }

        for (var parent = Parent; parent is not null; parent = parent.Parent)
        {
            parent.Included = true;
        }
    }

    public void SelectVariant(int index)
    {
        SelectedVariant = index;
        Children.Clear();
        Items.Clear();

        if (IsObject)
        {
            foreach (var property in EffectiveSchema.Properties)
            {
                Children.Add(new FormNode(property.Schema, property.Name, property.Required));
            }
        }
    }

    /// <summary>
    /// Fills empty fields with generated sample values.
    /// </summary>
    /// <remarks>
    /// Values already present are never overwritten, which is what lets this run both when the
    /// form is first built and again from the button afterwards. Read-only properties are
    /// skipped because they belong to responses rather than requests, and a field whose value
    /// cannot be generated honestly is left empty so the gap stays visible.
    /// </remarks>
    public void FillWithSamples()
    {
        if (IsObject)
        {
            foreach (var child in Children)
            {
                child.FillWithSamples();
            }

            return;
        }

        if (IsArray)
        {
            if (Items.Count == 0)
            {
                AddItem();
            }

            foreach (var item in Items)
            {
                item.FillWithSamples();
            }

            return;
        }

        if (!string.IsNullOrEmpty(Value) || EffectiveSchema.ReadOnly)
        {
            return;
        }

        var sample = SampleValueGenerator.Generate(EffectiveSchema, Name);
        if (sample is null)
        {
            return;
        }

        Value = sample;
        SetIncluded(true);
    }

    public void AddItem()
    {
        var itemSchema = EffectiveSchema.Items ?? new FieldSchema();
        var item = new FormNode(itemSchema, $"[{Items.Count}]", true) { Included = true, Parent = this };
        Items.Add(item);
        SetIncluded(true);
    }

    public void RemoveItem(FormNode item)
    {
        Items.Remove(item);
    }

    /// <summary>
    /// Renders the node as JSON. Optional nodes that were never filled in are omitted so the
    /// request body contains only what the user actually set.
    /// </summary>
    public JsonNode? ToJson()
    {
        var schema = EffectiveSchema;

        if (IsObject)
        {
            var obj = new JsonObject();

            foreach (var child in Children)
            {
                if (!child.Included && !child.Required)
                {
                    continue;
                }

                obj[child.Name ?? string.Empty] = child.ToJson();
            }

            return obj;
        }

        if (IsArray)
        {
            var array = new JsonArray();

            foreach (var item in Items)
            {
                array.Add(item.ToJson());
            }

            return array;
        }

        return ScalarToJson(Value, schema);
    }

    /// <summary>
    /// Converts the typed text into a JSON value of the declared type. An unparseable value
    /// is emitted as a string rather than being dropped, so the user sees the target API's
    /// own validation error instead of the dashboard silently changing their input.
    /// </summary>
    internal static JsonNode? ScalarToJson(string? raw, FieldSchema schema)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return schema.Nullable || !schema.Type.Equals(SchemaTypes.String, StringComparison.Ordinal)
                ? null
                : JsonValue.Create(string.Empty);
        }

        switch (schema.Type)
        {
            case SchemaTypes.Integer:
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? JsonValue.Create(i)
                    : JsonValue.Create(raw);

            case SchemaTypes.Number:
                return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? JsonValue.Create(d)
                    : JsonValue.Create(raw);

            case SchemaTypes.Boolean:
                return bool.TryParse(raw, out var b) ? JsonValue.Create(b) : JsonValue.Create(raw);

            case SchemaTypes.Object:
            case SchemaTypes.Array:
                // A truncated or free form node is edited as raw JSON text.
                try
                {
                    return JsonNode.Parse(raw);
                }
                catch (JsonException)
                {
                    return JsonValue.Create(raw);
                }

            default:
                return JsonValue.Create(raw);
        }
    }

    /// <summary>
    /// Repopulates the tree from a JSON document, used when the user edits the raw view and
    /// switches back to the form.
    /// </summary>
    public void LoadFrom(JsonNode? node)
    {
        if (node is null)
        {
            Value = string.Empty;
            Included = Required;
            return;
        }

        if (IsObject && node is JsonObject obj)
        {
            foreach (var child in Children)
            {
                if (child.Name is not null && obj.TryGetPropertyValue(child.Name, out var childNode))
                {
                    child.SetIncluded(true);
                    child.LoadFrom(childNode);
                }
                else
                {
                    child.Included = child.Required;
                }
            }

            Included = true;
            return;
        }

        if (IsArray && node is JsonArray array)
        {
            Items.Clear();

            foreach (var element in array)
            {
                AddItem();
                Items[^1].LoadFrom(element);
            }

            Included = true;
            return;
        }

        Value = node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node.ToJsonString().Trim('"');

        Included = true;
    }

    /// <summary>
    /// Renders the request body. Non-ASCII characters are written as themselves rather than
    /// as \u escapes, so a body typed in Turkish stays readable in the raw view and in the
    /// request log.
    /// </summary>
    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public string ToJsonString()
    {
        var json = ToJson();
        return json is null ? "{}" : json.ToJsonString(BodyOptions);
    }
}

using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// Turns a form node tree into a proxy request.
/// </summary>
/// <remarks>
/// Both the endpoint screen and the bulk run need this mapping, and they have to agree: a
/// sweep that composes requests slightly differently from the screen would report green for
/// calls the user cannot reproduce by hand. So the mapping lives here once and the screen
/// only supplies the nodes.
/// </remarks>
public static class RequestComposer
{
    public static string ParameterKey(DashboardParameter parameter) => $"{parameter.In}:{parameter.Name}";

    public static Dictionary<string, FormNode> CreateParameterNodes(DashboardOperation operation)
    {
        var nodes = new Dictionary<string, FormNode>(StringComparer.Ordinal);

        foreach (var parameter in operation.Parameters)
        {
            nodes[ParameterKey(parameter)] = new FormNode(parameter.Schema, parameter.Name, parameter.Required);
        }

        return nodes;
    }

    /// <summary>
    /// Copies the parameter values onto the request, in the place each parameter belongs.
    /// </summary>
    /// <returns>
    /// False when a required path parameter has no value. The call cannot be built at all in
    /// that case: the path template would keep its placeholder.
    /// </returns>
    public static bool TryApplyParameters(
        DashboardOperation operation,
        IReadOnlyDictionary<string, FormNode> nodes,
        ProxyRequest request,
        out string? missingPathParameter)
    {
        missingPathParameter = null;

        foreach (var parameter in operation.Parameters)
        {
            if (!nodes.TryGetValue(ParameterKey(parameter), out var node))
            {
                continue;
            }

            var values = node.IsArray
                ? node.Items.Select(i => i.Value).Where(v => !string.IsNullOrEmpty(v)).ToList()
                : [];

            var value = node.Value;

            if (node.IsArray)
            {
                if (values.Count == 0)
                {
                    continue;
                }
            }
            else if (string.IsNullOrEmpty(value))
            {
                if (parameter.Required && parameter.In == ParameterLocations.Path)
                {
                    missingPathParameter = parameter.Name;
                    return false;
                }

                if (!node.Included)
                {
                    continue;
                }
            }

            switch (parameter.In)
            {
                case ParameterLocations.Path:
                    // A path array uses the simple style: comma separated in one segment.
                    request.PathParameters[parameter.Name] =
                        node.IsArray ? string.Join(',', values) : value;
                    break;

                case ParameterLocations.Header:
                    // Header and cookie arrays were sent as an empty string before: the array
                    // node's own Value is always empty, and only the query branch looked at
                    // the items. The values are joined, which is the "simple" style OpenAPI
                    // defines for both.
                    request.Headers[parameter.Name] =
                        node.IsArray ? string.Join(',', values) : value;
                    break;

                case ParameterLocations.Cookie:
                    request.Cookies[parameter.Name] =
                        node.IsArray ? string.Join(',', values) : value;
                    break;

                default:
                    if (node.IsArray)
                    {
                        AddQueryArray(parameter, values, request);
                    }
                    else
                    {
                        request.QueryParameters.Add(new(parameter.Name, value));
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Serializes an array query parameter in the style the document declares.
    /// </summary>
    /// <remarks>
    /// The document says how it wants an array on the wire, and the four forms are not
    /// interchangeable: a target expecting <c>tags=a|b</c> receives nothing usable from
    /// <c>tags=a&amp;tags=b</c>. Style and explode were parsed and stored but never read, so
    /// every array went out as repeated keys and three of the four cases were wrong.
    /// </remarks>
    private static void AddQueryArray(
        DashboardParameter parameter,
        IReadOnlyList<string> values,
        ProxyRequest request)
    {
        // The reader fills in the specification's defaults, so Explode is true for form style
        // and false for the delimited ones even when the document omits it.
        var explode = parameter.Explode;

        var separator = parameter.Style?.ToLowerInvariant() switch
        {
            "pipedelimited" => "|",
            "spacedelimited" => " ",
            _ => ",",
        };

        if (explode)
        {
            foreach (var item in values)
            {
                request.QueryParameters.Add(new(parameter.Name, item));
            }

            return;
        }

        request.QueryParameters.Add(new(parameter.Name, string.Join(separator, values)));
    }

    /// <summary>
    /// Builds a runnable request for an operation without any user input, using generated
    /// sample values throughout.
    /// </summary>
    /// <remarks>
    /// This is what makes a one-click run of every endpoint possible. It refuses rather than
    /// guesses in the two cases where a generated request would be a lie: a file upload, for
    /// which there is no honest sample body, and a required path parameter whose schema is too
    /// constrained to generate a value for.
    /// </remarks>
    public static SampleRequestResult CreateSampleRequest(
        DashboardOperation operation,
        int apiDefinitionId,
        string? environmentName,
        string? userId,
        string? clientIp,
        bool isBulkRun = false)
    {
        var content = operation.RequestBody?.Contents.FirstOrDefault();

        if (content is not null &&
            content.ContentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return SampleRequestResult.Unsupported("Dosya yüklemesi için örnek içerik üretilemez.");
        }

        var nodes = CreateParameterNodes(operation);

        foreach (var node in nodes.Values)
        {
            node.FillWithSamples();
        }

        string? body = null;

        if (content is not null)
        {
            var bodyNode = new FormNode(content.Schema, null, true) { Included = true };
            bodyNode.FillWithSamples();
            body = bodyNode.ToJsonString();
        }

        var request = new ProxyRequest
        {
            ApiDefinitionId = apiDefinitionId,
            OperationSlug = operation.Slug,
            EnvironmentName = environmentName,
            UserId = userId,
            ClientIp = clientIp,
            ContentType = content?.ContentType ?? "application/json",
            Body = body,
            IsBulkRun = isBulkRun,
        };

        if (!TryApplyParameters(operation, nodes, request, out var missing))
        {
            return SampleRequestResult.Unsupported($"Zorunlu path parametresi için değer üretilemedi: {missing}");
        }

        return SampleRequestResult.Ok(request);
    }

    /// <summary>
    /// Reads the values a user has entered into a form so they can be stored.
    /// </summary>
    /// <remarks>
    /// An optional parameter that is neither ticked nor filled is left out entirely rather
    /// than stored as empty, so restoring the request produces the same call it made: an empty
    /// query parameter is not the same thing as an absent one.
    /// </remarks>
    public static SavedRequestPayload CapturePayload(
        IReadOnlyDictionary<string, FormNode> parameterNodes,
        string? contentType,
        string? body)
    {
        var parameters = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (key, node) in parameterNodes)
        {
            if (node.IsArray)
            {
                var values = node.Items
                    .Where(i => !string.IsNullOrEmpty(i.Value))
                    .Select(i => i.Value)
                    .ToList();

                if (values.Count > 0)
                {
                    parameters[key] = values;
                }
            }
            else if (node.Included || !string.IsNullOrEmpty(node.Value))
            {
                parameters[key] = [node.Value];
            }
        }

        return new SavedRequestPayload
        {
            Parameters = parameters,
            ContentType = contentType,
            Body = body,
        };
    }

    /// <summary>
    /// Puts stored values back into a freshly built form.
    /// </summary>
    /// <remarks>
    /// The nodes are expected to be empty: loading a saved request has to reproduce it
    /// exactly, and leftover sample values in fields the payload does not mention would be
    /// sent along with it. Fields the document no longer has are skipped silently, which is
    /// what keeps an old saved request usable after the API changes.
    /// </remarks>
    public static void ApplyPayload(
        SavedRequestPayload payload,
        IReadOnlyDictionary<string, FormNode> parameterNodes,
        FormNode? bodyNode)
    {
        foreach (var (key, values) in payload.Parameters)
        {
            if (!parameterNodes.TryGetValue(key, out var node))
            {
                continue;
            }

            if (node.IsArray)
            {
                node.Items.Clear();

                foreach (var value in values)
                {
                    node.AddItem();
                    node.Items[^1].Value = value;
                }

                node.Included = true;
            }
            else
            {
                node.Value = values.Count > 0 ? values[0] : string.Empty;
                node.Included = true;
            }
        }

        var body = payload.BodyAsNode();

        if (bodyNode is not null && body is not null)
        {
            bodyNode.LoadFrom(body);
        }
    }
}

public record SampleRequestResult(ProxyRequest? Request, string? Reason)
{
    public static SampleRequestResult Ok(ProxyRequest request) => new(request, null);

    public static SampleRequestResult Unsupported(string reason) => new(null, reason);
}

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

            var value = node.Value;

            if (string.IsNullOrEmpty(value) && !node.IsArray)
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
                    request.PathParameters[parameter.Name] = value;
                    break;

                case ParameterLocations.Header:
                    request.Headers[parameter.Name] = value;
                    break;

                case ParameterLocations.Cookie:
                    request.Cookies[parameter.Name] = value;
                    break;

                default:
                    if (node.IsArray)
                    {
                        // Array query parameters repeat the key, which is the default "form"
                        // style with explode enabled.
                        foreach (var item in node.Items.Where(i => !string.IsNullOrEmpty(i.Value)))
                        {
                            request.QueryParameters.Add(new(parameter.Name, item.Value));
                        }
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
}

public record SampleRequestResult(ProxyRequest? Request, string? Reason)
{
    public static SampleRequestResult Ok(ProxyRequest request) => new(request, null);

    public static SampleRequestResult Unsupported(string reason) => new(null, reason);
}

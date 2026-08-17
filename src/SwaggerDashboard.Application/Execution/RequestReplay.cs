using System.Web;
using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// Turns a logged call back into something the request form can be filled from.
/// </summary>
/// <remarks>
/// The log stores what was sent — a finished URL and a body — not the fields it was built
/// from, so replaying means reading the values back out of the URL. That is deliberate: the
/// log is an audit record of the call that actually went out, and duplicating the form state
/// into it would create a second version of the truth that can disagree with the first.
///
/// The result is a <see cref="SavedRequestPayload"/> so that history and saved requests load
/// through exactly the same path; a second loader would drift from the first.
/// </remarks>
public static class RequestReplay
{
    /// <summary>
    /// Reads a logged request URL and body into a payload, or returns null when the URL does
    /// not belong to this operation.
    /// </summary>
    public static SavedRequestPayload? FromLoggedRequest(
        DashboardOperation operation,
        string? requestUrl,
        string? body)
    {
        if (string.IsNullOrWhiteSpace(requestUrl) ||
            !Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var parameters = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (!TryReadPathParameters(operation, uri, parameters))
        {
            return null;
        }

        ReadQueryParameters(operation, uri, parameters);

        return new SavedRequestPayload
        {
            Parameters = parameters,
            ContentType = operation.RequestBody?.Contents.FirstOrDefault()?.ContentType,
            Body = string.IsNullOrWhiteSpace(body) ? null : body,
        };
    }

    /// <summary>
    /// Matches the operation's path template against the tail of the logged URL.
    /// </summary>
    /// <remarks>
    /// Aligned from the end because the front of the URL is the environment's base path,
    /// which is not part of the template and differs between environments. A literal segment
    /// that does not match means this URL was not produced by this operation, and guessing
    /// past it would fill the form with someone else's values.
    /// </remarks>
    private static bool TryReadPathParameters(
        DashboardOperation operation,
        Uri uri,
        Dictionary<string, List<string>> parameters)
    {
        var template = operation.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var actual = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (template.Length > actual.Length)
        {
            return false;
        }

        var offset = actual.Length - template.Length;

        for (var i = 0; i < template.Length; i++)
        {
            var segment = template[i];
            var value = Uri.UnescapeDataString(actual[offset + i]);

            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                var name = segment[1..^1];

                if (operation.Parameters.Any(p =>
                        p.In == ParameterLocations.Path &&
                        string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    parameters[$"{ParameterLocations.Path}:{name}"] = [value];
                }

                continue;
            }

            if (!string.Equals(segment, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Copies query values the document declares. Anything else in the URL is ignored: the
    /// form has no field for it, so carrying it would silently drop it on the next run.
    /// </summary>
    private static void ReadQueryParameters(
        DashboardOperation operation,
        Uri uri,
        Dictionary<string, List<string>> parameters)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);

        foreach (var parameter in operation.Parameters.Where(p => p.In == ParameterLocations.Query))
        {
            var values = query.GetValues(parameter.Name);

            if (values is null || values.Length == 0)
            {
                continue;
            }

            parameters[$"{ParameterLocations.Query}:{parameter.Name}"] = [.. values];
        }
    }
}

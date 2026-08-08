using System.Text;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// Renders the executed call as copy-pasteable client code.
/// </summary>
/// <remarks>
/// Secrets are replaced with a placeholder rather than emitted: snippets are meant to be
/// copied into tickets and chat, so a real token in the text would leak on the first paste.
/// </remarks>
public static class CodeSnippetGenerator
{
    public const string SecretPlaceholder = "<TOKEN>";

    public static string ToCurl(ExecutedCall call)
    {
        var builder = new StringBuilder();
        builder.Append("curl -X ").Append(call.Method).Append(" \\\n  '").Append(call.Url).Append('\'');

        foreach (var (name, value) in call.Headers)
        {
            builder.Append(" \\\n  -H '").Append(name).Append(": ").Append(Redact(name, value)).Append('\'');
        }

        if (!string.IsNullOrEmpty(call.Body))
        {
            builder.Append(" \\\n  -d '").Append(call.Body.Replace("'", "'\\''")).Append('\'');
        }

        return builder.ToString();
    }

    public static string ToCSharp(ExecutedCall call)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using var client = new HttpClient();");
        builder.AppendLine($"using var request = new HttpRequestMessage(new HttpMethod(\"{call.Method}\"), \"{call.Url}\");");

        foreach (var (name, value) in call.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.AppendLine($"request.Headers.TryAddWithoutValidation(\"{name}\", \"{Redact(name, value)}\");");
        }

        if (!string.IsNullOrEmpty(call.Body))
        {
            var escaped = call.Body.Replace("\"", "\"\"");
            builder.AppendLine($"request.Content = new StringContent(@\"{escaped}\", System.Text.Encoding.UTF8, \"{call.ContentType ?? "application/json"}\");");
        }

        builder.AppendLine("using var response = await client.SendAsync(request);");
        builder.AppendLine("var body = await response.Content.ReadAsStringAsync();");
        builder.AppendLine("Console.WriteLine((int)response.StatusCode);");
        builder.Append("Console.WriteLine(body);");

        return builder.ToString();
    }

    public static string ToJavaScript(ExecutedCall call)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"const response = await fetch('{call.Url}', {{");
        builder.AppendLine($"  method: '{call.Method}',");

        if (call.Headers.Count > 0)
        {
            builder.AppendLine("  headers: {");
            var headerLines = call.Headers
                .Select(h => $"    '{h.Key}': '{Redact(h.Key, h.Value)}'")
                .ToList();
            builder.AppendLine(string.Join(",\n", headerLines));
            builder.AppendLine(call.Body is null ? "  }" : "  },");
        }

        if (!string.IsNullOrEmpty(call.Body))
        {
            var escaped = call.Body.Replace("\\", "\\\\").Replace("`", "\\`");
            builder.AppendLine($"  body: `{escaped}`");
        }

        builder.AppendLine("});");
        builder.AppendLine("console.log(response.status);");
        builder.Append("console.log(await response.text());");

        return builder.ToString();
    }

    private static string Redact(string headerName, string value)
    {
        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var space = value.IndexOf(' ');
            return space > 0 ? string.Concat(value.AsSpan(0, space + 1), SecretPlaceholder) : SecretPlaceholder;
        }

        if (headerName.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            headerName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            headerName.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
        {
            return SecretPlaceholder;
        }

        return value;
    }
}

public record ExecutedCall(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    string? ContentType);

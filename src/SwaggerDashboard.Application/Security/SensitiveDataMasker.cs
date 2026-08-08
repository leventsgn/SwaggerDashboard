namespace SwaggerDashboard.Application.Security;

/// <summary>
/// Replaces secret header values before anything is written to the log tables.
/// </summary>
public class SensitiveDataMasker
{
    public const string Mask = "***";

    private readonly HashSet<string> _maskedHeaders;

    public SensitiveDataMasker(IEnumerable<string> maskedHeaderNames)
    {
        _maskedHeaders = new HashSet<string>(maskedHeaderNames, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsSensitive(string headerName) => _maskedHeaders.Contains(headerName);

    /// <summary>
    /// Keeps the scheme of an Authorization value visible so that logs stay diagnosable
    /// ("Bearer ***" rather than a bare mask) while the secret itself never lands.
    /// </summary>
    public string MaskValue(string headerName, string value)
    {
        if (!IsSensitive(headerName))
        {
            return value;
        }

        if (string.IsNullOrEmpty(value))
        {
            return Mask;
        }

        var spaceIndex = value.IndexOf(' ');
        if (spaceIndex > 0 && spaceIndex <= 10)
        {
            return string.Concat(value.AsSpan(0, spaceIndex + 1), Mask);
        }

        return Mask;
    }

    public Dictionary<string, string> MaskHeaders(IDictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers)
        {
            result[name] = MaskValue(name, value);
        }

        return result;
    }
}

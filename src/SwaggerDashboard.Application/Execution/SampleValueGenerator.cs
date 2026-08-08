using System.Globalization;
using System.Text.RegularExpressions;
using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// Produces a plausible test value for a schema field.
/// </summary>
/// <remarks>
/// Used only by the explicit "fill with sample data" action, never automatically: a request
/// that quietly carries invented values is worse than an empty form, because the user cannot
/// tell what they are about to send. Generated values are deliberately recognisable as test
/// data and use the reserved documentation ranges (example.com, 192.0.2.0/24) so that a
/// sample that escapes into a real system points nowhere.
/// </remarks>
public static class SampleValueGenerator
{
    /// <summary>A pattern is only worth checking for so long; a pathological one is treated as unmatched.</summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Returns a value for the field, or null when no honest value can be produced.
    /// </summary>
    public static string? Generate(FieldSchema schema, string? fieldName = null)
    {
        // A declared enum is the only truly safe choice, so it wins over everything.
        if (schema.Enum.Count > 0)
        {
            return schema.Enum[0];
        }

        if (schema.Format is "binary")
        {
            // Files come from the upload control, not from generated text.
            return null;
        }

        var candidate = Candidate(schema, fieldName);
        if (candidate is null)
        {
            return null;
        }

        candidate = ApplyLength(candidate, schema);

        // The generator cannot reason about arbitrary regexes, so it proposes values and
        // checks them. A field whose pattern nothing on offer satisfies is left empty rather
        // than filled with something the target API will reject.
        if (!string.IsNullOrEmpty(schema.Pattern) && !Matches(candidate, schema.Pattern))
        {
            return FromPattern(schema);
        }

        return candidate;
    }

    /// <summary>
    /// Last resort for a pattern-constrained field: try plain digit and letter runs of every
    /// plausible length and keep the first one the pattern accepts.
    /// </summary>
    /// <remarks>
    /// This is trial and error, not regex analysis, which is why the result is always
    /// verified against the pattern itself. It covers the fixed-length cases that show up
    /// constantly in real documents — postal codes, numeric identifiers, country codes —
    /// without pretending to understand arbitrary expressions.
    /// </remarks>
    private static string? FromPattern(FieldSchema schema)
    {
        const int maxLength = 16;

        var lower = schema.MinLength is { } min && min > 0 ? min : 1;
        var upper = schema.MaxLength is { } max && max > 0 ? Math.Min(max, maxLength) : maxLength;

        for (var length = lower; length <= upper; length++)
        {
            foreach (var seed in new[] { '1', 'a', 'A' })
            {
                var candidate = new string(seed, length);

                if (Matches(candidate, schema.Pattern!))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? Candidate(FieldSchema schema, string? fieldName)
    {
        switch (schema.Format)
        {
            case "uuid": return Guid.NewGuid().ToString();
            case "email": return "ornek@example.com";
            case "date": return DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case "date-time": return DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            case "password": return "Ornek-Parola-1";
            case "uri" or "url": return "https://example.com/ornek";
            case "hostname": return "example.com";
            case "ipv4": return "192.0.2.1";
            case "ipv6": return "2001:db8::1";
            case "byte": return Convert.ToBase64String("ornek"u8.ToArray());
        }

        return schema.Type switch
        {
            SchemaTypes.Boolean => "true",
            SchemaTypes.Integer => Integer(schema),
            SchemaTypes.Number => Number(schema),

            // A node that reached here as an object or array is one the generator truncated,
            // so it is edited as raw JSON; an empty container is a usable starting point.
            SchemaTypes.Object => "{}",
            SchemaTypes.Array => "[]",

            _ => ByName(fieldName),
        };
    }

    /// <summary>
    /// Falls back to the field's own name when the schema says nothing but "string".
    /// Only unambiguous hints are honoured; anything else gets a neutral placeholder.
    /// </summary>
    private static string ByName(string? fieldName)
    {
        var name = fieldName?.ToLowerInvariant() ?? string.Empty;

        if (name.Contains("mail"))
        {
            return "ornek@example.com";
        }

        if (name.Contains("phone") || name.Contains("telefon") || name.Contains("gsm"))
        {
            return "+905550000000";
        }

        if (name.Contains("url") || name.Contains("link"))
        {
            return "https://example.com/ornek";
        }

        return "ornek";
    }

    private static string Integer(FieldSchema schema)
    {
        var value = schema.Minimum is { } min ? Math.Ceiling(min) : 1m;

        if (schema.Maximum is { } max && value > max)
        {
            value = Math.Floor(max);
        }

        return ((long)value).ToString(CultureInfo.InvariantCulture);
    }

    private static string Number(FieldSchema schema)
    {
        var value = schema.Minimum ?? 1.5m;

        if (schema.Maximum is { } max && value > max)
        {
            value = max;
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Pads a value up to minLength and trims it down to maxLength.</summary>
    private static string ApplyLength(string candidate, FieldSchema schema)
    {
        if (schema.Type != SchemaTypes.String && schema.Type != SchemaTypes.Unknown)
        {
            return candidate;
        }

        if (schema.MaxLength is { } max && max > 0 && candidate.Length > max)
        {
            candidate = candidate[..max];
        }

        if (schema.MinLength is { } min && candidate.Length < min)
        {
            candidate = candidate.PadRight(min, 'x');

            if (schema.MaxLength is { } cap && cap > 0 && candidate.Length > cap)
            {
                candidate = candidate[..cap];
            }
        }

        return candidate;
    }

    private static bool Matches(string candidate, string pattern)
    {
        try
        {
            return Regex.IsMatch(candidate, pattern, RegexOptions.None, PatternTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            // An invalid or pathological pattern means the value cannot be vouched for.
            return false;
        }
    }
}

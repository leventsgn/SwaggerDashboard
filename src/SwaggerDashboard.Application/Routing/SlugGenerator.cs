using System.Text;

namespace SwaggerDashboard.Application.Routing;

/// <summary>
/// Builds the stable, URL safe identifier used in shareable endpoint links such as
/// <c>/api.company.com/swagger/endpoint/get-customer-by-id</c>.
/// </summary>
/// <remarks>
/// operationId is optional in OpenAPI and may contain characters that are unsafe in a
/// path, so it is only a preferred source: the method and path always provide a fallback,
/// and collisions get a numeric suffix.
/// </remarks>
public static class SlugGenerator
{
    private const int MaxSlugLength = 120;

    public static string Create(string method, string path, string? operationId, ISet<string> taken)
    {
        var candidate = Slugify(operationId);

        if (string.IsNullOrEmpty(candidate))
        {
            candidate = Slugify($"{method}-{path}");
        }

        if (string.IsNullOrEmpty(candidate))
        {
            candidate = method.ToLowerInvariant();
        }

        var unique = candidate;
        var counter = 2;
        while (!taken.Add(unique))
        {
            unique = $"{candidate}-{counter++}";
        }

        return unique;
    }

    /// <summary>
    /// Lowercases and strips everything that is not an unreserved URL character.
    /// Path templates lose their braces, so /customers/{id} becomes customers-id, and
    /// camel case words are split, so getCustomerById becomes get-customer-by-id.
    /// </summary>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        var lastWasSeparator = true;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (!lastWasSeparator && StartsNewWord(value, i))
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');

        return slug.Length > MaxSlugLength ? slug[..MaxSlugLength].TrimEnd('-') : slug;
    }

    /// <summary>
    /// True when the character begins a new camel case word: an upper case letter after a
    /// lower case one or a digit (getCustomer), or the last upper case letter of a run that
    /// is followed by a lower case one (APIKey becomes api-key rather than a-p-i-key).
    /// </summary>
    private static bool StartsNewWord(string value, int index)
    {
        if (index == 0 || !char.IsAsciiLetterUpper(value[index]))
        {
            return false;
        }

        var previous = value[index - 1];

        if (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous))
        {
            return true;
        }

        return char.IsAsciiLetterUpper(previous) &&
               index + 1 < value.Length &&
               char.IsAsciiLetterLower(value[index + 1]);
    }
}

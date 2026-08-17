namespace SwaggerDashboard.Application.Routing;

/// <summary>
/// Case folding for the endpoint search box.
/// </summary>
/// <remarks>
/// <see cref="StringComparison.OrdinalIgnoreCase"/> folds ASCII case only, and Turkish has
/// four letters in the I family rather than two: typing "İSTEK" or "ıstek" matched nothing at
/// all against "istek", because U+0130 and U+0131 fold to themselves and never to "i". The
/// whole family is collapsed onto plain "i" here, so all four spellings find each other. That
/// is deliberately looser than a linguistic comparison — a search box is meant to find things,
/// and a Turkish user who types the dotless letter still means the word.
/// </remarks>
public static class SearchText
{
    public static bool Contains(string? value, string term)
    {
        if (value is null)
        {
            return false;
        }

        return Normalize(value).Contains(Normalize(term), StringComparison.Ordinal);
    }

    public static string Normalize(string value)
    {
        var folded = string.Create(value.Length, value, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = source[index] switch
                {
                    'İ' or 'I' or 'ı' => 'i',
                    var other => other,
                };
            }
        });

        return folded.ToLowerInvariant();
    }
}

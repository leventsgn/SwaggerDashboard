using System.Text;
using System.Text.Json;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// Finds the token in whatever a login endpoint answered with.
/// </summary>
/// <remarks>
/// A login endpoint is not a standard, so there is no field name to rely on. Rather than
/// making every user describe their API's response before they can make a single call, the
/// well known spellings are recognised and the user only has to say something when the guess
/// misses. The search is deliberately shallow and name-driven: picking "the longest string in
/// the document" would find a token most of the time and silently send a user's full name as
/// a bearer the rest of the time.
/// </remarks>
public static class TokenResponseReader
{
    /// <summary>Field names that hold a token, in the order they are preferred.</summary>
    private static readonly string[] TokenNames =
    [
        "access_token",
        "accessToken",
        "token",
        "jwt",
        "id_token",
        "idToken",
        "authToken",
        "auth_token",
    ];

    /// <summary>Wrappers an API commonly puts its payload inside.</summary>
    private static readonly string[] EnvelopeNames = ["data", "result", "payload", "response", "d"];

    private static readonly string[] LifetimeNames = ["expires_in", "expiresIn", "expires", "ttl"];

    /// <summary>Used when the response says nothing about how long the token lasts.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reads the token and how long it is good for.
    /// </summary>
    /// <param name="body">The login response.</param>
    /// <param name="explicitField">
    /// The field the user named, optionally dotted (<c>data.accessToken</c>). When given, only
    /// that path is read: a user who had to name the field is correcting a wrong guess, and
    /// guessing again after being corrected would be worse than failing.
    /// </param>
    public static bool TryRead(
        string? body,
        string? explicitField,
        out string? token,
        out TimeSpan lifetime)
    {
        token = null;
        lifetime = DefaultLifetime;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        // A response that is nothing but the token itself, quoted or bare, is common enough
        // in hand written APIs to be worth handling before parsing as an object.
        var trimmed = body.Trim();

        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            token = trimmed.Trim('"');
            lifetime = LifetimeFromJwt(token) ?? DefaultLifetime;
            return token.Length > 0;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(explicitField))
            {
                if (!TryFollowPath(root, explicitField, out token))
                {
                    return false;
                }
            }
            else if (!TryFindToken(root, out token))
            {
                return false;
            }

            lifetime = LifetimeFrom(root) ?? LifetimeFromJwt(token) ?? DefaultLifetime;
            return !string.IsNullOrEmpty(token);
        }
    }

    private static bool TryFollowPath(JsonElement root, string path, out string? value)
    {
        value = null;
        var current = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
            {
                return false;
            }

            current = next;
        }

        value = current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryFindToken(JsonElement element, out string? token)
    {
        token = null;

        foreach (var name in TokenNames)
        {
            if (element.TryGetProperty(name, out var candidate) &&
                candidate.ValueKind == JsonValueKind.String)
            {
                token = candidate.GetString();

                if (!string.IsNullOrEmpty(token))
                {
                    return true;
                }
            }
        }

        // One level of unwrapping only. Descending further would eventually find a string
        // called "token" somewhere unrelated in a large response.
        foreach (var envelope in EnvelopeNames)
        {
            if (element.TryGetProperty(envelope, out var inner) &&
                inner.ValueKind == JsonValueKind.Object &&
                TryFindToken(inner, out token))
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan? LifetimeFrom(JsonElement root)
    {
        foreach (var name in LifetimeNames)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), out var parsed) &&
                parsed > 0)
            {
                return TimeSpan.FromSeconds(parsed);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the expiry out of a JWT's payload.
    /// </summary>
    /// <remarks>
    /// The token is not verified and nothing is trusted from it beyond a cache duration: this
    /// only decides how long the platform reuses the token before signing in again. A forged
    /// exp would at worst cause an extra login or one rejected call.
    /// </remarks>
    private static TimeSpan? LifetimeFromJwt(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var parts = token.Split('.');

        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

            if (!document.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds))
            {
                return null;
            }

            var remaining = DateTimeOffset.FromUnixTimeSeconds(seconds) - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

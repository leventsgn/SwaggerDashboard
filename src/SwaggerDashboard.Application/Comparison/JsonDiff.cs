using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace SwaggerDashboard.Application.Comparison;

public enum JsonDiffKind
{
    /// <summary>Present in the new response only.</summary>
    Added,

    /// <summary>Present in the old response only.</summary>
    Removed,

    /// <summary>Present in both, with a different value or type.</summary>
    Changed,
}

public record JsonDiffEntry(string Path, JsonDiffKind Kind, string? Left, string? Right);

public record JsonDiffResult(IReadOnlyList<JsonDiffEntry> Entries, bool Truncated, string? Unsupported)
{
    public bool AreEqual => Unsupported is null && Entries.Count == 0;

    public static JsonDiffResult NotJson(string reason) => new([], false, reason);
}

/// <summary>
/// Compares two JSON bodies field by field.
/// </summary>
/// <remarks>
/// A structural comparison rather than a text one. Two responses that differ only in key
/// order or whitespace are the same response, and a text diff would report every line of a
/// re-serialized body as changed — which is exactly the case where the user is looking for
/// the one field that really moved.
/// </remarks>
public static class JsonDiff
{
    /// <summary>
    /// Enough to see what changed, bounded so a comparison of two large collections cannot
    /// produce a page nobody can read (or a payload that stalls the circuit).
    /// </summary>
    public const int MaxEntries = 300;

    public static JsonDiffResult Compare(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return JsonDiffResult.NotJson("Karşılaştırmak için iki yanıt gövdesi de gerekli.");
        }

        JsonNode? leftNode;
        JsonNode? rightNode;

        try
        {
            leftNode = JsonNode.Parse(left);
            rightNode = JsonNode.Parse(right);
        }
        catch (JsonException)
        {
            return JsonDiffResult.NotJson(
                "Yanıtlardan biri JSON değil; alan bazlı karşılaştırma yapılamıyor.");
        }

        var entries = new List<JsonDiffEntry>();
        var truncated = !Walk("$", leftNode, rightNode, entries);

        return new JsonDiffResult(entries, truncated, null);
    }

    /// <summary>Returns false once the entry budget is spent.</summary>
    private static bool Walk(string path, JsonNode? left, JsonNode? right, List<JsonDiffEntry> entries)
    {
        if (entries.Count >= MaxEntries)
        {
            return false;
        }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            return WalkObject(path, leftObject, rightObject, entries);
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            return WalkArray(path, leftArray, rightArray, entries);
        }

        var leftText = Render(left);
        var rightText = Render(right);

        if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
        {
            entries.Add(new JsonDiffEntry(path, JsonDiffKind.Changed, leftText, rightText));
        }

        return true;
    }

    private static bool WalkObject(
        string path,
        JsonObject left,
        JsonObject right,
        List<JsonDiffEntry> entries)
    {
        // Union of both key sets, in the order the old response listed them first: a field
        // that disappeared should not fall to the bottom of the report.
        var keys = left.Select(p => p.Key)
            .Concat(right.Select(p => p.Key).Where(k => !left.ContainsKey(k)))
            .ToList();

        foreach (var key in keys)
        {
            if (entries.Count >= MaxEntries)
            {
                return false;
            }

            var childPath = $"{path}.{key}";
            var inLeft = left.TryGetPropertyValue(key, out var leftChild);
            var inRight = right.TryGetPropertyValue(key, out var rightChild);

            if (inLeft && !inRight)
            {
                entries.Add(new JsonDiffEntry(childPath, JsonDiffKind.Removed, Render(leftChild), null));
                continue;
            }

            if (!inLeft && inRight)
            {
                entries.Add(new JsonDiffEntry(childPath, JsonDiffKind.Added, null, Render(rightChild)));
                continue;
            }

            if (!Walk(childPath, leftChild, rightChild, entries))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares arrays position by position.
    /// </summary>
    /// <remarks>
    /// Matching elements by identity would need a key the document does not give us, and
    /// guessing one (id? first field?) would produce a confident but wrong answer on the
    /// documents where it guessed badly. Position is at least a rule the reader can predict.
    /// </remarks>
    private static bool WalkArray(
        string path,
        JsonArray left,
        JsonArray right,
        List<JsonDiffEntry> entries)
    {
        var shared = Math.Min(left.Count, right.Count);

        for (var i = 0; i < shared; i++)
        {
            if (!Walk($"{path}[{i}]", left[i], right[i], entries))
            {
                return false;
            }
        }

        for (var i = shared; i < left.Count; i++)
        {
            if (entries.Count >= MaxEntries)
            {
                return false;
            }

            entries.Add(new JsonDiffEntry($"{path}[{i}]", JsonDiffKind.Removed, Render(left[i]), null));
        }

        for (var i = shared; i < right.Count; i++)
        {
            if (entries.Count >= MaxEntries)
            {
                return false;
            }

            entries.Add(new JsonDiffEntry($"{path}[{i}]", JsonDiffKind.Added, null, Render(right[i])));
        }

        return true;
    }

    /// <summary>
    /// The default encoder escapes everything outside ASCII, which turns a changed Turkish
    /// value into a row of escape sequences — unreadable in exactly the column the user
    /// opened this tab to read.
    /// </summary>
    private static readonly JsonSerializerOptions RenderOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Compact one line rendering, clipped so a nested object stays one row.</summary>
    private static string? Render(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        var text = node.ToJsonString(RenderOptions);

        return text.Length > 200 ? string.Concat(text.AsSpan(0, 200), "…") : text;
    }
}

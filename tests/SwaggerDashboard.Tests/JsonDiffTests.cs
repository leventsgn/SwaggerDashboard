using SwaggerDashboard.Application.Comparison;
using Xunit;

namespace SwaggerDashboard.Tests;

public class JsonDiffTests
{
    [Fact]
    public void Two_identical_documents_have_no_differences()
    {
        var result = JsonDiff.Compare("""{"ad":"Ada","yas":31}""", """{"ad":"Ada","yas":31}""");

        Assert.True(result.AreEqual);
    }

    [Fact]
    public void Key_order_and_whitespace_are_not_differences()
    {
        // This is the whole reason the comparison is structural: a text diff would report a
        // re-serialized but identical body as changed from top to bottom.
        var result = JsonDiff.Compare(
            """{"ad":"Ada","yas":31}""",
            """
            {
              "yas": 31,
              "ad": "Ada"
            }
            """);

        Assert.True(result.AreEqual);
    }

    [Fact]
    public void A_changed_value_is_reported_with_both_sides()
    {
        var result = JsonDiff.Compare("""{"durum":"aktif"}""", """{"durum":"pasif"}""");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("$.durum", entry.Path);
        Assert.Equal(JsonDiffKind.Changed, entry.Kind);
        Assert.Equal("\"aktif\"", entry.Left);
        Assert.Equal("\"pasif\"", entry.Right);
    }

    [Fact]
    public void An_added_and_a_removed_field_are_told_apart()
    {
        var result = JsonDiff.Compare("""{"eski":1}""", """{"yeni":2}""");

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(JsonDiffKind.Removed, result.Entries.Single(e => e.Path == "$.eski").Kind);
        Assert.Equal(JsonDiffKind.Added, result.Entries.Single(e => e.Path == "$.yeni").Kind);
    }

    [Fact]
    public void Nested_paths_point_at_the_field_that_moved()
    {
        var result = JsonDiff.Compare(
            """{"musteri":{"adres":{"sehir":"Ankara"}}}""",
            """{"musteri":{"adres":{"sehir":"İzmir"}}}""");

        Assert.Equal("$.musteri.adres.sehir", Assert.Single(result.Entries).Path);
    }

    [Fact]
    public void Array_elements_are_compared_by_position()
    {
        var result = JsonDiff.Compare("""{"etiketler":["a","b"]}""", """{"etiketler":["a","c"]}""");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("$.etiketler[1]", entry.Path);
    }

    [Fact]
    public void A_longer_array_reports_the_extra_elements_as_added()
    {
        var result = JsonDiff.Compare("""[1]""", """[1,2,3]""");

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(JsonDiffKind.Added, e.Kind));
        Assert.Equal(["$[1]", "$[2]"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public void A_shorter_array_reports_the_missing_elements_as_removed()
    {
        var result = JsonDiff.Compare("""[1,2,3]""", """[1]""");

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(JsonDiffKind.Removed, e.Kind));
    }

    [Fact]
    public void A_type_change_counts_as_a_change()
    {
        var result = JsonDiff.Compare("""{"sayi":1}""", """{"sayi":"1"}""");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("1", entry.Left);
        Assert.Equal("\"1\"", entry.Right);
    }

    [Fact]
    public void A_null_that_becomes_a_value_is_a_change()
    {
        var result = JsonDiff.Compare("""{"not":null}""", """{"not":"var"}""");

        Assert.Equal("null", Assert.Single(result.Entries).Left);
    }

    [Fact]
    public void Turkish_text_is_shown_as_itself_rather_than_as_escapes()
    {
        // The column exists to be read: \u011F sequences in the one value that changed defeat
        // the purpose of showing it.
        var result = JsonDiff.Compare("""{"ad":"ornek"}""", """{"ad":"Değişmiş İsim"}""");

        Assert.Equal("\"Değişmiş İsim\"", Assert.Single(result.Entries).Right);
    }

    [Fact]
    public void A_body_that_is_not_json_is_reported_rather_than_compared_as_text()
    {
        var result = JsonDiff.Compare("<html>eski</html>", "<html>yeni</html>");

        Assert.NotNull(result.Unsupported);
        Assert.Empty(result.Entries);
        Assert.False(result.AreEqual);
    }

    [Fact]
    public void A_missing_body_is_reported_rather_than_treated_as_equal()
    {
        // An empty comparison must not read as "nothing changed".
        var result = JsonDiff.Compare(null, """{"a":1}""");

        Assert.NotNull(result.Unsupported);
        Assert.False(result.AreEqual);
    }

    [Fact]
    public void A_very_large_difference_is_capped_and_says_so()
    {
        var left = "[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"{{\"v\":{i}}}")) + "]";
        var right = "[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"{{\"v\":{i + 1}}}")) + "]";

        var result = JsonDiff.Compare(left, right);

        Assert.True(result.Truncated);
        Assert.Equal(JsonDiff.MaxEntries, result.Entries.Count);
    }
}

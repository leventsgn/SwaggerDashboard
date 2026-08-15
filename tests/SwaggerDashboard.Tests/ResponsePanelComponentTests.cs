using Bunit;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Web.Components.Shared;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Renders the response panel: status, body views, request echo and the comparison tab.
/// </summary>
public class ResponsePanelComponentTests : TestContext
{
    public ResponsePanelComponentTests()
    {
        // The panel calls into JS only to write the sandboxed HTML preview; the tests are
        // about what it renders, not about that call.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ProxyResponse Response(
        int statusCode = 200,
        string? body = """{"ad":"Ada"}""",
        string contentType = "application/json") => new()
    {
        Success = statusCode is >= 200 and < 300,
        StatusCode = statusCode,
        ReasonPhrase = statusCode == 200 ? "OK" : null,
        RequestUrl = "https://api.company.com/v1/customers",
        RequestMethod = "GET",
        ResponseBody = body,
        ContentType = contentType,
        ContentLength = body?.Length ?? 0,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private IRenderedComponent<ResponsePanel> Render(
        ProxyResponse? response, ProxyResponse? previous = null, bool executing = false) =>
        RenderComponent<ResponsePanel>(p => p
            .Add(c => c.Response, response)
            .Add(c => c.Previous, previous)
            .Add(c => c.Executing, executing));

    private static void OpenTab(IRenderedComponent<ResponsePanel> component, string name) =>
        component.FindAll(".sd-tabs button").Single(b => b.TextContent.Trim() == name).Click();

    [Fact]
    public void Nothing_sent_yet_says_so()
    {
        Assert.Contains("Henüz istek gönderilmedi", Render(null).Markup);
    }

    [Fact]
    public void A_running_call_shows_progress_rather_than_the_previous_answer()
    {
        Assert.Contains("İstek gönderiliyor", Render(null, executing: true).Markup);
    }

    [Theory]
    [InlineData(200, "s-ok")]
    [InlineData(302, "s-redirect")]
    [InlineData(404, "s-client")]
    [InlineData(500, "s-server")]
    [InlineData(0, "s-error")]
    public void The_status_carries_a_class_matching_its_class_of_code(int statusCode, string expected)
    {
        var component = Render(Response(statusCode));

        Assert.Contains(expected, component.Find(".sd-status").ClassName);
    }

    [Fact]
    public void A_transport_failure_shows_the_error_instead_of_a_status_code()
    {
        var component = Render(Response(0) with { Error = "Hedef adrese ulaşılamadı" });

        Assert.Contains("HATA", component.Find(".sd-status").TextContent);
        Assert.Contains("Hedef adrese ulaşılamadı", component.Markup);
    }

    [Fact]
    public void A_json_body_is_pretty_printed_with_turkish_text_intact()
    {
        // The default JSON encoder escapes non-ASCII, which would show a Turkish response as
        // a wall of \u sequences in the one view meant for reading it.
        var component = Render(Response(body: """{"ad":"Ada Yılmaz"}"""));

        Assert.Contains("Ada Yılmaz", component.Find(".sd-pre").TextContent);
    }

    [Fact]
    public void A_non_json_body_is_reported_in_the_tree_tab_rather_than_failing()
    {
        var component = Render(Response(body: "düz metin", contentType: "text/plain"));

        OpenTab(component, "JSON Tree");

        Assert.Contains("Yanıt JSON değil", component.Markup);
    }

    [Fact]
    public void A_binary_response_offers_a_download_instead_of_a_preview()
    {
        var component = Render(Response(body: null, contentType: "application/pdf") with
        {
            IsBinary = true,
            DownloadToken = "token-1",
            FileName = "rapor.pdf",
            ContentLength = 2048,
        });

        var link = component.Find("a[href='/download/token-1']");

        Assert.Contains("rapor.pdf", link.TextContent);
        Assert.Contains("tek kullanımlık", component.Markup);
    }

    [Fact]
    public void The_request_tab_masks_the_authorization_header()
    {
        // The panel echoes what was sent; echoing the token back would put it on screen and
        // into any screenshot of it.
        var response = Response();
        response.RequestHeaders["Authorization"] = "Bearer cok-gizli-token";

        var component = Render(response);

        OpenTab(component, "Request");

        Assert.Contains("Bearer ***", component.Markup);
        Assert.DoesNotContain("cok-gizli-token", component.Markup);
    }

    [Fact]
    public void The_comparison_tab_asks_for_a_second_run_when_there_is_nothing_to_compare()
    {
        var component = Render(Response());

        OpenTab(component, "Fark");

        Assert.Contains("bir kez daha çalıştırın", component.Markup);
    }

    [Fact]
    public void Two_identical_bodies_are_reported_as_equal()
    {
        var component = Render(Response(), Response());

        OpenTab(component, "Fark");

        Assert.Contains("alan bazında aynı", component.Markup);
    }

    [Fact]
    public void A_changed_field_is_listed_with_both_values()
    {
        var component = Render(
            Response(body: """{"durum":"pasif"}"""),
            Response(body: """{"durum":"aktif"}"""));

        OpenTab(component, "Fark");

        var row = component.Find(".sd-diff-table tbody tr").TextContent;
        Assert.Contains("$.durum", row);
        Assert.Contains("aktif", row);
        Assert.Contains("pasif", row);
    }

    [Fact]
    public void A_changed_status_code_is_called_out_on_its_own()
    {
        // Two bodies can be identical while the call went from 200 to 500; the codes are the
        // first thing the comparison should say.
        var component = Render(Response(500, body: """{"a":1}"""), Response(200, body: """{"a":1}"""));

        OpenTab(component, "Fark");

        Assert.Contains("Durum kodu değişti", component.Markup);
    }

    [Fact]
    public void The_generated_code_tab_replaces_secrets_with_a_placeholder()
    {
        var response = Response();
        response.RequestHeaders["Authorization"] = "Bearer cok-gizli-token";

        var component = Render(response);

        OpenTab(component, "Kod");

        Assert.DoesNotContain("cok-gizli-token", component.Markup);
        Assert.Contains("curl", component.Find(".sd-pre").TextContent);
    }
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Comparison;
using SwaggerDashboard.Application.Execution;

namespace SwaggerDashboard.Web.Components.Shared;

/// <summary>
/// Code-behind for the response panel.
/// </summary>
/// <remarks>
/// The logic lives here rather than in an @code block because the Razor parser mis-reads
/// relational patterns such as "&lt; 300" in switch expressions as the start of markup.
/// </remarks>
public partial class ResponsePanel : IDisposable
{

    [Parameter]
    public ProxyResponse? Response { get; set; }

    /// <summary>
    /// The response this endpoint returned before the current one, if any.
    /// </summary>
    [Parameter]
    public ProxyResponse? Previous { get; set; }

    [Parameter]
    public bool Executing { get; set; }

    private static readonly string[] Tabs =
        ["Preview", "JSON Tree", "Raw", "Fark", "Headers", "Request", "Timing", "Kod"];

    private string _tab = "Preview";
    private string _codeLanguage = "cURL";
    private bool _codeCopied;
    private JsonElement? _jsonRoot;
    private JsonDocument? _jsonDocument;
    private string? _renderedHtmlFor;

    private bool IsHtml =>
        Response?.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;

    private string PrettyBody => FormatJson(Response?.ResponseBody);

    /// <summary>
    /// Comparison of the previous response's body with the current one.
    /// </summary>
    /// <remarks>
    /// Computed once per parameter change rather than per render: the diff walks both
    /// documents, and a property read from markup would repeat that on every re-render.
    /// </remarks>
    private JsonDiffResult Diff { get; set; } = new([], false, null);

    private static string DiffClass(JsonDiffKind kind) => kind switch
    {
        JsonDiffKind.Added => "sd-diff-added",
        JsonDiffKind.Removed => "sd-diff-removed",
        _ => "sd-diff-changed",
    };

    protected override void OnParametersSet()
    {
        _codeCopied = false;
        ParseJson();

        Diff = Previous is null || Response is null
            ? new JsonDiffResult([], false, null)
            : JsonDiff.Compare(Previous.ResponseBody, Response.ResponseBody);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_tab != "Preview" || !IsHtml || Response?.ResponseBody is null)
        {
            return;
        }

        // Re-rendering the frame on every render would reload it constantly, so it is only
        // written when the body actually changed.
        if (_renderedHtmlFor == Response.RequestUrl + Response.StartedAt.Ticks)
        {
            return;
        }

        _renderedHtmlFor = Response.RequestUrl + Response.StartedAt.Ticks;
        await Js.InvokeVoidAsync("swaggerDashboard.renderSandboxed", "sd-html-preview", Response.ResponseBody);
    }

    private void ParseJson()
    {
        _jsonDocument?.Dispose();
        _jsonDocument = null;
        _jsonRoot = null;

        var body = Response?.ResponseBody;
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            _jsonDocument = JsonDocument.Parse(body);
            _jsonRoot = _jsonDocument.RootElement;
        }
        catch (JsonException)
        {
            // Not JSON; the tree tab reports that instead of failing the panel.
        }
    }

    /// <summary>
    /// Pretty prints a JSON body for display.
    /// </summary>
    /// <remarks>
    /// The default encoder escapes everything outside ASCII, which turns Turkish text into
    /// \u0131 sequences in the preview. Allowing the full Unicode range keeps the body
    /// readable; HTML sensitive characters stay escaped, and Blazor encodes the output
    /// again when it renders it.
    /// </remarks>
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static string FormatJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document.RootElement, DisplayOptions);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private string GeneratedCode
    {
        get
        {
            if (Response is null)
            {
                return string.Empty;
            }

            var call = new ExecutedCall(
                Response.RequestMethod,
                Response.RequestUrl,
                Response.RequestHeaders,
                Response.RequestBody,
                Response.RequestContentType);

            return _codeLanguage switch
            {
                "C#" => CodeSnippetGenerator.ToCSharp(call),
                "JavaScript" => CodeSnippetGenerator.ToJavaScript(call),
                _ => CodeSnippetGenerator.ToCurl(call),
            };
        }
    }

    private async Task CopyCode()
    {
        _codeCopied = await Js.InvokeAsync<bool>("swaggerDashboard.copyToClipboard", GeneratedCode);
    }

    private static string MaskIfSecret(string name, string value)
    {
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
        {
            var space = value.IndexOf(' ');
            return space > 0 ? string.Concat(value.AsSpan(0, space + 1), "***") : "***";
        }

        return value;
    }

    /// <summary>
    /// Shared with the endpoint screen's history list so a status reads the same colour
    /// wherever it appears.
    /// </summary>
    internal static string StatusClass(int statusCode) => statusCode switch
    {
        0 => "s-error",
        >= 200 and < 300 => "s-ok",
        >= 300 and < 400 => "s-redirect",
        >= 400 and < 500 => "s-client",
        _ => "s-server",
    };

    public void Dispose() => _jsonDocument?.Dispose();
}

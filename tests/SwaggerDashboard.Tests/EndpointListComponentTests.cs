using Bunit;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Web.Components.Shared;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Renders the endpoint list the way the browser does.
/// </summary>
/// <remarks>
/// The filtering rules live in the component, so until now they were only covered by driving
/// a real browser — which is slow and needs the whole application running. These render the
/// component alone and assert on the markup it produces.
/// </remarks>
public class EndpointListComponentTests : TestContext
{
    private static DashboardOperation Operation(
        string method,
        string path,
        string tag = "Customers",
        string? summary = null,
        bool deprecated = false,
        bool requiresAuth = false) => new()
    {
        Slug = $"{method}-{path}".ToLowerInvariant().Replace('/', '-').Trim('-'),
        Method = method,
        Path = path,
        Tag = tag,
        Summary = summary,
        Deprecated = deprecated,

        // RequiresAuthentication is derived from this list rather than set directly.
        SecuritySchemeKeys = requiresAuth ? ["bearerAuth"] : [],
    };

    private static DashboardDocument Dashboard(params DashboardOperation[] operations) => new()
    {
        Title = "Customer API",
        Operations = [.. operations],
    };

    private IRenderedComponent<EndpointList> Render(
        DashboardDocument dashboard,
        Action<ComponentParameterCollectionBuilder<EndpointList>>? configure = null) =>
        RenderComponent<EndpointList>(parameters =>
        {
            parameters.Add(p => p.Dashboard, dashboard);
            parameters.Add(p => p.Definition, new ApiDefinition
            {
                Id = 1,
                Name = "Customer API",
                ApiVersion = "1.4.0",
            });

            configure?.Invoke(parameters);
        });

    private static IReadOnlyList<string> Paths(IRenderedComponent<EndpointList> component) =>
        component.FindAll(".sd-endpoints .sd-path").Select(e => e.TextContent.Trim()).ToList();

    [Fact]
    public void Endpoints_are_grouped_under_their_tag()
    {
        var component = Render(Dashboard(
            Operation("GET", "/customers"),
            Operation("POST", "/payments", tag: "Payments")));

        var groups = component.FindAll(".sd-endpoints summary").Select(e => e.TextContent).ToList();

        Assert.Contains(groups, g => g.Contains("Customers"));
        Assert.Contains(groups, g => g.Contains("Payments"));
    }

    [Fact]
    public void Searching_matches_the_path_and_the_summary()
    {
        var component = Render(Dashboard(
            Operation("GET", "/customers", summary: "Müşterileri listeler"),
            Operation("GET", "/payments", summary: "Ödemeleri listeler")));

        component.Find(".sd-search").Input("payment");
        Assert.Equal(["/payments"], Paths(component));

        component.Find(".sd-search").Input("Müşterileri");
        Assert.Equal(["/customers"], Paths(component));
    }

    [Fact]
    public void Search_ignores_case_including_the_turkish_dotted_i()
    {
        // "İSTEK" lowercased with the invariant rules is not "istek", so a naive comparison
        // drops matches a Turkish user expects to find.
        var component = Render(Dashboard(Operation("GET", "/istekler", summary: "İstek listesi")));

        component.Find(".sd-search").Input("istek");

        Assert.Single(Paths(component));
    }

    [Fact]
    public void A_method_chip_narrows_the_list_and_toggles_off_again()
    {
        var component = Render(Dashboard(
            Operation("GET", "/customers"),
            Operation("POST", "/customers"),
            Operation("DELETE", "/customers/{id}")));

        // The element is re-found before each click: a re-render replaces the node, and the
        // stale reference has no event handler attached any more.
        void ClickPost() =>
            component.FindAll(".sd-method-chip").Single(c => c.TextContent.Trim() == "POST").Click();

        ClickPost();
        Assert.Single(Paths(component));

        ClickPost();
        Assert.Equal(3, Paths(component).Count);
    }

    [Fact]
    public void The_deprecated_and_auth_filters_apply_together()
    {
        var component = Render(Dashboard(
            Operation("GET", "/a", deprecated: true),
            Operation("GET", "/b", requiresAuth: true),
            Operation("GET", "/c", deprecated: true, requiresAuth: true)));

        component.FindAll(".sd-filters input[type=checkbox]")[0].Change(true);
        Assert.Equal(2, Paths(component).Count);

        component.FindAll(".sd-filters input[type=checkbox]")[1].Change(true);
        Assert.Equal(["/c"], Paths(component));
    }

    [Fact]
    public void Favourites_and_recents_are_listed_above_the_tag_groups()
    {
        // They are what a returning user reaches for first, so they lead rather than being
        // buried in whichever group they belong to.
        var favourite = Operation("GET", "/customers");
        var recent = Operation("POST", "/payments", tag: "Payments");

        var component = Render(
            Dashboard(favourite, recent),
            p =>
            {
                p.Add(c => c.Favorites, new[] { favourite.Slug });
                p.Add(c => c.Recents, new[] { recent.Slug });
            });

        var summaries = component.FindAll(".sd-endpoints summary").Select(e => e.TextContent).ToList();

        Assert.Contains("Favoriler", summaries[0]);
        Assert.Contains("Son kullanılanlar", summaries[1]);
    }

    [Fact]
    public void The_favourites_filter_only_appears_when_there_are_any()
    {
        var operation = Operation("GET", "/customers");

        Assert.DoesNotContain("Favori", Render(Dashboard(operation)).Markup);

        var withFavourite = Render(Dashboard(operation), p => p.Add(c => c.Favorites, new[] { operation.Slug }));
        Assert.Contains("Favori", withFavourite.Markup);
    }

    [Fact]
    public void Selecting_an_endpoint_reports_its_slug()
    {
        var operation = Operation("GET", "/customers");
        string? selected = null;

        var component = Render(Dashboard(operation), p => p.Add(c => c.OnSelect, slug => selected = slug));

        component.FindAll(".sd-endpoint")[0].Click();

        Assert.Equal(operation.Slug, selected);
    }

    [Fact]
    public void The_selected_endpoint_is_marked_in_the_markup()
    {
        var operation = Operation("GET", "/customers");

        var component = Render(Dashboard(operation), p => p.Add(c => c.SelectedSlug, operation.Slug));

        Assert.Contains("selected", component.Find(".sd-endpoint").ClassName);
    }

    [Fact]
    public void The_environment_selector_appears_only_with_more_than_one_environment()
    {
        // One environment is not a choice, and a select with a single option is noise.
        var operation = Operation("GET", "/customers");

        var single = Render(Dashboard(operation), p => p.Add(c => c.Environments,
            [new ApiEnvironment { Name = "Default", BaseUrl = "https://api.company.com" }]));
        Assert.Empty(single.FindAll(".sd-left > select"));

        var two = Render(Dashboard(operation), p => p.Add(c => c.Environments,
        [
            new ApiEnvironment { Name = "Default", BaseUrl = "https://api.company.com" },
            new ApiEnvironment { Name = "Test", BaseUrl = "https://test.company.com" },
        ]));
        Assert.Single(two.FindAll(".sd-left > select"));
    }

    [Fact]
    public void An_empty_result_says_so_rather_than_showing_nothing()
    {
        var component = Render(Dashboard(Operation("GET", "/customers")));

        component.Find(".sd-search").Input("bulunmayan");

        Assert.Contains("Eşleşen endpoint yok", component.Markup);
    }
}

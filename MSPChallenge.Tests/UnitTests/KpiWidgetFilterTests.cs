using System.Net;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MSPChallenge_Client_Browser.Components.Pages.KpiDashboardComponents;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge.Tests.UnitTests;

public class KpiWidgetFilterTests : TestContext
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler());
    }

    private readonly BunitJSModuleInterop _module;

    public KpiWidgetFilterTests()
    {
        var apiClient = new MspApiClient(
            new StubHttpClientFactory(),
            new ConfigurationManager(),
            new UserSessionService { GameServerAddress = "http://localhost" });
        Services.AddSingleton(new GameSessionService(apiClient, new WebSocketService(), new GameUIStateService(null!, null!)));

        _module = JSInterop.SetupModule("/js/kpi-dashboard.js");
        _module.Mode = JSRuntimeMode.Loose;
    }

    private static KpiCategoryState CreateCategory(params string[] valueNames) =>
        new(new KpiCategoryDefinition(
                "Biomass",
                ValueDefinitions: valueNames.Select(n => new KpiValueDefinition(n, ValueColor: "#ff0000")).ToList()),
            "MEL");

    private IRenderedComponent<KpiWidget> RenderWidget(KpiCategoryState category, KpiWidgetInstance? instance = null) =>
        RenderComponent<KpiWidget>(p => p
            .Add(w => w.Category, category)
            .Add(w => w.Instance, instance ?? new KpiWidgetInstance(category.CategoryKey)));

    private static string[] LegendLabels(IRenderedComponent<KpiWidget> cut) =>
        cut.FindAll(".kpi-widget-legend-label").Select(e => e.TextContent).ToArray();

    private string[] ChartSeriesNames()
    {
        var payload = (string)_module.Invocations["renderWidget"].Last().Arguments[1]!;
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToArray();
    }

    private static void OpenFilter(IRenderedComponent<KpiWidget> cut) => cut.Find(".kpi-series-filter-btn").Click();

    [Fact]
    public void Initially_AllBarsAreInLegendAndChart()
    {
        var cut = RenderWidget(CreateCategory("Cod", "Herring", "Seals"));

        cut.Find(".kpi-series-filter-btn").TextContent.Should().Contain("All bars");
        LegendLabels(cut).Should().Equal("Cod", "Herring", "Seals");
        ChartSeriesNames().Should().Equal("Cod", "Herring", "Seals");
    }

    [Fact]
    public void UncheckingABar_RemovesItFromLegendAndChart()
    {
        var cut = RenderWidget(CreateCategory("Cod", "Herring", "Seals"));
        OpenFilter(cut);

        cut.FindAll(".kpi-series-filter-item input")[1].Change(false);

        cut.Find(".kpi-series-filter-btn").TextContent.Should().Contain("2 of 3 bars");
        LegendLabels(cut).Should().Equal("Cod", "Seals");
        ChartSeriesNames().Should().Equal("Cod", "Seals");
    }

    [Fact]
    public void NoneThenAll_HidesAndRestoresEveryBar()
    {
        var cut = RenderWidget(CreateCategory("Cod", "Herring", "Seals"));
        OpenFilter(cut);

        cut.FindAll(".kpi-series-filter-action")[1].Click(); // None
        cut.Find(".kpi-series-filter-btn").TextContent.Should().Contain("No bars");
        LegendLabels(cut).Should().BeEmpty();
        ChartSeriesNames().Should().BeEmpty();

        cut.FindAll(".kpi-series-filter-action")[0].Click(); // All
        cut.Find(".kpi-series-filter-btn").TextContent.Should().Contain("All bars");
        ChartSeriesNames().Should().Equal("Cod", "Herring", "Seals");
    }

    [Fact]
    public void Filter_IsKeptOnTheWidgetInstance()
    {
        var category = CreateCategory("Cod", "Herring");
        var instance = new KpiWidgetInstance(category.CategoryKey);
        var cut = RenderWidget(category, instance);
        OpenFilter(cut);
        cut.FindAll(".kpi-series-filter-item input")[0].Change(false);

        // A new component for the same instance (e.g. after switching tabs) still has the filter applied.
        var again = RenderWidget(category, instance);

        LegendLabels(again).Should().Equal("Herring");
    }

    [Fact]
    public void SingleBarCategory_HasNeitherFilterNorLegend()
    {
        var cut = RenderWidget(CreateCategory("Bycatch"));

        cut.FindAll(".kpi-series-filter-btn").Should().BeEmpty();
        cut.FindAll(".kpi-widget-legend").Should().BeEmpty();
    }
}

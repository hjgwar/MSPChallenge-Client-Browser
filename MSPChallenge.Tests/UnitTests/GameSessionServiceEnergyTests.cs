using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Tests for the Energy/CEL KPI pipeline (GameSessionService.Energy.cs): parsing grids from a
/// plan's "energy" policy, merging them into the implemented-grid registry, and aggregating the
/// CEL simulation_updates stream into per-country "Energy Output" totals.
/// </summary>
public class GameSessionServiceEnergyTests
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

    private static GameSessionService CreateTestState()
    {
        var ws = new WebSocketService();
        var apiClient = new MspApiClient(
            new StubHttpClientFactory(),
            new ConfigurationManager(),
            new UserSessionService { GameServerAddress = "http://localhost" });
        return new GameSessionService(apiClient, ws, new GameUIStateService(null!, null!));
    }

    // One implemented plan declaring two grids: grid 1 (country 4 only) and grid 2 (shared by
    // countries 4 and 7, like a real North Sea interconnector grid).
    private const string PlanWithTwoGrids = """
    {
        "plan": [
            {
                "id": 1,
                "name": "NS_ENERGY_STARTING_PLAN_GREEN",
                "state": "IMPLEMENTED",
                "country": 4,
                "startdate": 0,
                "policies": [
                    {
                        "policy_type": "energy",
                        "grids": [
                            {
                                "id": 1, "persistent": 1, "name": "Grid A", "active": "1", "distribution_only": "0",
                                "energy": [ { "country_id": 4, "expected": 100 } ],
                                "sources": [ { "geometry_id": 101 } ],
                                "sockets": [ { "geometry_id": 102 } ]
                            },
                            {
                                "id": 2, "persistent": 2, "name": "Grid B", "active": "1", "distribution_only": "0",
                                "energy": [ { "country_id": 4, "expected": 50 }, { "country_id": 7, "expected": 20 } ],
                                "sources": [],
                                "sockets": [ { "geometry_id": 201 }, { "geometry_id": 202 } ]
                            }
                        ]
                    }
                ]
            }
        ]
    }
    """;

    [Fact]
    public void ApplyGameLatest_ParsesEnergyGridsOntoPlan()
    {
        var state = CreateTestState();

        state.ApplyGameLatest(JsonDocument.Parse(PlanWithTwoGrids).RootElement);

        var plan = state.Plans.Should().ContainSingle().Subject;
        plan.EnergyGrids.Should().HaveCount(2);

        var gridA = plan.EnergyGrids.Should().ContainSingle(g => g.PersistentId == 1).Subject;
        gridA.Name.Should().Be("Grid A");
        gridA.Countries.Should().ContainSingle(c => c.CountryId == 4 && c.Expected == 100);
        gridA.SourceGeometryIds.Should().Equal("101");
        gridA.SocketGeometryIds.Should().Equal("102");

        var gridB = plan.EnergyGrids.Should().ContainSingle(g => g.PersistentId == 2).Subject;
        gridB.Countries.Select(c => c.CountryId).Should().BeEquivalentTo([4, 7]);
    }

    [Fact]
    public void ApplyGameLatest_ImplementedPlanGrids_CreateEnergyOutputCategoryWithPerCountrySeries()
    {
        var state = CreateTestState();

        state.ApplyGameLatest(JsonDocument.Parse(PlanWithTwoGrids).RootElement);

        var category = state.EnergyKpiCategories.Should().ContainSingle().Subject;
        category.Definition.CategoryDisplayName.Should().Be("Energy Output");
        category.Values.Select(v => v.Definition.ValueName).Should().BeEquivalentTo(["4", "7"]);
    }

    [Fact]
    public void ApplyEnergySimulationUpdates_SumsMultipleGridsForSameCountryAndMonth()
    {
        // Country 4 owns both Grid A (100) and the shared Grid B (its 50 share) in month 0 —
        // the aggregate "Energy Output" value for country 4 must be their sum (150), not either
        // individual grid's value, since a country's total output should include every grid it owns.
        var state = CreateTestState();
        state.ApplyGameLatest(JsonDocument.Parse(PlanWithTwoGrids).RootElement);

        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "simulation_updates": [
                {
                    "simulation_type": "CEL",
                    "kpi": [
                        { "grid": "1", "month": "0", "country": "4", "actual": "100" },
                        { "grid": "2", "month": "0", "country": "4", "actual": "50" },
                        { "grid": "2", "month": "0", "country": "7", "actual": "20" }
                    ]
                }
            ]
        }
        """).RootElement);

        var category = state.EnergyKpiCategories.Should().ContainSingle().Subject;
        var country4 = category.FindValue("4");
        var country7 = category.FindValue("7");
        country4.Should().NotBeNull();
        country7.Should().NotBeNull();

        // KpiWidget resolves values via GetValue(month, ViewingCountryId); Energy Output stores its
        // aggregate under the global (-1) key, so any countryId argument must resolve the same total.
        country4!.GetValue(0, countryId: 4).Should().Be(150);
        country4.GetValue(0, countryId: 999).Should().Be(150);
        country7!.GetValue(0, countryId: 7).Should().Be(20);
    }

    [Fact]
    public void ApplyEnergySimulationUpdates_TracksLatestMonthAcrossSeveralUpdates()
    {
        var state = CreateTestState();
        state.ApplyGameLatest(JsonDocument.Parse(PlanWithTwoGrids).RootElement);

        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "simulation_updates": [
                { "simulation_type": "CEL", "kpi": [ { "grid": "1", "month": "0", "country": "4", "actual": "100" } ] }
            ]
        }
        """).RootElement);
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "simulation_updates": [
                { "simulation_type": "CEL", "kpi": [ { "grid": "1", "month": "1", "country": "4", "actual": "110" } ] }
            ]
        }
        """).RootElement);

        var category = state.EnergyKpiCategories.Should().ContainSingle().Subject;
        var country4 = category.FindValue("4")!;
        country4.MostRecentMonth.Should().Be(1);
        country4.GetValue(0, 4).Should().Be(100);
        country4.GetValue(1, 4).Should().Be(110);
    }

    [Fact]
    public void ApplyGameLatest_NonImplementedPlan_DoesNotRegisterGrids()
    {
        var state = CreateTestState();

        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "plan": [
                {
                    "id": 1, "name": "Draft plan", "state": "DESIGN", "country": 4, "startdate": 5,
                    "policies": [
                        { "policy_type": "energy", "grids": [
                            { "id": 1, "persistent": 1, "name": "Grid A", "active": "1",
                              "energy": [ { "country_id": 4, "expected": 100 } ], "sources": [], "sockets": [] }
                        ] }
                    ]
                }
            ]
        }
        """).RootElement);

        // The plan's own EnergyGrids are still parsed (for e.g. a future "my draft plans" view)...
        state.Plans.Single().EnergyGrids.Should().HaveCount(1);
        // ...but a DESIGN-state plan's grids must not count towards the live registry/KPI category,
        // consistent with the geometry-merge precedent (implemented plans only).
        state.EnergyKpiCategories.Should().BeEmpty();
    }
}

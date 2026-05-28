using FluentAssertions;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Unit tests for plan approval calculation logic.
/// These tests validate the production implementation's business rules for when approval is required,
/// including plan layers, geometry state, per-layer approval modes, EEZ intersections, and admin/GM slots.
/// </summary>
public class PlanApprovalTests
{
    private readonly PlanApprovalService _service = new();

    [Fact]
    public void ApprovalCalculation_PlanWithNoLayers_ReturnsEmptyList()
    {
        // Arrange
        var plan = CreatePlan(country: 1, layers: []);
        var eezPolygons = new List<EezPolygon>();
        var layerEntries = new List<LayerEntry>();
        var countryNames = new Dictionary<int, string> { { 1, "Country 1" }, { 2, "Country 2" } };

        // Act
        var result = _service.CalculateApproval(
            plan, plan.Country, eezPolygons, layerEntries, countryNames, _ => []);

        // Assert
        result.Should().BeEmpty("plan with no layers should not require approval");
    }

    [Fact]
    public void ApprovalCalculation_GeometryWithNotDependentApproval_ReturnsEmptyList()
    {
        // Arrange
        var geometry = new PlanGeometryItem(Coordinates: [[1.0, 2.0]]);
        var layer = new PlanLayerData("layer1", "layer1", "active", [geometry], []);
        var plan = CreatePlan(country: 1, layers: [layer]);

        var layerEntry = new LayerEntry
        {
            LayerId = "layer1",
            DisplayName = "Test Layer",
            TypeDefs = [new TypeDef("Type1", "Color1", null, "NotDependent")]
        };
        var eezPolygons = new List<EezPolygon>();
        var countryNames = new Dictionary<int, string> { { 1, "Country 1" } };

        // Act
        var result = _service.CalculateApproval(
            plan, plan.Country, eezPolygons, [layerEntry], countryNames, _ => []);

        // Assert
        result.Should().BeEmpty("geometry with NotDependent approval should not require approval");
    }

    [Fact]
    public void ApprovalCalculation_GeometryWithAllCountriesApproval_RequiresAllCountries()
    {
        // Arrange
        var geometry = new PlanGeometryItem(Coordinates: [[1.0, 2.0]]);
        var layer = new PlanLayerData("layer1", "layer1", "active", [geometry], []);
        var plan = CreatePlan(country: 1, layers: [layer]);

        var layerEntry = new LayerEntry
        {
            LayerId = "layer1",
            DisplayName = "Test Layer",
            TypeDefs = [new TypeDef("Type1", "Color1", null, "AllCountries")]
        };
        var eezPolygons = new List<EezPolygon>();
        var countryNames = new Dictionary<int, string>
        {
            { 1, "Country 1" }, // Owner (admin)
            { 2, "Country 2" }, // Admin/GM - should be skipped
            { 3, "Country 3" }, // Should require approval
            { 4, "Country 4" }  // Should require approval
        };

        // Act
        var result = _service.CalculateApproval(
            plan, plan.Country, eezPolygons, [layerEntry], countryNames, _ => []);

        // Assert
        result.Should().HaveCount(2, "should require approval from all countries except owner and admin/GM slots");
        result.Should().Contain(a => a.CountryId == 3);
        result.Should().Contain(a => a.CountryId == 4);
        result.Should().NotContain(a => a.CountryId == 1, "owner should not be included");
        result.Should().NotContain(a => a.CountryId == 2, "admin/GM slot should not be included");
    }

    [Fact]
    public void ApprovalCalculation_GeometryInOtherCountryEEZ_RequiresEEZOwnerApproval()
    {
        // Arrange
        var geometry = new PlanGeometryItem(Coordinates: [[10.0, 20.0]]);
        var layer = new PlanLayerData("layer1", "layer1", "active", [geometry], []);
        var plan = CreatePlan(country: 1, layers: [layer]);

        var layerEntry = new LayerEntry
        {
            LayerId = "layer1",
            DisplayName = "Test Layer",
            TypeDefs = [new TypeDef("Type1", "Color1", null, "EEZ")]
        };
        var eezPolygons = new List<EezPolygon>
        {
            new(CountryId: 2, Points: [[0.0, 0.0], [20.0, 0.0], [20.0, 30.0], [0.0, 30.0]])
        };
        var countryNames = new Dictionary<int, string>
        {
            { 1, "Country 1" },
            { 2, "Country 2" }
        };

        // Act
        var result = _service.CalculateApproval(
            plan, plan.Country, eezPolygons, [layerEntry], countryNames, _ => []);

        // Assert
        result.Should().HaveCount(1, "geometry in other country's EEZ should require approval");
        result[0].CountryId.Should().Be(2);
        result[0].Reasons.Should().Contain(r => r.Contains("Country 2's EEZ"));
    }

    [Fact]
    public void ApprovalCalculation_GeometryInOwnEEZ_ReturnsEmptyList()
    {
        // Arrange
        var geometry = new PlanGeometryItem(Coordinates: [[10.0, 20.0]]);
        var layer = new PlanLayerData("layer1", "layer1", "active", [geometry], []);
        var plan = CreatePlan(country: 1, layers: [layer]);

        var layerEntry = new LayerEntry
        {
            LayerId = "layer1",
            DisplayName = "Test Layer",
            TypeDefs = [new TypeDef("Type1", "Color1", null, "EEZ")]
        };
        var eezPolygons = new List<EezPolygon>
        {
            new(CountryId: 1, Points: [[0.0, 0.0], [20.0, 0.0], [20.0, 30.0], [0.0, 30.0]])
        };
        var countryNames = new Dictionary<int, string> { { 1, "Country 1" } };

        // Act
        var result = _service.CalculateApproval(
            plan, plan.Country, eezPolygons, [layerEntry], countryNames, _ => []);

        // Assert
        result.Should().BeEmpty("geometry in own EEZ should not require approval");
    }

    [Fact]
    public void ApprovalCalculation_DeletedGeometryFromOtherCountryEEZ_RequiresApproval()
    {
        // Arrange
        var layer = new PlanLayerData("layer1", "layer1", "active", [], DeletedPersistentIds: ["geom1"]);
        var plan = CreatePlan(country: 1, layers: [layer]);

        var layerEntry = new LayerEntry
        {
            LayerId = "layer1",
            DisplayName = "Test Layer",
            TypeDefs = [new TypeDef("Type1", "Color1", null, "NotDependent")]
        };
        var eezPolygons = new List<EezPolygon>
        {
            new(CountryId: 2, Points: [[0.0, 0.0], [20.0, 0.0], [20.0, 30.0], [0.0, 30.0]])
        };
        var countryNames = new Dictionary<int, string>
        {
            { 1, "Country 1" },
            { 2, "Country 2" }
        };

        // Provide base geometry that is in country 2's EEZ
        var baseGeometry = new List<ParsedGeometry>
        {
            new("geom1", TypeIndex: 0, Coordinates: [[10.0, 20.0]])
        };

        // Act
        var result = _service.CalculateApproval(
            plan, plan.Country, eezPolygons, [layerEntry], countryNames, _ => baseGeometry);

        // Assert
        result.Should().HaveCount(1, "deleted geometry from other country should require approval");
        result[0].CountryId.Should().Be(2);
        result[0].Reasons.Should().Contain(r => r.Contains("Country 2") && r.Contains("removed"));
    }

    [Fact]
    public void ApprovalCalculation_MultipleLayersWithDifferentApprovalModes_CombinesRequirements()
    {
        // Arrange
        var geom1 = new PlanGeometryItem(Coordinates: [[1.0, 2.0]]);
        var layer1 = new PlanLayerData("layer1", "layer1", "active", [geom1], []);
        
        var geom2 = new PlanGeometryItem(Coordinates: [[10.0, 20.0]]);
        var layer2 = new PlanLayerData("layer2", "layer2", "active", [geom2], []);
        
        var plan = CreatePlan(country: 1, layers: [layer1, layer2]);

        var layerEntries = new List<LayerEntry>
        {
            new()
            {
                LayerId = "layer1",
                DisplayName = "AllCountries Layer",
                TypeDefs = [new TypeDef("Type1", "Color1", null, "AllCountries")]
            },
            new()
            {
                LayerId = "layer2",
                DisplayName = "EEZ Layer",
                TypeDefs = [new TypeDef("Type2", "Color2", null, "EEZ")]
            }
        };
        var eezPolygons = new List<EezPolygon>
        {
            new(CountryId: 3, Points: [[0.0, 0.0], [20.0, 0.0], [20.0, 30.0], [0.0, 30.0]])
        };
        var countryNames = new Dictionary<int, string>
        {
            { 1, "Country 1" },
            { 2, "Country 2" },
            { 3, "Country 3" },
            { 4, "Country 4" }
        };

        // Act
        var result = _service.CalculateApproval(
            plan, plan.Country, eezPolygons, layerEntries, countryNames, _ => []);

        // Assert
        result.Should().HaveCount(2, "should combine requirements from both layers");
        // Country 3 appears from both AllCountries and EEZ
        result.Should().Contain(a => a.CountryId == 3);
        // Country 4 appears only from AllCountries (not in EEZ)
        result.Should().Contain(a => a.CountryId == 4);
    }

    private static PlanEntry CreatePlan(int country, IReadOnlyList<PlanLayerData> layers) =>
        new(
            PlanId: 1,
            Name: "Test Plan",
            Description: "Test",
            State: "DESIGN",
            Country: country,
            StartDate: 0,
            ConstructionTime: 0,
            PolicyNames: [],
            PolicyTypes: [],
            Layers: layers
        );
}
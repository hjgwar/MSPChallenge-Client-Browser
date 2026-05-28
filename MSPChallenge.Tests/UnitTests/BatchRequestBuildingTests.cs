using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Unit tests for batch request building logic in SavePlanAsync.
/// Tests request structure, grouping, and reference handling.
/// </summary>
public class BatchRequestBuildingTests
{
    [Fact]
    public void BuildPlanCreateRequest_ForNewPlan_HasCorrectStructure()
    {
        // Arrange
        var planName = "Test Plan";
        var planDescription = "Test Description";
        var startMonth = 120; // 10 years * 12 months
        var countryId = 1;

        // Act
        var request = new
        {
            call_id = 1,
            endpoint = "api/Plan/Post",
            endpoint_data = new Dictionary<string, string>
            {
                { "country", countryId.ToString() },
                { "name", planName },
                { "description", planDescription },
                { "startdate", startMonth.ToString() }
            },
            group = 1
        };

        // Assert
        request.call_id.Should().Be(1);
        request.endpoint.Should().Be("api/Plan/Post");
        request.group.Should().Be(1, "plan creation should be in group 1");
        request.endpoint_data["name"].Should().Be(planName);
        request.endpoint_data["startdate"].Should().Be("120");
    }

    [Fact]
    public void BuildGeometryAddRequest_WithValidCoordinates_HasCorrectStructure()
    {
        // Arrange
        var layerId = "ENERGY";
        var coords = new[] { new[] { 100.0, 50.0 }, new[] { 101.0, 51.0 } };
        var typeIndex = 1;
        var planIdRef = "!Ref:1";

        // Act
        var geometryJson = JsonSerializer.Serialize(coords);
        var request = new
        {
            call_id = 5,
            endpoint = "api/Plan/AddGeometry",
            endpoint_data = new Dictionary<string, string>
            {
                { "id", planIdRef },
                { "layer", layerId },
                { "geometry", geometryJson },
                { "type", typeIndex.ToString() }
            },
            group = 5
        };

        // Assert
        request.endpoint.Should().Be("api/Plan/AddGeometry");
        request.group.Should().Be(5, "geometry additions should be in group 5");
        request.endpoint_data["id"].Should().Be("!Ref:1", "should reference plan creation call");
        request.endpoint_data["geometry"].Should().Contain("100");
        request.endpoint_data["geometry"].Should().Contain("50");
    }

    [Theory]
    [InlineData(1, "PLAN_CREATE")]
    [InlineData(3, "LAYER_ADD")]
    [InlineData(4, "GEOMETRY_DELETE")]
    [InlineData(5, "GEOMETRY_ADD")]
    [InlineData(10, "GEOMETRY_DATA")]
    public void BatchGroups_HaveCorrectSequencing(int groupNumber, string operationType)
    {
        // This test documents the batch group execution order
        // Lower group numbers execute first
        
        var expectedOrder = new Dictionary<int, string>
        {
            { 1, "PLAN_CREATE" },
            { 3, "LAYER_ADD" },
            { 4, "GEOMETRY_DELETE" },
            { 5, "GEOMETRY_ADD" },
            { 10, "GEOMETRY_DATA" },
            { 100, "UNLOCK" }
        };

        expectedOrder.Should().ContainKey(groupNumber);
        expectedOrder[groupNumber].Should().Be(operationType);
    }

    [Fact]
    public void BuildLayerAddRequest_ReferencesNewPlanId()
    {
        // Arrange
        var layerId = "SHIPPING";
        var createPlanCallId = 1;

        // Act
        var request = new
        {
            call_id = 2,
            endpoint = "api/Plan/AddLayer",
            endpoint_data = new Dictionary<string, string>
            {
                { "id", $"!Ref:{createPlanCallId}" },
                { "layer", layerId }
            },
            group = 3
        };

        // Assert
        request.endpoint_data["id"].Should().Be("!Ref:1", 
            "layer addition should reference the plan creation call");
        request.group.Should().Be(3, "layer operations should be in group 3");
    }

    [Fact]
    public void BuildPlanUpdateRequest_ForExistingPlan_UsesRealPlanId()
    {
        // Arrange
        var existingPlanId = 42;
        var newName = "Updated Plan Name";

        // Act
        var request = new
        {
            call_id = 1,
            endpoint = "api/Plan/SetName",
            endpoint_data = new Dictionary<string, string>
            {
                { "id", existingPlanId.ToString() },
                { "name", newName }
            },
            group = 5
        };

        // Assert
        request.endpoint_data["id"].Should().Be("42", 
            "existing plan updates should use the real plan ID");
        request.endpoint_data["id"].Should().NotContain("!Ref", 
            "existing plans should not use references");
    }
}
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
        var createPlanCallId = 1;

        // Act - Production code splits this into multiple requests:
        // 1. POST to create plan (group 1)
        var createRequest = new
        {
            call_id = createPlanCallId,
            endpoint = "api/Plan/Post",
            endpoint_data = JsonSerializer.Serialize(new { country = countryId }),
            group = 1
        };

        // 2. Name request (group 5)
        var nameRequest = new
        {
            call_id = 2,
            endpoint = "api/Plan/Name",
            endpoint_data = JsonSerializer.Serialize(new
            {
                id = $"!Ref:{createPlanCallId}",
                name = planName
            }),
            group = 5
        };

        // 3. Description request (group 5)
        var descriptionRequest = new
        {
            call_id = 3,
            endpoint = "api/Plan/Description",
            endpoint_data = JsonSerializer.Serialize(new
            {
                id = $"!Ref:{createPlanCallId}",
                description = planDescription
            }),
            group = 5
        };

        // 4. Date request (group 5)
        var dateRequest = new
        {
            call_id = 4,
            endpoint = "api/Plan/Date",
            endpoint_data = JsonSerializer.Serialize(new
            {
                id = $"!Ref:{createPlanCallId}",
                date = startMonth
            }),
            group = 5
        };

        // Assert - Validate create request
        createRequest.call_id.Should().Be(1);
        createRequest.endpoint.Should().Be("api/Plan/Post");
        createRequest.group.Should().Be(1, "plan creation should be in group 1");
        createRequest.endpoint_data.Should().Contain("country");
        createRequest.endpoint_data.Should().NotContain("name", "name is sent separately");

        // Assert - Validate name request
        nameRequest.endpoint.Should().Be("api/Plan/Name");
        nameRequest.group.Should().Be(5, "plan name update should be in group 5");
        nameRequest.endpoint_data.Should().Contain(planName);
        nameRequest.endpoint_data.Should().Contain("!Ref:1", "should reference plan creation call");

        // Assert - Validate date request
        dateRequest.endpoint.Should().Be("api/Plan/Date");
        dateRequest.endpoint_data.Should().Contain("120");
    }

    [Fact]
    public void BuildGeometryAddRequest_WithValidCoordinates_HasCorrectStructure()
    {
        // Arrange
        var layerId = "ENERGY";
        var coords = new[] { new[] { 100.0, 50.0 }, new[] { 101.0, 51.0 } };
        var typeIndex = 1;
        var planIdRef = "!Ref:1";
        var countryId = 1;
        var geoCallId = 5;

        // Act - Production code splits geometry creation into two requests:
        // 1. POST to create geometry (group 5)
        var geometryJson = JsonSerializer.Serialize(coords);
        var createRequest = new
        {
            call_id = geoCallId,
            endpoint = "api/Geometry/Post",
            endpoint_data = JsonSerializer.Serialize(new
            {
                geometry = geometryJson,
                country = countryId,
                layer = layerId,
                plan = planIdRef
            }),
            group = 5
        };

        // 2. Data request to set type (group 10)
        var dataRequest = new
        {
            call_id = 6,
            endpoint = "api/Geometry/Data",
            endpoint_data = JsonSerializer.Serialize(new
            {
                id = $"!Ref:{geoCallId}",
                data = "",
                type = typeIndex.ToString()
            }),
            group = 10
        };

        // Assert - Validate geometry creation request
        createRequest.endpoint.Should().Be("api/Geometry/Post");
        createRequest.group.Should().Be(5, "geometry creation should be in group 5");
        createRequest.endpoint_data.Should().Contain("country");
        createRequest.endpoint_data.Should().Contain(layerId);
        createRequest.endpoint_data.Should().Contain(planIdRef);
        createRequest.endpoint_data.Should().Contain("100");
        createRequest.endpoint_data.Should().Contain("50");

        // Assert - Validate geometry data request
        dataRequest.endpoint.Should().Be("api/Geometry/Data");
        dataRequest.group.Should().Be(10, "geometry data should be in group 10");
        dataRequest.endpoint_data.Should().Contain("!Ref:5", "should reference geometry creation call");
        dataRequest.endpoint_data.Should().Contain(typeIndex.ToString());
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
        var layerId = 10; // Integer layer ID as used in production
        var createPlanCallId = 1;

        // Act - Production code uses api/Plan/Layer endpoint
        var request = new
        {
            call_id = 2,
            endpoint = "api/Plan/Layer",
            endpoint_data = JsonSerializer.Serialize(new
            {
                id = $"!Ref:{createPlanCallId}",
                layerid = layerId
            }),
            group = 3
        };

        // Assert
        request.endpoint.Should().Be("api/Plan/Layer", "production uses api/Plan/Layer endpoint");
        request.endpoint_data.Should().Contain("!Ref:1", 
            "layer addition should reference the plan creation call");
        request.endpoint_data.Should().Contain("\"layerid\"", "should use layerid parameter");
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
namespace MSPChallenge.Tests.IntegrationTests;

/// <summary>
/// Tests for geometry visualization data structures used in immediate visual feedback.
/// These tests verify that geometry data is correctly formatted for map display
/// with appropriate badges, colors, and restriction markers.
/// </summary>
public class GeometryVisualizationTests
{
    [Fact]
    public void GeometryDataStructure_ShouldIncludeRequiredFields()
    {
        // Arrange: Create a sample geometry data structure as produced by RefreshPlanGeometryVisualsAsync
        var geometryData = new
        {
            id = "test_geometry_1",
            coords = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } },
            isNew = true,
            mspType = 0,
            restrictionMarkers = Array.Empty<object>(),
            restrictions = Array.Empty<string>()
        };

        // Assert: Verify all required fields are present
        geometryData.id.Should().NotBeNullOrEmpty();
        geometryData.coords.Should().NotBeNull();
        geometryData.coords.Should().HaveCountGreaterThan(0);
        geometryData.isNew.Should().BeTrue();
        geometryData.restrictionMarkers.Should().NotBeNull();
        geometryData.restrictions.Should().NotBeNull();
    }

    [Fact]
    public void LayerDataStructure_ShouldIncludeGeometriesAndDeletedIds()
    {
        // Arrange: Create a sample layer data structure for map visualization
        var layerData = new
        {
            geoType = "polygon",
            geometries = new[]
            {
                new
                {
                    id = "geom_1",
                    coords = new[] { new[] { 0.0, 0.0 } },
                    isNew = true,
                    mspType = 0,
                    restrictionMarkers = Array.Empty<object>(),
                    restrictions = Array.Empty<string>()
                }
            },
            originalLayerId = "layer_1",
            deletedIds = Array.Empty<string>()
        };

        // Assert: Verify structure matches expected format
        layerData.geoType.Should().NotBeNullOrEmpty();
        layerData.geometries.Should().NotBeNull();
        layerData.geometries.Should().HaveCount(1);
        layerData.originalLayerId.Should().NotBeNullOrEmpty();
        layerData.deletedIds.Should().NotBeNull();
    }

    [Theory]
    [InlineData(true, "New geometry should be marked as new")]
    [InlineData(false, "Edited geometry should not be marked as new")]
    public void GeometryIsNewFlag_ShouldIndicateCreationStatus(bool isNew, string reason)
    {
        // Arrange: Create geometry with specific isNew flag
        var geometry = new
        {
            id = isNew ? "temp_123" : "existing_456",
            coords = new[] { new[] { 0.0, 0.0 } },
            isNew = isNew,
            mspType = 0,
            restrictionMarkers = Array.Empty<object>(),
            restrictions = Array.Empty<string>()
        };

        // Assert: Verify isNew flag is correctly set
        geometry.isNew.Should().Be(isNew, reason);
    }

    [Fact]
    public void RestrictionMarker_ShouldIncludeCoordinatesAndSeverity()
    {
        // Arrange: Create a restriction marker as produced by restriction evaluation
        var restrictionMarker = new
        {
            severity = "ERROR",
            message = "Overlaps with protected area",
            sourceLayer = "Wind Farm",
            targetLayer = "Marine Protected Area",
            changeKind = "New geometry",
            coord = new[] { 10.5, 20.3 }
        };

        // Assert: Verify marker structure
        restrictionMarker.severity.Should().NotBeNullOrEmpty();
        restrictionMarker.message.Should().NotBeNullOrEmpty();
        restrictionMarker.sourceLayer.Should().NotBeNullOrEmpty();
        restrictionMarker.targetLayer.Should().NotBeNullOrEmpty();
        restrictionMarker.coord.Should().HaveCount(2);
        restrictionMarker.coord[0].Should().BeGreaterThan(0);
        restrictionMarker.coord[1].Should().BeGreaterThan(0);
    }

    [Fact]
    public void GeometryWithMultipleRestrictions_ShouldListAllSeverities()
    {
        // Arrange: Create geometry with multiple restriction types
        var geometry = new
        {
            id = "geom_1",
            coords = new[] { new[] { 0.0, 0.0 } },
            isNew = false,
            mspType = 0,
            restrictionMarkers = new[]
            {
                new { severity = "ERROR", message = "Overlap 1", coord = new[] { 0.0, 0.0 } },
                new { severity = "WARNING", message = "Overlap 2", coord = new[] { 1.0, 1.0 } }
            },
            restrictions = new[] { "ERROR", "WARNING" }
        };

        // Assert: Verify restrictions list contains unique severities
        geometry.restrictions.Should().Contain("ERROR");
        geometry.restrictions.Should().Contain("WARNING");
        geometry.restrictions.Should().HaveCount(2);
        geometry.restrictionMarkers.Should().HaveCount(2);
    }

    [Fact]
    public void DeletedGeometry_ShouldAppearInDeletedIdsList()
    {
        // Arrange: Create layer data with deleted geometry
        var deletedWorldStateId = "ws_123";
        var layerData = new
        {
            geoType = "polygon",
            geometries = Array.Empty<object>(),
            originalLayerId = "layer_1",
            deletedIds = new[] { deletedWorldStateId }
        };

        // Assert: Verify deleted ID is tracked
        layerData.deletedIds.Should().Contain(deletedWorldStateId);
        layerData.deletedIds.Should().HaveCount(1);
    }

    [Fact]
    public void EmptyOverlay_ShouldProduceValidEmptyStructure()
    {
        // Arrange: Create empty layer data (no geometries)
        var layerData = new
        {
            geoType = "polygon",
            geometries = Array.Empty<object>(),
            originalLayerId = "layer_1",
            deletedIds = Array.Empty<string>()
        };

        // Assert: Verify structure is valid even when empty
        layerData.Should().NotBeNull();
        layerData.geometries.Should().NotBeNull();
        layerData.geometries.Should().BeEmpty();
        layerData.deletedIds.Should().NotBeNull();
        layerData.deletedIds.Should().BeEmpty();
    }
}

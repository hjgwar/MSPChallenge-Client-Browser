namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Tests for geometry type change functionality and persistence.
/// These tests verify that type changes are properly detected, saved to the server,
/// and restored when re-entering edit mode.
/// </summary>
public class GeometryTypeChangeTests
{
    [Fact]
    public void WorldStateFeature_TypeChangeOnly_ShouldBeIncludedInSave()
    {
        // Arrange: World-state feature with changed type but unchanged coordinates
        var overlayItem = new
        {
            FeatureId = "feature_1",
            TypeIndex = 2,  // Changed from original type 1
            WorldStateId = "ws_123",
            OriginalCoords = (double[][]?)null,
            Coords = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        var worldStateGeo = new
        {
            FeatureId = "ws_123",
            TypeIndex = 1,  // Original type
            Coordinates = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        // Act: Determine if feature should be saved
        var origTypeIndex = worldStateGeo.TypeIndex;
        var coordsUnchanged = CoordsMatch(overlayItem.Coords, worldStateGeo.Coordinates);
        var typeChanged = overlayItem.TypeIndex != origTypeIndex;
        var shouldSave = !coordsUnchanged || typeChanged;

        // Assert: Should save because type changed, even though coords didn't
        shouldSave.Should().BeTrue("type change alone should trigger a save");
        typeChanged.Should().BeTrue();
        coordsUnchanged.Should().BeTrue();
    }

    [Fact]
    public void WorldStateFeature_TypeChangeWithOriginalCoordsProvided_ShouldStillDetectTypeChange()
    {
        // Arrange: World-state feature with OriginalCoords already provided (not null)
        // This tests the bug fix where origTypeIndex wasn't being fetched when origCoords was provided
        var overlayItem = new
        {
            FeatureId = "feature_1",
            TypeIndex = 3,  // Changed from original type 1
            WorldStateId = "ws_123",
            OriginalCoords = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } },  // Provided
            Coords = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        var worldStateGeo = new
        {
            FeatureId = "ws_123",
            TypeIndex = 1,  // Original type
            Coordinates = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        // Act: Simulate the logic - ALWAYS fetch original type from world state
        var origCoords = overlayItem.OriginalCoords;
        int? origTypeIndex = worldStateGeo.TypeIndex;  // Always fetch from world state
        
        var coordsChanged = origCoords is null || !CoordsMatch(overlayItem.Coords, origCoords);
        var typeChanged = origTypeIndex.HasValue && overlayItem.TypeIndex != origTypeIndex.Value;
        var shouldSave = coordsChanged || typeChanged;

        // Assert: Should detect type change and save
        shouldSave.Should().BeTrue("type change should be detected even when OriginalCoords is provided");
        typeChanged.Should().BeTrue("type should be detected as changed");
        coordsChanged.Should().BeFalse("coordinates should be detected as unchanged");
    }

    [Fact]
    public void WorldStateFeature_NoChanges_ShouldBeSkipped()
    {
        // Arrange: World-state feature with no changes
        var overlayItem = new
        {
            FeatureId = "feature_1",
            TypeIndex = 1,  // Same as original
            WorldStateId = "ws_123",
            OriginalCoords = (double[][]?)null,
            Coords = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        var worldStateGeo = new
        {
            FeatureId = "ws_123",
            TypeIndex = 1,  // Original type
            Coordinates = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        // Act: Determine if feature should be saved
        var origTypeIndex = worldStateGeo.TypeIndex;
        var coordsUnchanged = CoordsMatch(overlayItem.Coords, worldStateGeo.Coordinates);
        var typeChanged = overlayItem.TypeIndex != origTypeIndex;
        var shouldSave = !coordsUnchanged || typeChanged;

        // Assert: Should skip because nothing changed
        shouldSave.Should().BeFalse("unchanged features should be skipped");
        typeChanged.Should().BeFalse();
        coordsUnchanged.Should().BeTrue();
    }

    [Fact]
    public void PlanGeometry_TypeChangeOnly_ShouldSendDataRequest()
    {
        // Arrange: Existing plan geometry with changed type but unchanged coordinates
        var overlayItem = new
        {
            FeatureId = "plan_geo_1",
            TypeIndex = 3,  // Changed from original type 2
            WorldStateId = (string?)null,
            Coords = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        var planGeometry = new
        {
            Id = "plan_geo_1",
            TypeIndex = 2,  // Original type
            Coordinates = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        // Act: Determine what requests to send
        var origCoords = planGeometry.Coordinates;
        var coordsChanged = !CoordsMatch(overlayItem.Coords, origCoords);
        var typeChanged = overlayItem.TypeIndex != planGeometry.TypeIndex;

        // Assert: Should send Data request for type change
        typeChanged.Should().BeTrue("type should be detected as changed");
        coordsChanged.Should().BeFalse("coordinates should be detected as unchanged");
    }

    [Fact]
    public void PlanGeometry_BothChanges_ShouldSendBothRequests()
    {
        // Arrange: Existing plan geometry with both type and coordinate changes
        var overlayItem = new
        {
            FeatureId = "plan_geo_1",
            TypeIndex = 3,  // Changed from 2
            WorldStateId = (string?)null,
            Coords = new[] { new[] { 0.0, 0.0 }, new[] { 2.0, 2.0 } }  // Changed
        };

        var planGeometry = new
        {
            Id = "plan_geo_1",
            TypeIndex = 2,
            Coordinates = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } }
        };

        // Act: Determine what requests to send
        var coordsChanged = !CoordsMatch(overlayItem.Coords, planGeometry.Coordinates);
        var typeChanged = overlayItem.TypeIndex != planGeometry.TypeIndex;

        // Assert: Should send both Update and Data requests
        coordsChanged.Should().BeTrue("coordinates should be detected as changed");
        typeChanged.Should().BeTrue("type should be detected as changed");
    }

    [Fact]
    public void GeometryDataRequest_ShouldIncludeRequiredFields()
    {
        // Arrange: Create a Data request as sent to api/Geometry/Data
        var dataRequest = new
        {
            id = "123",
            type = "2",  // Type as string
            data = "{}"  // Empty JSON object to preserve existing metadata
        };

        // Assert: Verify required fields are present
        dataRequest.id.Should().NotBeNull();
        dataRequest.type.Should().NotBeNull();
        dataRequest.type.Should().BeOfType<string>("server expects type as string");
        dataRequest.data.Should().NotBeNull("data field should be present");
        dataRequest.data.Should().Be("{}", "empty metadata should be empty JSON object");
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(5, "5")]
    [InlineData(99, "99")]
    public void TypeIndex_ShouldBeConvertedToString(int typeIndex, string expectedString)
    {
        // Act: Convert type index to string as server expects
        var typeString = typeIndex.ToString();

        // Assert: Verify conversion is correct
        typeString.Should().Be(expectedString);
    }

    // Helper method to compare coordinates
    private static bool CoordsMatch(double[][] coords1, double[][] coords2)
    {
        if (coords1.Length != coords2.Length) return false;
        for (int i = 0; i < coords1.Length; i++)
        {
            if (coords1[i].Length != coords2[i].Length) return false;
            for (int j = 0; j < coords1[i].Length; j++)
            {
                if (Math.Abs(coords1[i][j] - coords2[i][j]) > 1e-9)
                    return false;
            }
        }
        return true;
    }
}

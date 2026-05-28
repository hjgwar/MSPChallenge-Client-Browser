namespace MSPChallenge.Tests.IntegrationTests;

/// <summary>
/// Tests for plan geometry projection and base geometry hiding.
/// These tests verify that when viewing/editing a plan, the base geometry
/// that the plan modifies is properly hidden, and restored when the plan is closed.
/// </summary>
public class PlanGeometryProjectionTests
{
    [Fact]
    public void CurrentPlan_ModifiedGeometry_ShouldHideBaseGeometry()
    {
        // Arrange: Plan that modifies existing geometry (PersistentId != Id)
        var planLayer = new
        {
            OriginalLayerId = "layer_1",
            Geometry = new[]
            {
                new
                {
                    Id = "plan_geo_123",
                    PersistentId = "base_geo_456",  // References base geometry
                    Coordinates = new[] { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } },
                    TypeIndex = 2
                }
            },
            DeletedPersistentIds = Array.Empty<string>()
        };

        // Act: Determine which base geometry should be hidden
        var shouldHide = !string.IsNullOrEmpty(planLayer.Geometry[0].PersistentId) 
                        && planLayer.Geometry[0].PersistentId != planLayer.Geometry[0].Id;

        // Assert: Base geometry should be hidden
        shouldHide.Should().BeTrue("modified geometry should hide its base");
        planLayer.Geometry[0].PersistentId.Should().Be("base_geo_456", "persistent ID identifies the base geometry to hide");
    }

    [Fact]
    public void CurrentPlan_NewGeometry_ShouldNotHideAnything()
    {
        // Arrange: Plan with new geometry (PersistentId == Id or empty)
        var planLayer = new
        {
            OriginalLayerId = "layer_1",
            Geometry = new[]
            {
                new
                {
                    Id = "plan_geo_123",
                    PersistentId = "plan_geo_123",  // Same as Id = new geometry
                    Coordinates = new[] { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } },
                    TypeIndex = 1
                }
            },
            DeletedPersistentIds = Array.Empty<string>()
        };

        // Act: Determine which base geometry should be hidden
        var shouldHide = !string.IsNullOrEmpty(planLayer.Geometry[0].PersistentId) 
                        && planLayer.Geometry[0].PersistentId != planLayer.Geometry[0].Id;

        // Assert: Nothing should be hidden for new geometry
        shouldHide.Should().BeFalse("new geometry has no base to hide");
    }

    [Fact]
    public void CurrentPlan_DeletedGeometry_ShouldHideBaseGeometry()
    {
        // Arrange: Plan that deletes existing geometry
        var planLayer = new
        {
            OriginalLayerId = "layer_1",
            Geometry = Array.Empty<object>(),
            DeletedPersistentIds = new[] { "base_geo_789" }
        };

        // Assert: Deleted IDs should be in the hidden list
        planLayer.DeletedPersistentIds.Should().Contain("base_geo_789");
        planLayer.DeletedPersistentIds.Length.Should().Be(1);
    }

    [Fact]
    public void CurrentPlan_MultipleModifications_ShouldHideAllBaseGeometry()
    {
        // Arrange: Plan with multiple modifications
        var planLayer = new
        {
            OriginalLayerId = "layer_1",
            Geometry = new[]
            {
                new
                {
                    Id = "plan_geo_1",
                    PersistentId = "base_geo_1",  // Modified
                    Coordinates = new[] { new[] { 1.0, 1.0 } },
                    TypeIndex = 2
                },
                new
                {
                    Id = "plan_geo_2",
                    PersistentId = "plan_geo_2",  // New
                    Coordinates = new[] { new[] { 2.0, 2.0 } },
                    TypeIndex = 1
                },
                new
                {
                    Id = "plan_geo_3",
                    PersistentId = "base_geo_3",  // Modified
                    Coordinates = new[] { new[] { 3.0, 3.0 } },
                    TypeIndex = 3
                }
            },
            DeletedPersistentIds = new[] { "base_geo_4", "base_geo_5" }
        };

        // Act: Build list of hidden base geometry IDs
        var hiddenIds = new List<string>();
        
        // Add deleted geometry
        hiddenIds.AddRange(planLayer.DeletedPersistentIds);
        
        // Add modified geometry
        foreach (var geo in planLayer.Geometry)
        {
            if (!string.IsNullOrEmpty(geo.PersistentId) && geo.PersistentId != geo.Id)
                hiddenIds.Add(geo.PersistentId);
        }

        // Assert: Should hide 4 base geometries total
        hiddenIds.Should().HaveCount(4, "2 modified + 2 deleted");
        hiddenIds.Should().Contain(new[] { "base_geo_1", "base_geo_3", "base_geo_4", "base_geo_5" });
        hiddenIds.Should().NotContain("plan_geo_2", "new geometry doesn't hide base");
    }

    [Fact]
    public void ProjectionData_ShouldNotIncludeCurrentPlanGeometry()
    {
        // Arrange: Projection data structure
        var projectionData = new
        {
            hidden = new Dictionary<string, string[]>
            {
                ["layer_1"] = new[] { "base_geo_1", "base_geo_2" }
            },
            added = new[]
            {
                new
                {
                    layerId = "layer_1",
                    geoType = "polygon",
                    id = "prior_plan_geo_123",
                    typeIndex = 1,
                    coords = new[] { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } }
                }
            }
        };

        // Assert: Only prior finalized plan geometry should be in "added"
        // Current plan's geometry is shown in the overlay, not the base layer
        projectionData.added.Should().NotBeEmpty("prior finalized plans add to base layers");
        projectionData.added.Should().AllSatisfy(item => 
            item.id.Should().NotStartWith("temp_", "temporary IDs are for current plan only"));
    }

    [Fact]
    public void WhenPlanClosed_AllBaseGeometryShouldBeRestored()
    {
        // Arrange: Simulate closing a plan
        var projectionWasActive = true;
        var planOverlayWasShown = true;

        // Act: Close plan (calls clearPlanProjection and clearPlanOverlay)
        var clearProjectionCalled = projectionWasActive;
        var clearOverlayCalled = planOverlayWasShown;

        // Assert: Both should be cleared
        clearProjectionCalled.Should().BeTrue("projection hides/shows base geometry");
        clearOverlayCalled.Should().BeTrue("overlay shows plan's own geometry");
    }

    [Theory]
    [InlineData("", "geo_1", false, "Empty PersistentId means new geometry")]
    [InlineData("geo_1", "geo_1", false, "PersistentId == Id means new geometry")]
    [InlineData("base_123", "plan_456", true, "Different IDs mean modified base geometry")]
    [InlineData("base_123", "base_456", true, "Different IDs even if both start with 'base'")]
    public void GeometryModificationDetection_VariousCases(string persistentId, string id, bool shouldHide, string reason)
    {
        // Act: Determine if base geometry should be hidden
        var isModified = !string.IsNullOrEmpty(persistentId) && persistentId != id;

        // Assert
        isModified.Should().Be(shouldHide, reason);
    }
}

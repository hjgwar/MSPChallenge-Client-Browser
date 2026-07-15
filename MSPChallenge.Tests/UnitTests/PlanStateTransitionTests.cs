using FluentAssertions;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Unit tests for plan state transitions and validation.
/// NOTE: PlanStateTransitions service not yet extracted - logic is currently in UI components.
/// These tests are currently skipped until the service is created.
/// </summary>
public class PlanStateTransitionTests
{
    [Theory(Skip = "PlanStateTransitions service not yet extracted from UI components")]
    [InlineData("DESIGN", false, false, new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "ARCHIVED" })]
    [InlineData("CONSULTATION", false, false, new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "ARCHIVED" })]
    [InlineData("APPROVAL", false, false, new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "ARCHIVED" })]
    [InlineData("APPROVED", false, false, new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "ARCHIVED" })]
    [InlineData("IMPLEMENTED", false, false, new string[] { })]
    public void GetAvailablePlanStates_ForGivenState_ReturnsValidTransitions(
        string currentState, bool requiresApproval, bool hasErrors, string[] expectedStates)
    {
        // TODO: Extract state transition logic to a testable service
        // Act
        // var result = PlanStateTransitions.GetAvailablePlanStates(currentState, requiresApproval, hasErrors);

        // Assert
        // result.Should().BeEquivalentTo(expectedStates,
        //     $"state '{currentState}' should allow transitions to: {string.Join(", ", expectedStates)}");
    }


    [Theory(Skip = "PlanStateTransitions service not yet extracted from UI components")]
    [InlineData(true, new[] { "DESIGN", "CONSULTATION", "APPROVAL", "ARCHIVED" })]
    [InlineData(false, new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED", "ARCHIVED" })]
    public void GetAvailablePlanStates_RespectsRequiresApproval(
        bool requiresApproval, string[] expectedStates)
    {
        // Act
        // var result = PlanStateTransitions.GetAvailablePlanStates("DESIGN", requiresApproval, false);

        // Assert
        // result.Should().BeEquivalentTo(expectedStates);
    }

    [Fact(Skip = "PlanStateTransitions service not yet extracted from UI components")]
    public void GetAvailablePlanStates_WhenPlanHasErrors_ReturnsDesignAndArchivedOnly()
    {
        // Act
        // var result = PlanStateTransitions.GetAvailablePlanStates("CONSULTATION", false, true);

        // Assert
        // result.Should().BeEquivalentTo(new[] { "DESIGN", "ARCHIVED" });
    }

    [Fact(Skip = "PlanStateTransitions service not yet extracted from UI components")]
    public void GetAvailablePlanStates_ForArchivedState_ReturnsOnlyDesign()
    {
        // Act
        // var result = PlanStateTransitions.GetAvailablePlanStates("ARCHIVED", false, false);

        // Assert
        // result.Should().ContainSingle().Which.Should().Be("DESIGN");
    }
}
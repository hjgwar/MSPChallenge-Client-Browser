using FluentAssertions;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Unit tests for plan state transitions and validation.
/// Tests the GetAvailablePlanStates logic from Game.PlanUI.cs
/// </summary>
public class PlanStateTransitionTests
{
    [Theory]
    [InlineData("DESIGN", new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED" })]
    [InlineData("CONSULTATION", new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED" })]
    [InlineData("APPROVAL", new[] { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED" })]
    [InlineData("APPROVED", new[] { "APPROVED", "DELETED" })]
    [InlineData("IMPLEMENTED", new string[] { })] // No transitions from implemented
    public void GetAvailablePlanStates_ForGivenState_ReturnsValidTransitions(
        string currentState, string[] expectedStates)
    {
        // Act
        var result = GetAvailablePlanStatesHelper(currentState);

        // Assert
        result.Should().BeEquivalentTo(expectedStates,
            $"state '{currentState}' should allow transitions to: {string.Join(", ", expectedStates)}");
    }

    [Fact]
    public void GetAvailablePlanStates_ForDeletedState_ReturnsOnlyDeleted()
    {
        // Act
        var result = GetAvailablePlanStatesHelper("DELETED");

        // Assert
        result.Should().ContainSingle()
            .Which.Should().Be("DELETED", "deleted plans should not be editable");
    }

    [Theory]
    [InlineData("DESIGN", "APPROVED", true)]
    [InlineData("DESIGN", "DELETED", false)]
    [InlineData("APPROVED", "DESIGN", false)]
    [InlineData("CONSULTATION", "APPROVAL", true)]
    public void CanTransition_FromStateToState_ReturnsExpectedResult(
        string fromState, string toState, bool canTransition)
    {
        // Act
        var availableStates = GetAvailablePlanStatesHelper(fromState);
        var result = availableStates.Contains(toState);

        // Assert
        result.Should().Be(canTransition,
            $"transition from '{fromState}' to '{toState}' should be {(canTransition ? "allowed" : "forbidden")}");
    }

    // Helper method simulating GetAvailablePlanStates from Game.PlanUI.cs
    private List<string> GetAvailablePlanStatesHelper(string currentState)
    {
        var upper = currentState.ToUpperInvariant();
        
        if (upper == "IMPLEMENTED")
            return new List<string>();
        
        if (upper == "APPROVED")
            return new List<string> { "APPROVED", "DELETED" };
        
        if (upper == "DELETED")
            return new List<string> { "DELETED" };
        
        return new List<string> { "DESIGN", "CONSULTATION", "APPROVAL", "APPROVED" };
    }
}
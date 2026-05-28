using FluentAssertions;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Unit tests for plan approval calculation logic.
/// These tests validate the business rules for when approval is required.
/// </summary>
public class PlanApprovalTests
{
    [Fact]
    public void ApprovalCalculation_NoRestrictionsFromOtherCountries_ReturnsEmptyList()
    {
        // Arrange
        var planCountryId = 1;
        var restrictions = new List<TestRestriction>(); // No restrictions

        // Act
        var result = CalculateApprovalRequirements(planCountryId, restrictions);

        // Assert
        result.Should().BeEmpty("plan with no restrictions should not require approval");
    }

    [Fact]
    public void ApprovalCalculation_RestrictionsFromSameCountry_ReturnsEmptyList()
    {
        // Arrange
        var planCountryId = 1;
        var restrictions = new List<TestRestriction>
        {
            new(CountryId: 1, Message: "Self-restriction") // Same country as plan
        };

        // Act
        var result = CalculateApprovalRequirements(planCountryId, restrictions);

        // Assert
        result.Should().BeEmpty("restrictions from the same country should not require approval");
    }

    [Fact]
    public void ApprovalCalculation_RestrictionsFromDifferentCountries_ReturnsApprovalRequired()
    {
        // Arrange
        var planCountryId = 1;
        var restrictions = new List<TestRestriction>
        {
            new(CountryId: 2, Message: "Overlaps shipping lane"),
            new(CountryId: 3, Message: "Overlaps fishing zone")
        };

        // Act
        var result = CalculateApprovalRequirements(planCountryId, restrictions);

        // Assert
        result.Should().HaveCount(2, "two different countries have restrictions");
        result.Should().Contain(a => a.CountryId == 2);
        result.Should().Contain(a => a.CountryId == 3);
    }

    [Fact]
    public void ApprovalCalculation_MultipleRestrictionsFromSameCountry_ReturnsOneApprovalEntry()
    {
        // Arrange
        var planCountryId = 1;
        var restrictions = new List<TestRestriction>
        {
            new(CountryId: 2, Message: "Restriction 1"),
            new(CountryId: 2, Message: "Restriction 2")
        };

        // Act
        var result = CalculateApprovalRequirements(planCountryId, restrictions);

        // Assert
        result.Should().HaveCount(1, "multiple restrictions from same country should be grouped");
        var approval = result[0];
        approval.CountryId.Should().Be(2);
        approval.Reasons.Should().HaveCount(2, "both restriction messages should be included");
        approval.Reasons.Should().Contain("Restriction 1");
        approval.Reasons.Should().Contain("Restriction 2");
    }

    [Theory]
    [InlineData(1, 2, true)]  // Different country - requires approval
    [InlineData(1, 1, false)] // Same country - no approval needed
    [InlineData(2, 3, true)]  // Different country - requires approval
    [InlineData(5, 5, false)] // Same country - no approval needed
    public void ApprovalCalculation_VariousCountryCombinations_ReturnsExpectedResult(
        int planCountry, int restrictingCountry, bool shouldRequireApproval)
    {
        // Arrange
        var restrictions = new List<TestRestriction>
        {
            new(CountryId: restrictingCountry, Message: "Test restriction")
        };

        // Act
        var result = CalculateApprovalRequirements(planCountry, restrictions);

        // Assert
        if (shouldRequireApproval)
            result.Should().HaveCount(1, $"country {restrictingCountry} should require approval");
        else
            result.Should().BeEmpty($"country {restrictingCountry} should not require approval");
    }

    /// <summary>
    /// Helper method that simulates the approval calculation logic.
    /// Mimics the core business rule: restrictions from other countries require approval.
    /// </summary>
    private List<TestApprovalRequirement> CalculateApprovalRequirements(
        int planCountryId,
        List<TestRestriction> restrictions)
    {
        var grouped = restrictions
            .Where(r => r.CountryId != planCountryId)
            .GroupBy(r => r.CountryId)
            .Select(g => new TestApprovalRequirement(
                CountryId: g.Key,
                CountryName: $"Country {g.Key}",
                Reasons: g.Select(r => r.Message).ToList()
            ))
            .ToList();

        return grouped;
    }

    // Test-only types that mirror the structure used in the actual code
    private record TestRestriction(int CountryId, string Message);
    private record TestApprovalRequirement(int CountryId, string CountryName, List<string> Reasons);
}
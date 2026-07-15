using System.Text.Json;
using FluentAssertions;
using MSPChallenge_Client_Browser.Services;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

public class GameSessionStateEraTests
{
    private GameSessionState CreateTestState()
    {
        // Use a real WebSocket service instance; no connection is established in unit tests.
        var ws = new WebSocketService();
        return new GameSessionState(ws);
    }

    [Theory]
    [InlineData(0, 120, 0)]    // Month 0, era_total_months 120 → Era 0
    [InlineData(60, 120, 0)]   // Month 60, era_total_months 120 → Era 0
    [InlineData(119, 120, 0)]  // Month 119, era_total_months 120 → Era 0
    [InlineData(120, 120, 1)]  // Month 120, era_total_months 120 → Era 1
    [InlineData(240, 120, 2)]  // Month 240, era_total_months 120 → Era 2
    [InlineData(360, 120, 3)]  // Month 360, era_total_months 120 → Era 3
    [InlineData(480, 120, 3)]  // Month 480 (overflow), era_total_months 120 → Era 3 (clamped)
    [InlineData(600, 120, 3)]  // Month 600 (overflow), era_total_months 120 → Era 3 (clamped)
    public void GetCurrentEra_ReturnsCorrectEraIndex(int currentMonth, int eraTotalMonths, int expectedEra)
    {
        // Arrange
        var state = CreateTestState();
        state.GameEraTotalMonths = eraTotalMonths;
        state.ApplyGameLatest(JsonDocument.Parse($$"""
        {
            "month": {{currentMonth}}
        }
        """).RootElement);

        // Act
        var era = state.GetCurrentEra();

        // Assert
        era.Should().Be(expectedEra);
    }

    [Fact]
    public void ApplyGameLatest_ParsesPlanningEraRealtime_FromTickProperty()
    {
        // Arrange
        var state = CreateTestState();

        // Act
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "tick": {
                "planning_era_realtime": "7200,5400,3600,1800"
            }
        }
        """).RootElement);

        // Assert
        state.EraRealTimes.Should().Equal(7200, 5400, 3600, 1800);
    }

    [Fact]
    public void ApplyGameLatest_ParsesPlanningEraRealtime_FromRootProperty()
    {
        // Arrange
        var state = CreateTestState();

        // Act
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "planning_era_realtime": "10800,9000,7200,5400"
        }
        """).RootElement);

        // Assert
        state.EraRealTimes.Should().Equal(10800, 9000, 7200, 5400);
    }

    [Fact]
    public void ApplyGameLatest_ParsesPlanningEraRealtime_WithWhitespace()
    {
        // Arrange
        var state = CreateTestState();

        // Act
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "planning_era_realtime": " 3600 , 3600 , 3600 , 3600 "
        }
        """).RootElement);

        // Assert
        state.EraRealTimes.Should().Equal(3600, 3600, 3600, 3600);
    }

    [Fact]
    public void ApplyGameLatest_HandlesMissingPlanningEraRealtime()
    {
        // Arrange
        var state = CreateTestState();

        // Act
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "month": 100
        }
        """).RootElement);

        // Assert - should not throw, EraRealTimes should remain zeros
        state.EraRealTimes.Should().Equal(0, 0, 0, 0);
    }

    [Fact]
    public void ApplyGameLatest_HandlesPartialPlanningEraRealtime()
    {
        // Arrange
        var state = CreateTestState();

        // Act
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "planning_era_realtime": "7200,5400"
        }
        """).RootElement);

        // Assert - first two eras set, last two remain zero
        state.EraRealTimes.Should().Equal(7200, 5400, 0, 0);
    }

    [Fact]
    public async Task ResetAsync_ClearsEraRealTimes()
    {
        // Arrange
        var state = CreateTestState();
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "planning_era_realtime": "7200,5400,3600,1800"
        }
        """).RootElement);

        // Act
        await state.ResetAsync();

        // Assert
        state.EraRealTimes.Should().Equal(0, 0, 0, 0);
    }

    [Fact]
    public void GetCurrentEra_ReturnsZero_WhenEraTotalMonthsIsZero()
    {
        // Arrange
        var state = CreateTestState();
        state.ApplyGameLatest(JsonDocument.Parse("""
        {
            "month": 100
        }
        """).RootElement);

        // Act
        var era = state.GetCurrentEra();

        // Assert
        era.Should().Be(0);
    }

    [Theory]
    [InlineData(0, 120, 0, true)]    // Current era (0), current month 0 → editable
    [InlineData(0, 120, 60, true)]   // Current era (0), current month 60 → editable
    [InlineData(1, 120, 60, true)]   // Future era (1), current month 60 → editable
    [InlineData(0, 120, 120, false)] // Past era (0), current month 120 (era 1) → not editable
    [InlineData(0, 120, 240, false)] // Past era (0), current month 240 (era 2) → not editable
    [InlineData(2, 120, 240, true)]  // Current era (2), current month 240 → editable
    [InlineData(3, 120, 240, true)]  // Future era (3), current month 240 → editable
    public void EraEditability_BasedOnCurrentEra(int eraIndex, int eraTotalMonths, int currentMonth, bool expectedEditable)
    {
        // Arrange
        var state = CreateTestState();
        state.GameEraTotalMonths = eraTotalMonths;
        state.ApplyGameLatest(JsonDocument.Parse($$"""
        {
            "month": {{currentMonth}}
        }
        """).RootElement);

        var currentEra = state.GetCurrentEra();

        // Act
        var isEditable = eraIndex >= currentEra;

        // Assert
        isEditable.Should().Be(expectedEditable,
            $"Era {eraIndex} should be {(expectedEditable ? "editable" : "not editable")} when current era is {currentEra}");
    }
}

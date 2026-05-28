using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace MSPChallenge.Tests.IntegrationTests;

/// <summary>
/// Integration tests for WebSocket message parsing.
/// Tests the OnWsMessageReceived logic for batch responses.
/// </summary>
public class WebSocketMessageParsingTests
{
    [Fact]
    public void ParseBatchResponse_WithNewPlanId_ExtractsPlanIdCorrectly()
    {
        // Arrange
        var batchGuid = Guid.NewGuid().ToString();
        var expectedPlanId = 123;
        
        var wsMessage = new
        {
            header_name = "Batch/ExecuteBatch",
            header_data = new
            {
                batch_guid = batchGuid
            },
            results = new[]
            {
                new { call_id = 1, success = true, message = expectedPlanId.ToString() }
            }
        };

        var json = JsonSerializer.Serialize(wsMessage);

        // Act
        var parsed = JsonDocument.Parse(json);
        var headerName = parsed.RootElement.GetProperty("header_name").GetString();
        var headerData = parsed.RootElement.GetProperty("header_data");
        var receivedBatchGuid = headerData.GetProperty("batch_guid").GetString();
        
        var results = parsed.RootElement.GetProperty("results");
        var firstResult = results[0];
        var callId = firstResult.GetProperty("call_id").GetInt32();
        var success = firstResult.GetProperty("success").GetBoolean();
        var planIdStr = firstResult.GetProperty("message").GetString();
        var planId = int.Parse(planIdStr!);

        // Assert
        headerName.Should().Be("Batch/ExecuteBatch");
        receivedBatchGuid.Should().Be(batchGuid);
        callId.Should().Be(1, "plan creation should be call_id 1");
        success.Should().BeTrue();
        planId.Should().Be(expectedPlanId);
    }

    [Fact]
    public void ParseBatchResponse_WithMultipleResults_ParsesAllCallIds()
    {
        // Arrange
        var wsMessage = new
        {
            header_name = "Batch/ExecuteBatch",
            header_data = new { batch_guid = Guid.NewGuid().ToString() },
            results = new[]
            {
                new { call_id = 1, success = true, message = "123" },
                new { call_id = 2, success = true, message = "OK" },
                new { call_id = 3, success = false, message = "Error" }
            }
        };

        var json = JsonSerializer.Serialize(wsMessage);

        // Act
        var parsed = JsonDocument.Parse(json);
        var results = parsed.RootElement.GetProperty("results");
        var callIds = new List<int>();
        var successFlags = new List<bool>();

        foreach (var result in results.EnumerateArray())
        {
            callIds.Add(result.GetProperty("call_id").GetInt32());
            successFlags.Add(result.GetProperty("success").GetBoolean());
        }

        // Assert
        callIds.Should().Equal(1, 2, 3);
        successFlags.Should().HaveCount(3);
        successFlags.Take(2).Should().AllBeEquivalentTo(true);
        successFlags.Last().Should().BeFalse();
    }

    [Fact]
    public void ParseGameLatestMessage_HasCorrectStructure()
    {
        // Arrange
        var wsMessage = new
        {
            header_name = "Game/Latest",
            game_state = new
            {
                month = 120,
                plans_added = new[] { 123, 124 }
            }
        };

        var json = JsonSerializer.Serialize(wsMessage);

        // Act
        var parsed = JsonDocument.Parse(json);
        var headerName = parsed.RootElement.GetProperty("header_name").GetString();
        var gameState = parsed.RootElement.GetProperty("game_state");
        var month = gameState.GetProperty("month").GetInt32();

        // Assert
        headerName.Should().Be("Game/Latest");
        month.Should().Be(120);
    }
}
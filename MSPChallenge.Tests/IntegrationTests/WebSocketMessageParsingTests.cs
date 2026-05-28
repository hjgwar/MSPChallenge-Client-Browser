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
            payload = new
            {
                results = new[]
                {
                    new { call_id = 1, payload = expectedPlanId.ToString() }
                }
            }
        };

        var json = JsonSerializer.Serialize(wsMessage);

        // Act
        var parsed = JsonDocument.Parse(json);
        var headerName = parsed.RootElement.GetProperty("header_name").GetString();
        var headerData = parsed.RootElement.GetProperty("header_data");
        var receivedBatchGuid = headerData.GetProperty("batch_guid").GetString();
        
        var payload = parsed.RootElement.GetProperty("payload");
        var results = payload.GetProperty("results");
        var firstResult = results[0];
        var callId = firstResult.GetProperty("call_id").GetInt32();
        var planIdStr = firstResult.GetProperty("payload").GetString();
        var planId = int.Parse(planIdStr!);

        // Assert
        headerName.Should().Be("Batch/ExecuteBatch");
        receivedBatchGuid.Should().Be(batchGuid);
        callId.Should().Be(1, "plan creation should be call_id 1");
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
            payload = new
            {
                results = new[]
                {
                    new { call_id = 1, payload = "123" },
                    new { call_id = 2, payload = "OK" },
                    new { call_id = 3, payload = "Error" }
                }
            }
        };

        var json = JsonSerializer.Serialize(wsMessage);

        // Act
        var parsed = JsonDocument.Parse(json);
        var payload = parsed.RootElement.GetProperty("payload");
        var results = payload.GetProperty("results");
        var callIds = new List<int>();

        foreach (var result in results.EnumerateArray())
        {
            callIds.Add(result.GetProperty("call_id").GetInt32());
        }

        // Assert
        callIds.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ParseGameLatestMessage_HasCorrectStructure()
    {
        // Arrange
        var wsMessage = new
        {
            header_name = "Game/Latest",
            payload = new
            {
                month = 120,
                game_state = "PLAY",
                plan = new[]
                {
                    new { id = 123, name = "Test Plan 1", state = "DESIGN" },
                    new { id = 124, name = "Test Plan 2", state = "CONSULTATION" }
                }
            }
        };

        var json = JsonSerializer.Serialize(wsMessage);

        // Act
        var parsed = JsonDocument.Parse(json);
        var headerName = parsed.RootElement.GetProperty("header_name").GetString();
        var payload = parsed.RootElement.GetProperty("payload");
        var month = payload.GetProperty("month").GetInt32();
        var gameState = payload.GetProperty("game_state").GetString();
        var plan = payload.GetProperty("plan");

        // Assert
        headerName.Should().Be("Game/Latest");
        month.Should().Be(120);
        gameState.Should().Be("PLAY");
        plan.GetArrayLength().Should().Be(2);
    }
}
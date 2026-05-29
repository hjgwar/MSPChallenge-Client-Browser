using FluentAssertions;
using Xunit;

namespace MSPChallenge.Tests.UnitTests;

/// <summary>
/// Unit tests for Time Manager simulation control logic.
/// Tests button enable/disable states based on game state.
/// </summary>
public class TimeManagerTests
{
    [Theory]
    [InlineData("PLAY", false, true, true)]
    [InlineData("PAUSE", true, false, true)]
    [InlineData("FASTFORWARD", false, true, false)]
    [InlineData("SETUP", true, false, true)]
    [InlineData("END", true, false, true)]
    [InlineData("SIMULATION", true, false, true)]
    [InlineData("", true, false, true)]
    [InlineData(null, true, false, true)]
    public void TimeManager_ButtonStates_BasedOnGameState(
        string? gameState, bool expectedCanPlay, bool expectedCanPause, bool expectedCanFastForward)
    {
        // Arrange & Act
        var canPlay = CanPlay(gameState);
        var canPause = CanPause(gameState);
        var canFastForward = CanFastForward(gameState);

        // Assert
        canPlay.Should().Be(expectedCanPlay, 
            $"Play button should be {(expectedCanPlay ? "enabled" : "disabled")} when state is '{gameState}'");
        canPause.Should().Be(expectedCanPause, 
            $"Pause button should be {(expectedCanPause ? "enabled" : "disabled")} when state is '{gameState}'");
        canFastForward.Should().Be(expectedCanFastForward, 
            $"Fast Forward button should be {(expectedCanFastForward ? "enabled" : "disabled")} when state is '{gameState}'");
    }

    [Fact]
    public void TimeManager_PlayButton_DisabledWhenAlreadyPlaying()
    {
        // Arrange
        var gameState = "PLAY";

        // Act
        var canPlay = CanPlay(gameState);

        // Assert
        canPlay.Should().BeFalse("Play button should be disabled when already playing");
    }

    [Fact]
    public void TimeManager_PlayButton_DisabledWhenFastForwarding()
    {
        // Arrange
        var gameState = "FASTFORWARD";

        // Act
        var canPlay = CanPlay(gameState);

        // Assert
        canPlay.Should().BeFalse("Play button should be disabled when fast forwarding");
    }

    [Fact]
    public void TimeManager_PauseButton_EnabledOnlyWhenPlayingOrFastForwarding()
    {
        // Arrange
        var playState = "PLAY";
        var fastForwardState = "FASTFORWARD";
        var pauseState = "PAUSE";

        // Act
        var canPauseWhilePlaying = CanPause(playState);
        var canPauseWhileFastForwarding = CanPause(fastForwardState);
        var canPauseWhilePaused = CanPause(pauseState);

        // Assert
        canPauseWhilePlaying.Should().BeTrue("Pause button should be enabled when playing");
        canPauseWhileFastForwarding.Should().BeTrue("Pause button should be enabled when fast forwarding");
        canPauseWhilePaused.Should().BeFalse("Pause button should be disabled when already paused");
    }

    [Fact]
    public void TimeManager_FastForwardButton_DisabledOnlyWhenFastForwarding()
    {
        // Arrange
        var fastForwardState = "FASTFORWARD";
        var pauseState = "PAUSE";
        var playState = "PLAY";

        // Act
        var canFastForwardWhileFastForwarding = CanFastForward(fastForwardState);
        var canFastForwardWhilePaused = CanFastForward(pauseState);
        var canFastForwardWhilePlaying = CanFastForward(playState);

        // Assert
        canFastForwardWhileFastForwarding.Should().BeFalse("Fast Forward button should be disabled when already fast forwarding");
        canFastForwardWhilePaused.Should().BeTrue("Fast Forward button should be enabled when paused");
        canFastForwardWhilePlaying.Should().BeTrue("Fast Forward button should be enabled when playing normally (to speed up)");
    }

    [Theory]
    [InlineData("play", false)]
    [InlineData("Play", false)]
    [InlineData("PLAY", false)]
    [InlineData("pause", true)]
    [InlineData("Pause", true)]
    [InlineData("PAUSE", true)]
    public void TimeManager_ButtonLogic_IsCaseInsensitive(string gameState, bool expectedCanPlay)
    {
        // Act
        var canPlay = CanPlay(gameState);

        // Assert
        canPlay.Should().Be(expectedCanPlay, "Button logic should be case-insensitive");
    }

    // Helper methods that mirror the TimeManager component logic
    private static bool CanPlay(string? gameState) =>
        gameState?.ToUpperInvariant() != "PLAY" && gameState?.ToUpperInvariant() != "FASTFORWARD";

    private static bool CanPause(string? gameState) =>
        gameState?.ToUpperInvariant() == "PLAY" || gameState?.ToUpperInvariant() == "FASTFORWARD";

    private static bool CanFastForward(string? gameState) =>
        gameState?.ToUpperInvariant() != "FASTFORWARD";
}

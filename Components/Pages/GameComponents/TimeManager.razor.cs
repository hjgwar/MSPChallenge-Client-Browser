using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;
public partial class TimeManager : GameComponentBase, IDisposable
{
    private string[] _eraTimeInputs = new string[GameSessionService.ERA_COUNT];
    private bool[] _eraTimeEditing = new bool[GameSessionService.ERA_COUNT];

    private double _timelineProgress => CalculateTimelineProgress();
    
    private bool CanPlay => GameSessionService.GameState?.ToUpperInvariant() != "PLAY" && GameSessionService.GameState?.ToUpperInvariant() != "FASTFORWARD";
    
    private bool CanPause => GameSessionService.GameState?.ToUpperInvariant() == "PLAY" || GameSessionService.GameState?.ToUpperInvariant() == "FASTFORWARD";
    
    private bool CanFastForward => GameSessionService.GameState?.ToUpperInvariant() != "FASTFORWARD";

    protected override void OnInitialized()
    {
        // Subscribe to WebSocket state changes for immediate UI updates
        GameSessionService.Changed += OnGameSessionServiceChanged;
    }

    public void Dispose()
    {
        GameSessionService.Changed -= OnGameSessionServiceChanged;
    }

    private void OnGameSessionServiceChanged()
    {
        // Marshal back to Blazor UI thread and trigger re-render
        _ = InvokeAsync(StateHasChanged);
    }

    protected override void OnParametersSet()
    {
        if (_eraTimeInputs[0] == null)
        {
            // Initialize time inputs from GameSessionService
            for (int i = 0; i < GameSessionService.ERA_COUNT; i++)
            {
                _eraTimeInputs[i] = FormatTimeLeftForInput(GameSessionService.EraRealTimes[i]);
            }
        }
    }

    private string GetEraTimeDisplay(int eraIndex)
    {
        var currentEra = GameSessionService.GetCurrentEra();
        
        if (eraIndex == currentEra)
        {
            // Current era: show EraTimeLeft
            return FormatTimeLeft(GameSessionService.EraTimeLeft);
        }
        else if (eraIndex < currentEra)
        {
            // Past era: show 0:00:00
            return "0:00:00";
        }
        else
        {
            // Future era: show from EraRealTimes
            return FormatTimeLeft(GameSessionService.EraRealTimes[eraIndex]);
        }
    }

    private bool IsEraEditable(int eraIndex)
    {
        var currentEra = GameSessionService.GetCurrentEra();
        // Current and future eras are editable, past eras are not
        return eraIndex >= currentEra;
    }

    private string FormatTimeLeft(double seconds)
    {
        if (seconds <= 0) return "0:00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private string FormatTimeLeftForInput(double seconds)
    {
        if (seconds <= 0) return "0:00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void OnEraTimeClick(int eraIndex)
    {
        if (IsEraEditable(eraIndex))
        {
            _eraTimeEditing[eraIndex] = true;
            var currentEra = GameSessionService.GetCurrentEra();
            // Current era uses EraTimeLeft, future eras use EraRealTimes
            var timeValue = eraIndex == currentEra 
                ? GameSessionService.EraTimeLeft 
                : GameSessionService.EraRealTimes[eraIndex];
            _eraTimeInputs[eraIndex] = FormatTimeLeftForInput(timeValue);
        }
    }

    private async Task OnEraTimeInputChange(int eraIndex, ChangeEventArgs e)
    {
        if (e.Value is string input)
        {
            _eraTimeInputs[eraIndex] = input;
            
            // Parse the time input (format: H:MM:SS or HH:MM:SS)
            if (TryParseTimeInput(input, out var totalSeconds))
            {
                var currentEra = GameSessionService.GetCurrentEra();
                
                try
                {
                    if (eraIndex == currentEra)
                    {
                        // Current era: set current era time via api/Game/Realtime
                        var fields = new[] { new KeyValuePair<string, string>("realtime", totalSeconds.ToString()) };
                        await ApiClient.PostFormAsync("Game/Realtime", fields);
                    }
                    else
                    {
                        // Future era: update all era times via api/Game/FutureRealtime
                        var newEraTimes = new int[GameSessionService.ERA_COUNT];
                        Array.Copy(GameSessionService.EraRealTimes, newEraTimes, GameSessionService.ERA_COUNT);
                        newEraTimes[eraIndex] = totalSeconds;
                        var fields = new[] { new KeyValuePair<string, string>("realtime", string.Join(",", newEraTimes)) };
                        await ApiClient.PostFormAsync("Game/FutureRealtime", fields);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to set era realtime: {ex.Message}");
                }
            }
        }
    }

    private void OnEraTimeBlur(int eraIndex)
    {
        _eraTimeEditing[eraIndex] = false;
    }

    private bool TryParseTimeInput(string input, out int totalSeconds)
    {
        totalSeconds = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var parts = input.Split(':');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out var hours) || hours < 0) return false;
        if (!int.TryParse(parts[1], out var minutes) || minutes < 0 || minutes >= 60) return false;
        if (!int.TryParse(parts[2], out var seconds) || seconds < 0 || seconds >= 60) return false;

        totalSeconds = hours * 3600 + minutes * 60 + seconds;
        return true;
    }

    private double CalculateTimelineProgress()
    {
        var endMonth = GameSessionService.GameEndMonth > 0 
            ? GameSessionService.GameEndMonth 
            : GameSessionService.GameEraTotalMonths * GameSessionService.ERA_COUNT;
        if (endMonth <= 0) return 0;
        return Math.Clamp((double)GameSessionService.GameCurrentMonth / endMonth * 100.0, 0, 100);
    }

    private async Task OnSetGameStateAsync(string state)
    {
        try
        {
            var fields = new[] { new KeyValuePair<string, string>("state", state) };
            await ApiClient.PostFormAsync("Game/State", fields);
            // State will be updated via WebSocket, no need to manually update here
        }
        catch (Exception ex)
        {
            // Log error or show notification
            Console.WriteLine($"Failed to set game state: {ex.Message}");
        }
    }

    private async Task OnPlay() => await OnSetGameStateAsync("PLAY");
    private async Task OnPause() => await OnSetGameStateAsync("PAUSE");
    private async Task OnFastForward() => await OnSetGameStateAsync("FASTFORWARD");
}
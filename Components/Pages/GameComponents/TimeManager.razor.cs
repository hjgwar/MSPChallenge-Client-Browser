using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Services;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents
{
    public partial class TimeManager
    {
        [Inject] private GameSessionState GameSessionState { get; set; } = null!;
        [Inject] private SessionState SessionState { get; set; } = null!;
        [Inject] private MspApiClient ApiClient { get; set; } = null!;

        [Parameter] public bool IsVisible { get; set; }
        [Parameter] public EventCallback OnClose { get; set; }
        [Parameter] public EventCallback<string> OnSetGameState { get; set; }

        private string[] _eraTimeInputs = new string[GameSessionState.ERA_COUNT];
        private bool[] _eraTimeEditing = new bool[GameSessionState.ERA_COUNT];

        private double _timelineProgress => CalculateTimelineProgress();

        private bool CanPlay => GameSessionState.GameState?.ToUpperInvariant() != "PLAY" && GameSessionState.GameState?.ToUpperInvariant() != "FASTFORWARD";
        private bool CanPause => GameSessionState.GameState?.ToUpperInvariant() == "PLAY" || GameSessionState.GameState?.ToUpperInvariant() == "FASTFORWARD";
        private bool CanFastForward => GameSessionState.GameState?.ToUpperInvariant() != "FASTFORWARD";

        protected override void OnInitialized()
        {
            // Subscribe to WebSocket state changes for immediate UI updates
            GameSessionState.Changed += OnGameSessionStateChanged;
        }

        private void OnGameSessionStateChanged()
        {
            // Marshal back to Blazor UI thread and trigger re-render
            InvokeAsync(StateHasChanged);
        }

        protected override void OnParametersSet()
        {
            if (IsVisible && _eraTimeInputs[0] == null)
            {
                // Initialize time inputs from GameSessionState
                for (int i = 0; i < GameSessionState.ERA_COUNT; i++)
                {
                    _eraTimeInputs[i] = FormatTimeLeftForInput(GameSessionState.EraRealTimes[i]);
                }
            }
        }

        private string GetEraTimeDisplay(int eraIndex)
        {
            var currentEra = GameSessionState.GetCurrentEra();
            
            if (eraIndex == currentEra)
            {
                // Current era: show EraTimeLeft
                return FormatTimeLeft(GameSessionState.EraTimeLeft);
            }
            else if (eraIndex < currentEra)
            {
                // Past era: show 0:00:00
                return "0:00:00";
            }
            else
            {
                // Future era: show from EraRealTimes
                return FormatTimeLeft(GameSessionState.EraRealTimes[eraIndex]);
            }
        }

        private bool IsEraEditable(int eraIndex)
        {
            var currentEra = GameSessionState.GetCurrentEra();
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
                var currentEra = GameSessionState.GetCurrentEra();
                // Current era uses EraTimeLeft, future eras use EraRealTimes
                var timeValue = eraIndex == currentEra 
                    ? GameSessionState.EraTimeLeft 
                    : GameSessionState.EraRealTimes[eraIndex];
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
                    var currentEra = GameSessionState.GetCurrentEra();
                    
                    try
                    {
                        if (eraIndex == currentEra)
                        {
                            // Current era: set current era time via api/Game/Realtime
                            await ApiClient.SetRealtimeAsync(
                                SessionState.GameServerAddress,
                                SessionState.SessionId,
                                totalSeconds);
                        }
                        else
                        {
                            // Future era: update all era times via api/Game/FutureRealtime
                            var newEraTimes = new int[GameSessionState.ERA_COUNT];
                            Array.Copy(GameSessionState.EraRealTimes, newEraTimes, GameSessionState.ERA_COUNT);
                            newEraTimes[eraIndex] = totalSeconds;
                            var realtimeString = string.Join(",", newEraTimes);
                            await ApiClient.SetFutureRealtimeAsync(
                                SessionState.GameServerAddress,
                                SessionState.SessionId,
                                realtimeString);
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
            var endMonth = GameSessionState.GameEndMonth > 0 
                ? GameSessionState.GameEndMonth 
                : GameSessionState.GameEraTotalMonths * GameSessionState.ERA_COUNT;
            if (endMonth <= 0) return 0;
            return Math.Clamp((double)GameSessionState.GameCurrentMonth / endMonth * 100.0, 0, 100);
        }

        private async Task OnPlay() => await OnSetGameState.InvokeAsync("PLAY");
        private async Task OnPause() => await OnSetGameState.InvokeAsync("PAUSE");
        private async Task OnFastForward() => await OnSetGameState.InvokeAsync("FASTFORWARD");

        private void OnBackdropClick() => _ = OnClose.InvokeAsync();
    }
}
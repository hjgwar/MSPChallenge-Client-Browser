using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class GameTimeBar
{
    private PlanEntry? _planForNeedle;
    [Parameter] public int _selectedPlanId { get; set; }
    [Parameter] public PlanViewMode _planViewMode { get; set; }
    [Parameter] public EventCallback ToggleTimeManager { get; set; }
    [Parameter] public required Func<PlanViewMode, Task> SetPlanViewModeAsync { get; set; }

    protected override void OnParametersSet()
    {
        PlanForNeedle();
    }
    
    private double BarProgress => GameSessionState.TotalMonths > 0
        ? Math.Clamp((double) GameSessionState.GameCurrentMonth / GameSessionState.TotalMonths * 100.0, 0, 100)
        : 0;

    private void PlanForNeedle() {
        _planForNeedle = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == _selectedPlanId);
    }

    private string BarNeedle() {
        if (_planForNeedle is not null) {
            double _planNeedlePct = Math.Clamp(
                (double) _planForNeedle.StartDate / GameSessionState.TotalMonths * 100.0, 0, 100
            );
            return _planNeedlePct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
        return "0";
    }
}
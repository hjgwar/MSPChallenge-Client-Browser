using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class GameTimeViewControl
{
    [CascadingParameter] public PlanPanelsControl PlanPanelsController { get; set; } = null!;
    public bool TimeManagerVisible { get; private set; } = false;
    private PlanEntry? _planForNeedle;
    
    protected override void OnParametersSet()
    {
        PlanForNeedle();
    }

    public void ToggleTimeManager()
    {
        if (UserSessionService.IsAdmin)
            TimeManagerVisible = !TimeManagerVisible;
    }
    
    private double BarProgress => GameSessionState.TotalMonths > 0
        ? Math.Clamp((double) GameSessionState.GameCurrentMonth / GameSessionState.TotalMonths * 100.0, 0, 100)
        : 0;

    private void PlanForNeedle() {
        _planForNeedle = GameSessionState.Plans.FirstOrDefault(p => p.PlanId == GameSessionState.SelectedPlanId);
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
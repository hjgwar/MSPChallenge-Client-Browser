using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class GameTimeView
{
    [CascadingParameter] public PlanControl PlanController { get; set; } = null!;
    public bool TimeManagerVisible { get; private set; } = false;
    private PlanEntry? _planForNeedle;
    
    protected override void OnParametersSet()
    {
        PlanForNeedle();
    }

    public async Task SetPlanViewModeAsync(PlanViewMode mode)
    {
        if (PlanController.Map.MapJSModule is null || GameSessionState.SelectedPlanId == 0 || mode == GameSessionState.PlanViewMode) return;
        GameSessionState.PlanViewMode = mode;

        // Overlay is hidden only in Original mode.
        await PlanController.Map.MapJSModule.InvokeVoidAsync("setPlanOverlayVisible", mode != PlanViewMode.Original);

        // Referenced base layers are hidden only in ChangesOnly mode.
        bool showBase = mode != PlanViewMode.ChangesOnly;
        foreach (var planLayer in PlanController.SelectedPlan.Layers)
            if (!string.IsNullOrEmpty(planLayer.OriginalLayerId))
                await PlanController.Map.MapJSModule.InvokeVoidAsync("setLayerVisible", planLayer.OriginalLayerId, showBase);
        
        StateHasChanged();
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
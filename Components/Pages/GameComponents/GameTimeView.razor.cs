using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class GameTimeView
{
    [Parameter] public IJSObjectReference MapJSModule { get; set; } = null!;
    public bool TimeManagerVisible { get; private set; } = false;

    public async Task SetPlanViewModeAsync(PlanViewMode mode)
    {
        if (MapJSModule is null || GameSessionState.SelectedPlan is null || mode == GameSessionState.PlanViewMode) return;
        GameSessionState.PlanViewMode = mode;

        // Overlay is hidden only in Original mode.
        await MapJSModule.InvokeVoidAsync("setPlanOverlayVisible", mode != PlanViewMode.Original);

        // Referenced base layers are hidden only in ChangesOnly mode.
        bool showBase = mode != PlanViewMode.ChangesOnly;
        foreach (var planLayer in GameSessionState.SelectedPlan.Layers)
            if (!string.IsNullOrEmpty(planLayer.OriginalLayerId))
                await MapJSModule.InvokeVoidAsync("setLayerVisible", planLayer.OriginalLayerId, showBase);
        
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

    private string BarNeedle() {
        if (GameSessionState.SelectedPlan is not null) {
            double _planNeedlePct = Math.Clamp(
                (double) GameSessionState.SelectedPlan.StartDate / GameSessionState.TotalMonths * 100.0, 0, 100
            );
            return _planNeedlePct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
        return "0";
    }
}
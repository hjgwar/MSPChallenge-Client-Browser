using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
using MSPChallenge_Client_Browser.Utils;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class GameTimeView : IDisposable
{
    [Parameter] public IJSObjectReference MapJSModule { get; set; } = null!;
    protected override void OnInitialized()
    {
        base.OnInitialized();
        GameSessionState.Changed += OnStateChanged;
    }

    private void OnStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public async Task SetPlanViewModeAsync(PlanViewMode mode)
    {
        if (MapJSModule is null || GameSessionState.SelectedPlan is null || mode == GameSessionState.PlanViewMode) return;
        GameSessionState.PlanViewMode = mode;
        GameSessionState.NotifyChanged();

        // Plan overlay: visible in AfterChanges and ChangesOnly; hidden in Original.
        // Hiding the overlay is what produces the "pre-plan world state" in Original mode —
        // layer visibility itself does NOT change between Original and AfterChanges.
        await MapJSModule.InvokeVoidAsync("setPlanOverlayVisible", mode != PlanViewMode.Original);

        // Plan's own base layers — the map layers whose geometry this plan proposes to change.
        // These are always shown when viewing a plan (auto-shown, mirroring Unity's
        // UpdateVisibleLayersToPlan which calls ShowLayer() for every plan layer).
        var planBaseLayerIds = new HashSet<string>(
            GameSessionState.SelectedPlan.Layers
                .Where(l => !string.IsNullOrEmpty(l.OriginalLayerId))
                .Select(l => l.OriginalLayerId),
            StringComparer.OrdinalIgnoreCase);

        // IsBaseLayer (_PLAYAREA) layers are infrastructure — never touched.
        // For every other layer the target visibility depends on the mode:
        //
        //   AfterChanges / Original : honour the user's Visible preference PLUS auto-show
        //                             plan layers (matching Unity's UpdateVisibleLayersToPlan).
        //                             The only difference between these two modes is the overlay.
        //
        //   ChangesOnly             : show non-toggleable (always-on) layers only.
        //                             Extra user-activated non-plan layers are hidden so the
        //                             player sees nothing but the plan's proposed changes.
        //                             Plan base layers are also hidden; the overlay covers them.
        foreach (var layer in GameSessionState.LayerEntries.Where(l => !l.IsBaseLayer))
        {
            bool show = mode == PlanViewMode.ChangesOnly
                ? layer.Visible && (!layer.IsToggleable || !layer.Editable)
                : layer.Visible || planBaseLayerIds.Contains(layer.LayerId);
            await MapJSModule.InvokeVoidAsync("setLayerVisible", layer.LayerId, show);
        }

        StateHasChanged();
    }

    public void ToggleTimeManager()
    {
        if (UserSessionService.IsAdmin)
        {
            GameSessionState.TimeManagerOpen = !GameSessionState.TimeManagerOpen;
            GameSessionState.NotifyChanged();
        }
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

    public void Dispose()
    {
        GameSessionState.Changed -= OnStateChanged;
    }
}
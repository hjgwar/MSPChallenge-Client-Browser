using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelIssues : GameComponentBase
{
    [Parameter] public MapViewPort? Map { get; set; }
    [Parameter] public IReadOnlyList<PlanRestrictionIssue> SelectedPlanIssues { get; set; } = [];
    [Parameter] public EventCallback OnClose { get; set; }

    private async Task FocusPlanIssueAsync(PlanRestrictionIssue issue)
    {
        if (Map?.MapJSModule is null) return;
        await EnsureRestrictionLayersVisibleAsync(issue.SourceLayer, issue.TargetLayer);
        await Map.MapJSModule.InvokeVoidAsync("focusOnCoordinate", issue.MarkerX, issue.MarkerY);
    }

    private async Task EnsureRestrictionLayersVisibleAsync(string? sourceDisplayName, string? targetDisplayName)
    {
        bool changed = false;
        foreach (string? name in new[] { sourceDisplayName, targetDisplayName })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            Layer? le = GameSessionService.LayerEntries.FirstOrDefault(e =>
                string.Equals(e.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.LayerName,   name, StringComparison.OrdinalIgnoreCase));
            if (le is null || le.Visible || Map is null) continue;
            await Map.ToggleLayerAsync(le, true);
            changed = true;
        }
        if (changed) StateHasChanged();
    }
}
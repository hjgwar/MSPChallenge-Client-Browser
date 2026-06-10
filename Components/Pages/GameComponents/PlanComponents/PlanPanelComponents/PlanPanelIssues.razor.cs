using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelIssues
{
    [CascadingParameter] public PlanControl PlanPanelsController { get; set; } = null!;
    public List<PlanRestrictionIssue> SelectedPlanIssues { get; set; } = [];

    private async Task FocusPlanIssueAsync(PlanRestrictionIssue issue)
    {
        await EnsureRestrictionLayersVisibleAsync(issue.SourceLayer, issue.TargetLayer);
        await PlanPanelsController.Map.MapJSModule.InvokeVoidAsync("focusOnCoordinate", issue.MarkerX, issue.MarkerY);
    }

    private async Task EnsureRestrictionLayersVisibleAsync(string? sourceDisplayName, string? targetDisplayName)
    {
        bool changed = false;
        foreach (string? name in new[] { sourceDisplayName, targetDisplayName })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            LayerEntry? le = GameSessionState.LayerEntries.FirstOrDefault(e =>
                string.Equals(e.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.LayerName,   name, StringComparison.OrdinalIgnoreCase));
            if (le is null || le.Visible) continue;
            await PlanPanelsController.Map.ToggleLayerAsync(le, true);
            changed = true;
        }
        if (changed) StateHasChanged();
    }
}
    
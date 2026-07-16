using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanNameDesc : GameComponentBase
{
    [Parameter] public Plan? Plan { get; set; }
    [Parameter] public bool EditSaving { get; set; }
    [Parameter] public EventCallback<string> NameChanged { get; set; }
    [Parameter] public EventCallback<string> DescriptionChanged { get; set; }

    private string? _editName;
    private string? _editDescription;
    private bool _detailDescExpanded = false;
    private int? _lastInitialisedPlanId;

    protected override void OnParametersSet()
    {
        // Only reinitialise when the plan identity changes, not on routine re-renders,
        // so that text the user has typed isn't silently reverted mid-edit.
        if (Plan?.PlanId == _lastInitialisedPlanId) return;
        _lastInitialisedPlanId = Plan?.PlanId;

        _editName        = Plan?.Name;
        _editDescription = Plan?.Description;
    }

    private async Task OnNameChanged(ChangeEventArgs e)
    {
        _editName = e.Value?.ToString();
        if (NameChanged.HasDelegate)
            await NameChanged.InvokeAsync(_editName ?? string.Empty);
    }

    private async Task OnDescriptionChanged(ChangeEventArgs e)
    {
        _editDescription = e.Value?.ToString();
        if (DescriptionChanged.HasDelegate)
            await DescriptionChanged.InvokeAsync(_editDescription ?? string.Empty);
    }
}
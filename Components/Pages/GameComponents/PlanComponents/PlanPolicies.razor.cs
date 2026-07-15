using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanPolicies : GameComponentBase
{
    [Parameter] public Plan? Plan { get; set; }
    [Parameter] public bool EditSaving { get; set; }
    [Parameter] public HashSet<string> EditPolicyTypes { get; set; } = [];
    [Parameter] public EventCallback<HashSet<string>> EditPolicyTypesChanged { get; set; }
    [Parameter] public EventCallback OnSubPanelOpening { get; set; }

    private Plan _detailPlan => Plan ?? new Plan(
        0, string.Empty, string.Empty, Models.PlanState.DESIGN, 0, 0, 0,
        [], [], [], false, 0, 0, null, 0);
    
    private bool _editMode => GameSessionState.EditMode;
    private HashSet<string> _editPolicyTypes => EditPolicyTypes;
    private bool _policyPickerOpen = false;

    /// <summary>Closes this sub-panel. Called by PlanDetails when another panel is opened.</summary>
    public void CloseSubPanel()
    {
        _policyPickerOpen = false;
        StateHasChanged();
    }

    private async Task TogglePolicyPicker()
    {
        if (!_policyPickerOpen)
            await OnSubPanelOpening.InvokeAsync();
        _policyPickerOpen = !_policyPickerOpen;
        StateHasChanged();
    }

    private async Task OnTogglePolicyType(string policyType)
    {
        if (!_editPolicyTypes.Remove(policyType))
            _editPolicyTypes.Add(policyType);
        
        if (EditPolicyTypesChanged.HasDelegate)
            await EditPolicyTypesChanged.InvokeAsync(_editPolicyTypes);
        
        StateHasChanged();
    }
}
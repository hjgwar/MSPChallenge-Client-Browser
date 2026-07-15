using Microsoft.AspNetCore.Components;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents.PlanPanelComponents;

public partial class PlanPanelPolicyPicker : GameComponentBase
{
    [Parameter] public HashSet<string> SelectedPolicyTypes { get; set; } = [];
    [Parameter] public EventCallback<string> OnTogglePolicyType { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private HashSet<string> _editPolicyTypes => SelectedPolicyTypes;

    private async Task TogglePolicyType(string policyType)
    {
        if (OnTogglePolicyType.HasDelegate)
            await OnTogglePolicyType.InvokeAsync(policyType);
    }

    private async Task TogglePolicyPicker()
    {
        if (OnClose.HasDelegate)
            await OnClose.InvokeAsync();
    }
}
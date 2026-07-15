using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanMessages : GameComponentBase
{
    [Parameter] public Plan? Plan { get; set; }
    
    private bool _planMessagesOpen = false;
    private Plan? _detailPlan => Plan ?? GameSessionState.SelectedPlan;

    public void TogglePlanMessagesPanel()
    {
        _planMessagesOpen = !_planMessagesOpen;
        StateHasChanged();
    }
}
using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;
using MSPChallenge_Client_Browser.Services;
namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanPanelComponents;

public partial class PlanPanelState
{
    [CascadingParameter] private Game? GamePage { get; set; }

    
}
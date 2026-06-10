using Microsoft.AspNetCore.Components;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public class PlanComponentBase : GameComponentBase
{
    [CascadingParameter] public PlanControl PlanController { get; set; } = null!;
}
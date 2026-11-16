using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class OptionsPanel : GameComponentBase
{
    [Inject] NavigationManager NavigationManager { get; set; } = default!;
    [Inject] IJSRuntime JS { get; set; } = default!;
    
    private async Task OpenTutorialAsync()
    {
        GameUIStateService.ToggleOptionsPanel();
        var tourModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/tour.js");
        await tourModule.InvokeVoidAsync("initiateTutorial");
    }
}
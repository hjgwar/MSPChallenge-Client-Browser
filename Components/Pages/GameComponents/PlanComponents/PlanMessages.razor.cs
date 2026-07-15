using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents.PlanComponents;

public partial class PlanMessages : GameComponentBase, IDisposable
{
    [Parameter] public Plan? Plan { get; set; }
    [Parameter] public EventCallback OnSubPanelOpening { get; set; }
    
    private bool _planMessagesOpen = false;
    private Plan? _detailPlan => Plan ?? GameSessionState.SelectedPlan;

    protected override void OnInitialized()
    {
        // Subscribe so the panel re-renders whenever the WS tick delivers new messages,
        // even if the parent's parameter-diffing would otherwise short-circuit the render.
        GameSessionState.Changed += OnStateChanged;
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => GameSessionState.Changed -= OnStateChanged;

    /// <summary>Closes this sub-panel. Called by PlanDetails when another panel is opened.</summary>
    public void CloseSubPanel()
    {
        _planMessagesOpen = false;
        StateHasChanged();
    }

    public async Task TogglePlanMessagesPanel()
    {
        // Before opening, notify PlanDetails to close all other sub-panels.
        if (!_planMessagesOpen)
            await OnSubPanelOpening.InvokeAsync();
        _planMessagesOpen = !_planMessagesOpen;
        StateHasChanged();
    }
}
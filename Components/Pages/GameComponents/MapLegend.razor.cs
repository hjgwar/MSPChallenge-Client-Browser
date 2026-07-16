using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class MapLegend : IDisposable
{
    [Parameter] public Func<Layer, bool, Task> ToggleLayerAsync { get; set; } = null!;

    protected override void OnInitialized()
    {
        GameSessionState.Changed += OnStateChanged;
    }

    private void OnStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        GameSessionState.Changed -= OnStateChanged;
    }
}
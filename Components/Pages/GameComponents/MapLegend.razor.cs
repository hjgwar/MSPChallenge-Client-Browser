using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class MapLegend
{
    [Parameter] public Func<Layer, bool, Task> ToggleLayerAsync { get; set; } = null!;
    [Parameter] public Func<Task> SyncZIndicesAsync { get; set; } = null!;

    [JSInvokable]
    public async Task ReorderLegend(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= GameSessionState.LegendOrderLayerIds.Count || to >= GameSessionState.LegendOrderLayerIds.Count) return;
        var item = GameSessionState.LegendOrderLayerIds[from];
        GameSessionState.LegendOrderLayerIds.RemoveAt(from);
        GameSessionState.LegendOrderLayerIds.Insert(to, item);
        
        await SyncZIndicesAsync();
        StateHasChanged();
    }

}
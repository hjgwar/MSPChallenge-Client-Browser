using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class MapLegend
{
    [Parameter] public Func<LayerEntry, bool, Task> ToggleLayerAsync { get; set; } = null!;

    private List<LayerEntry> _legendOrder = [];

}
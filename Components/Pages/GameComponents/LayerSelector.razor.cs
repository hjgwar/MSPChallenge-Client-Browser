using Microsoft.AspNetCore.Components;
using MSPChallenge_Client_Browser.Models;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class LayerSelector
{    
    [Parameter] public Func<LayerEntry, bool, Task> ToggleLayerAsync { get; set; } = null!;
    private string _layerSearch    = string.Empty;
}
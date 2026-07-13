using Microsoft.AspNetCore.Components;

namespace MSPChallenge_Client_Browser.Components.Pages.GameComponents;

public partial class MapProperties
{
    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public double X { get; set; }
    [Parameter] public double Y { get; set; }
    [Parameter] public string? LayerName { get; set; }
    [Parameter] public IReadOnlyList<(string Label, string Value)> Properties { get; set; } = [];
    [Parameter] public EventCallback OnClose { get; set; }

    private async Task ClosePopupAsync()
    {
        await OnClose.InvokeAsync();
    }
}
